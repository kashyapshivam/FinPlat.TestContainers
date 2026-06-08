# Multi-Service SLTs

> Run **two or more real service containers** in a single test process, wired
> together over a shared Docker network, with Azurite, WireMock, and an nginx
> TLS proxy backing them. Use this when one service calls another (e.g. a
> worker → an HTTP API → shared storage) and a mock of the downstream is no
> longer good enough.

## When to reach for this

Use a multi-service SLT (instead of the single-app pattern in `skills.md`)
when **any** of the following is true:

* You need to verify the **real** HTTP contract between two of your services
  (request shape, auth headers, response handling). WireMock would let bugs
  through here.
* The downstream service writes state (queue messages, blobs, table rows) that
  the upstream must later read.
* Two services share the same Azurite (queue/blob/table) and you want to prove
  they cooperate — producer ↔ consumer, indexer ↔ reader, etc.

Concrete example shipped in `CFS.FinPlat.FinancialOrchestrator`:
**FO Worker → Collector.FD `/v1.0/ingestion` → Azurite blob + queue →
Collector.Orchestrator → Azurite `CollectorIndex` table**.

## The mental model

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Single docker network, brought up & torn down by the test process       │
│                                                                          │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐                                │
│  │ Azurite  │  │ WireMock │  │  nginx   │                                │
│  │ queues   │  │ external │  │ TLS-term │                                │
│  │ blobs    │  │ APIs     │  │ for      │                                │
│  │ tables   │  │ (graph,  │  │ Azurite  │                                │
│  └────┬─────┘  │ partner) │  │ + AAD    │                                │
│       │        └────┬─────┘  └────┬─────┘                                │
│       │             │             │                                      │
│       ▼             ▼             ▼                                      │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ App A                  App B                  App C                │  │
│  │ collector-fd           collector-orch         fo-worker            │  │
│  │ writes blob + msg ──► reads queue,            sends queue msg ──┐  │  │
│  │ POST /v1.0/ingestion   indexes,               processes order   │  │  │
│  │                        writes index row       calls App A ◄─────┘  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

Three things to internalise:

1. **Every app container speaks to Azurite & WireMock through the nginx TLS
   proxy via `*.azurite.local` hostnames.** Apps don't get raw connection
   strings — they use Token credential + `AZURE_AUTHORITY_HOST` pointed at the
   in-cluster mock AAD that WireMock signs.
2. **App-to-app calls are by container alias** (`http://collector-fd:8080`).
   The builder hands you `AppUrl(<alias>, <envVarName>)` to inject that URL
   into the caller's config.
3. **Storage is shared.** Two apps wired to the same queue/blob/table will
   read/write the same backing entity. This is the whole point of multi-service.

## Minimum viable two-app wiring

```csharp
_env = await new TestEnvironmentBuilder()
    .AddAzurite(opts =>
    {
        opts.UseTokenAuth = true;
        opts.ExternalCertificate = cert; // nginx cert+key (loaded from .slt/docker/nginx)
    })
    .AddMockApi("services", mock => { /* downstream HTTP mocks */ })

    // ── App A: the API that App B will call (collector-fd) ────────────────
    .AddApplication("collector-fd", app =>
    {
        app.FromDockerfile("docker/Dockerfile.fd.slt", contextPath: sltContext);
        app.WithInternalPort(8080);
        app.WithHttpHealthCheck("/v1.0/ping", timeoutSeconds: 120);
    })

    // ── App B: the caller (fo-worker) ─────────────────────────────────────
    .AddApplication("fo-worker", app =>
    {
        app.FromDockerfile("docker/Dockerfile.slt", contextPath: sltContext);
        app.WithEnv("Environment", "Local");
        app.WithEnv("WorkerNames", "BigCatProductQueueWorkerV1");
    })

    // ── Wire each app individually ────────────────────────────────────────
    .Wire("collector-fd", wire => wire
        .Blob("bigcatproduct-v3")
        .Queue("bigcatproduct-v3")
        .Table("CollectorIndex")
        .UseTokenAuth())                          // ← read the pitfalls below
    .Wire("fo-worker", wire => wire
        .Queue("bigcatproductv1")
        .Table("CollectorIndex")
        .Blob("featureflag")
        .UseTokenAuth()
        .AppUrl("collector-fd", "CollectorUri"))  // ← App B learns App A's URL

    .BuildAsync();
```

### What `AppUrl(<peer>, <envVar>)` does
Sets the env var on **this** container to `http://<peer>:<port>` (the alias
the docker network resolves). At runtime your service reads the env var via
`IConfiguration` and points its `HttpClient` at the real peer container —
not at a WireMock stub.

## Seeding state before the test runs

Most multi-service flows need shared state preloaded — e.g. FO will look up
billing records that Collector must have indexed before the order arrives.
Use `.Seed(...)` (chained on the builder, after `.Wire(...)`):

```csharp
.Seed(s => s
    .HttpPostFile(
        targetApp: "collector-fd",
        path: "/v1.0/ingestion",
        fixturePath: Path.Combine("Scenarios", "BigCatProduct", "BillingGroupSummary-Preseed.json"),
        configure: opts => opts
            .WaitUntilHttpGet("collector-fd", "/v1.0/ping")
            .WithHeader("Authorization", $"Bearer {MintFakeBearer()}"),
        name: "Seed BG-001")
    .WaitForTableQuery(
        tableName: "CollectorIndex",
        filter: "PartitionKey eq 'bg-001'",
        minMatchingRows: 1,
        timeout: TimeSpan.FromSeconds(120),
        name: "Wait for BG-001 index row"))
```

### Why POST-then-wait-on-table is the canonical seed step
Collector indexes rows with a `SHA256(JObject)`-derived RowKey and writes
its journal blob under a timestamped path — both **effectively impossible
to reproduce** by writing directly to Azurite. POST through the real
ingestion endpoint, then poll the destination table until the indexer's
side-effect lands. A 2xx response alone does **not** prove persistence —
many indexers run async after the response is sent.

## Pitfalls we hit (and the fixes)

These are the actual surprises encountered shipping the FO+Collector SLT.
Read each one before debugging the symptom.

### 1. Orchestrator silently fails to write the index row → seed times out

**Symptom:** `WaitForTableQuery` times out, `CollectorIndex` table has every
other row except the one you preseeded. No exception bubbles out of any
container.

**Root cause:** Collector.Orchestrator's `BatchPostProcessor` wraps the
indexer call in a `try/catch` that **logs** the exception but does **not**
rethrow. So a malformed fixture causes the indexer to throw, the post
processor logs it, message is `Complete`-d, **and nothing ever shows up in
the index table.**

**How to find it:** A timeout on `WaitForTableQuery` triggers the framework's
diagnostic dump — it writes each app container's full logs to
`%TEMP%\slt-logs\<appname>.log`. Search those logs for
`"adding primary indexes"` / `"NullReferenceException"` / `BatchPostProcessor`.

**Concrete instance:**
`BillingRecordSummaryIndexedProperties.GetLineItemIds()` does
`lineItem?.SelectToken("id").SelectToken("id")?.Value<string>()` — note the
**missing `?.` after the first `SelectToken`**. So `line_items[*].id` MUST
be a `JObject { "id": "<guid>" }`, not a string and not absent. Fix:

```json
"line_items": [
  {
    "id": { "id": "0fae9ea7-3ad6-4f8a-9cf0-1f7a4a8a9e22" },
    "billing_line_item_id": "0",
    "sku": "CFQ7TTC0LH0R-0001"
  }
]
```

### 2. Orchestrator container needs `UseTokenAuth()` even though it never sees a user

Connection strings won't work. Collector.Orchestrator's storage adapters
always go through `TokenCredential`. If you wire it without `.UseTokenAuth()`
the workers boot, idle on the queue, but throw on first storage call.
Same for Collector.FD — its `BatchStorageRepositoryContainer` constructor
requires `ITokenProvider`.

**Fix:** Always end the `.Wire(...)` for any Collector-family container with
`.UseTokenAuth()`. The framework injects `AZURE_AUTHORITY_HOST` pointing at
the local mock AAD, which signs tokens Azurite (in token-auth mode) accepts.

### 3. Default `WorkerNames` won't run the workers you need

Both Collector.Orchestrator and FO Worker decide what to run by reading the
`WorkerNames` env var. Default config typically enables a different set
than what your scenario exercises. Be explicit:

```csharp
.AddApplication("collector-orch", app =>
{
    app.WithEnv(
        "WorkerNames",
        "BillingGroupEventGridQueueWorkerV20190531," +
        "BillingEventGridQueueWorkerV20191015," +
        "BigCatProductEventGridQueueWorkerV3");
})
```

Pick the **EventGridQueueWorker** variants (they consume the queue
messages FD enqueues), not the `TableIndex*` variants (those are a
secondary projection path you don't need for primary-index assertions).

### 4. Auth: `EnableS2SAuthentication=false` is your friend

Real S2S auth needs a real AAD tenant. The SLT Collector config sets
`EnableS2SAuthentication=false`, in which case
`Microsoft.Crs.Financials.AspNetCore.GetClientDetails()` **parses but does
not validate** the bearer JWT — it just reads the `appid` and `oid` claims.
So you can mint an unsigned (or fake-signed) JWT with the right claims:

```csharp
private static string MintFinancialOrchestratorBearerToken()
{
    var handler = new JwtSecurityTokenHandler();
    var token = handler.CreateJwtSecurityToken(
        issuer: "https://login.microsoftonline.com/.../v2.0",
        audience: CollectorResourceId,
        subject: new ClaimsIdentity(new[]
        {
            new Claim("appid", FoPartnerAppId),
            new Claim("oid",   FoPartnerObjectId),
        }),
        expires: DateTime.UtcNow.AddHours(1));
    return handler.WriteToken(token);
}
```

The same `Bearer <token>` goes on every preseed POST via
`.WithHeader("Authorization", ...)`. Pick the `appid` of a partner that
your Collector config actually has registered.

### 5. Per-app health check timeouts

Collector.Orchestrator boots **noticeably slower** than FD because it spins
up N workers, each with its own queue listener and indexer dependencies.
Bump its `WithHttpHealthCheck` timeout:

```csharp
app.WithHttpHealthCheck("/v1.0/ping", timeoutSeconds: 120); // FD
app.WithHttpHealthCheck("/monitor",   timeoutSeconds: 180); // Orch
```

If you skip the health check, the seed can fire before the worker has
attached its queue listener — and the seed POST will 200, the message will
land in the queue, and then sit there forever because nobody is listening.

### 6. The diagnostic suite is intentional — let it run

On any `WaitForTableQuery` timeout, the framework will dump:

* Rows in the target table (up to 50)
* All other tables in Azurite (with approx row counts)
* All queues and their approx message counts
* All blob containers and per-container blob counts
* **Full log file** for every app container at `%TEMP%\slt-logs\<appname>.log`
* Tail (last 4000 chars) of each app's logs inline in the test output

This is your debugger when a seed times out. Don't try to write your own —
it's already there. The full per-app log files are the high-signal artifact;
the inline tail is just a hint to push you toward them.

## Quick recipe: adding a third service

1. **`AddApplication("svc-c", ...)`** — point at its dockerfile, set env
   vars, add a health check.
2. **`Wire("svc-c", ...)`** — declare every queue/blob/table it touches.
   End with `.UseTokenAuth()` if it talks to Azurite by TokenCredential.
3. If `svc-c` calls another app, add `.AppUrl("peer-alias", "PeerUrlEnvVar")`.
4. If another app calls `svc-c`, add the symmetric `AppUrl` on that app's
   wiring.
5. If `svc-c` needs preloaded state, add a `.Seed(...)` step — almost always
   an `HttpPostFile` + `WaitForTableQuery` pair.

## See also

* `skills.md` — single-app SLT primer
* `README.md` — full builder API reference
* `.github/skills/fo-slt-authoring.md` — agent skill for **single-service**
  FO worker SLTs
* `.github/skills/multi-service-collector-slt.md` — agent skill for the
  **two-service FO + Collector** flow this document describes
