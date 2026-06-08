# Skill: Authoring Multi-Service SLTs (FO + Collector and friends)

## Overview

This skill teaches an AI agent how to author a **multi-service Service Level
Test** for FinPlat services using `FinPlat.TestContainers` — specifically, the
pattern where **FO Worker + Collector.FD + Collector.Orchestrator** (or any
similar caller / receiver / background-indexer trio) run as real Docker
containers in the same test process and cooperate over a shared Azurite.

Companion to `fo-slt-authoring.md` (which covers single-service FO worker
SLTs). Read that one first — this skill assumes the reader already knows the
single-service pattern, the `EventEntityV1` envelope, the catch-all WireMock
discipline, and the `_env.Queue/Blob/Table` assertion APIs.

**Reference implementation:**
`CFS.FinPlat.FinancialOrchestrator/tests/FinancialOrchestrator.SltTests/FullFlowSltTests.cs`
— the proven, passing end-to-end test that exercises this skill in
production. When in doubt, mirror what that file does.

**Library:** `FinPlat.TestContainers`
**Target framework:** .NET 8.0+
**Test framework:** MSTest

---

## When to use this skill

Use a multi-service SLT — not a single-service one with WireMock — when **any**
of these are true:

1. The thing you're testing is the **real HTTP contract** between two of your
   services (auth headers, request body shape, response codes / payloads). A
   WireMock stub of the receiver hides receiver bugs and contract drift.
2. The downstream service writes state (queue messages, blobs, table rows)
   that the upstream service later reads. WireMock can't model that
   round-trip.
3. You want to verify a chain of background processing — e.g. Service A
   accepts a POST, writes a queue message; Service B consumes the queue,
   indexes a table; Service C reads the table on the next request.

The FO + Collector flow hits all three.

---

## The architecture you're targeting

```
┌──────────────────────────────────────────────────────────────────────────┐
│  MSTest Process — owns the docker network, owns the lifecycle            │
│                                                                          │
│  ┌────────┐  ┌────────┐  ┌────────────────────────┐                     │
│  │Azurite │  │WireMock│  │ nginx (TLS terminator) │                     │
│  │queue/  │  │external│  │ *.azurite.local        │                     │
│  │blob/   │  │APIs    │  │ login.microsoftonline. │                     │
│  │table   │  │        │  │ com (mock AAD)         │                     │
│  └───┬────┘  └───┬────┘  └────────────┬───────────┘                     │
│      │           │                    │                                  │
│      ▼           ▼                    ▼                                  │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ App A: collector-fd  ──── /v1.0/ingestion ◄────── App C: fo-w   │    │
│  │   writes blob to ─────────────► billing-2019-10-15 container    │    │
│  │   enqueues msg to ───────────► billing-2019-10-15 queue ──┐     │    │
│  │                                                            │     │    │
│  │ App B: collector-orch                                      │     │    │
│  │   workers consume queue ◄──────────────────────────────────┘     │    │
│  │   read blob via TokenCredential                                  │    │
│  │   BatchPostProcessor → IndexRepository                           │    │
│  │   writes primary row to CollectorIndex table                     │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│                                                                          │
│  Test:                                                                   │
│   1. Preseed via HTTP POST to App A (using a minted Bearer JWT)          │
│   2. Wait on the CollectorIndex table until App B indexes the preseed    │
│   3. Drop a real order onto fo-worker's queue                            │
│   4. Assert: real journal blob lands in Azurite via the full A↔B↔C path  │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Recipe (do these in order)

### Step 1 — Confirm there are real services to stand up

You need:

* A buildable Dockerfile for each service that runs in Azure today. Look in
  `.slt/docker/` for `Dockerfile.*.slt` files. If one doesn't exist for a
  receiver/orchestrator service, the upstream PR adding it is a prerequisite.
* A `.slt/docker/<svc>-config/config.docker.json` for any service that loads
  config from disk. The SLT config should toggle off real S2S auth
  (`EnableS2SAuthentication=false`) and point storage at `azurite.local`.
* Internal port numbers each service listens on (usually 8080).
* The health-check path each service exposes (FD: `/v1.0/ping`,
  Orchestrator: `/monitor`, FO Worker: typically none — workers don't expose
  HTTP).

If any of those are missing, stop and tell the user what's needed before
writing test code.

### Step 2 — Set up the test class skeleton

Use **MSTest**, `[ClassInitialize]` for environment build, `[ClassCleanup]`
for teardown. One `TestEnvironment` instance per test class (containers are
expensive to start). Mirror this exactly:

```csharp
[TestClass]
[TestCategory("SLT")]
[TestCategory("FullFlow")]
public class FullFlowSltTests
{
    private const string MockServicesAlias = "mock-services:8080";
    private const string CollectorIndexTable = "CollectorIndex";
    private const string JournalBlobContainer = "bigcatproduct-v3";

    // IDs must match what's in your Order_*.json fixture so the lookups
    // FO performs at runtime hit the rows the preseed wrote.
    private const string BillingGroupId  = "bg-001";
    private const string BillingRecordId = "b2f482fa-4691-452d-9b89-3d1af3e1035c";

    private static TestEnvironment _env = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        // ... see Step 3+
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_env != null) await _env.DisposeAsync();
    }
}
```

### Step 3 — Mint a Bearer JWT for the preseed calls

The SLT Collector runs with `EnableS2SAuthentication=false`. In that mode,
`Microsoft.Crs.Financials.AspNetCore.GetClientDetails()` **parses but does
not validate** the JWT — it only reads `appid` and `oid` claims. So you can
mint an unsigned JWT carrying the right partner identity:

```csharp
private const string FoPartnerAppId      = "33349fe2-44d3-47b3-b8c7-9bf8279cdf6b";
private const string FoPartnerObjectId   = "d8a24b41-d537-47bb-95c6-f5aa46ead300";
private const string CollectorResourceId = "b37f1adf-0d67-46e6-b0ea-d3981e8fc494";

private static string MintFinancialOrchestratorBearerToken()
{
    var handler = new JwtSecurityTokenHandler();
    var token = handler.CreateJwtSecurityToken(
        issuer: "https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0",
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

**The `appid` MUST match a partner registered in the SLT Collector config.**
Look in `.slt/docker/fd-config/Configuration/config.docker.json` →
`PartnerConfiguration` array. Pick one with permissions for the event types
you're seeding. For FO flows, use the `FinancialOrchestrator` partner.

### Step 4 — Build the environment: containers + wiring + seed

This is one big builder chain. Walk through it sequentially:

```csharp
var sltContext = GetSltContext();
var cert = new CertificateMaterial(
    await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.crt")),
    await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.key")));
var foAuthorizationHeader = $"Bearer {MintFinancialOrchestratorBearerToken()}";

_env = await new TestEnvironmentBuilder()
    // ── 4a. Infrastructure ────────────────────────────────────────────
    .AddAzurite(opts =>
    {
        opts.UseTokenAuth = true;             // ALL apps need token auth, not connection strings
        opts.ExternalCertificate = cert;
    })
    .AddMockApi("services", mock =>
    {
        mock.OnGet("/v8.0/products/CFQ7TTC0H9MP/0001", 200, catalogResponse);
        mock.OnAny("/billinggroups", 200, """[{ "fedGovSAP": 1 }]""");
        mock.OnUnmatched(200, """[]""");      // catch-all is essential
    })

    // ── 4b. Real services (3 containers) ─────────────────────────────
    .AddApplication("collector-fd", app =>
    {
        app.FromDockerfile("docker/Dockerfile.fd.slt", contextPath: sltContext);
        app.WithInternalPort(8080);
        app.WithHttpHealthCheck("/v1.0/ping", timeoutSeconds: 120);
    })
    .AddApplication("collector-orch", app =>
    {
        app.FromDockerfile("docker/Dockerfile.orch.slt", contextPath: sltContext);
        app.WithInternalPort(8080);
        // Pick EventGridQueueWorkers (consume queue, run BatchPostProcessor → primary index).
        // Do NOT use TableIndex*Worker variants — those are a different projection path.
        app.WithEnv("WorkerNames",
            "BillingGroupEventGridQueueWorkerV20190531," +
            "BillingEventGridQueueWorkerV20191015," +
            "BigCatProductEventGridQueueWorkerV3");
        app.WithHttpHealthCheck("/monitor", timeoutSeconds: 180);  // boots slower than FD
    })
    .AddApplication("fo-worker", app =>
    {
        app.FromDockerfile("docker/Dockerfile.slt", contextPath: sltContext);
        app.WithEnv("Environment", "Local");
        app.WithEnv("WorkerNames", "BigCatProductQueueWorkerV1");
        app.WithEnv("DisplayCatalogUri", $"http://{MockServicesAlias}/displaycatalog");
        app.WithEnv("BillingGroupUri",   $"http://{MockServicesAlias}/billinggroups");
    })

    // ── 4c. Wire each app to shared storage + peers ──────────────────
    .Wire("collector-fd", wire => wire
        .Blob(JournalBlobContainer)
        .Blob("billing-group-2019-05-31")
        .Blob("billing-2019-10-15")
        .Queue("billing-group-2019-05-31")
        .Queue("billing-2019-10-15")
        .Queue("bigcatproduct-v3")
        .Table(CollectorIndexTable)
        .UseTokenAuth())                                  // MUST be UseTokenAuth, not connection string
    .Wire("collector-orch", wire => wire
        .Blob(JournalBlobContainer)
        .Blob("billing-group-2019-05-31")
        .Blob("billing-2019-10-15")
        .Queue("billing-group-2019-05-31")
        .Queue("billing-2019-10-15")
        .Queue("bigcatproduct-v3")
        .Table(CollectorIndexTable)
        .UseTokenAuth())                                  // same — workers go through TokenCredential
    .Wire("fo-worker", wire => wire
        .Queue("bigcatproductv1")
        .Table(CollectorIndexTable)
        .Blob("featureflag")
        .Blob("catalogcachedatacfq7ttc0h9mp0001cfq7ttc0kq2z")
        .Blob("bigcatproductv4")
        .UseTokenAuth()
        .AppUrl("collector-fd", "CollectorUri"))          // fo-worker LEARNS where collector-fd is

    // ── 4d. Preseed Collector with parent entities FO will look up ───
    .Seed(s => s
        .HttpPostFile(
            targetApp:   "collector-fd",
            path:        "/v1.0/ingestion",
            fixturePath: Path.Combine("Scenarios", "BigCatProduct", "BillingGroupSummary-Preseed.json"),
            configure:   opts => opts
                .WaitUntilHttpGet("collector-fd", "/v1.0/ping")
                .WithHeader("Authorization", foAuthorizationHeader),
            name: $"BillingGroupSummary {BillingGroupId}")
        .WaitForTableQuery(
            tableName:        CollectorIndexTable,
            filter:           $"PartitionKey eq '{BillingGroupId}'",
            minMatchingRows:  1,
            timeout:          TimeSpan.FromSeconds(120),
            name:             $"Wait for BillingGroupSummary {BillingGroupId} index row")
        .HttpPostFile(
            targetApp:   "collector-fd",
            path:        "/v1.0/ingestion",
            fixturePath: Path.Combine("Scenarios", "BigCatProduct", "BillingRecordSummary-Preseed.json"),
            configure:   opts => opts.WithHeader("Authorization", foAuthorizationHeader),
            name: $"BillingRecordSummary {BillingRecordId}")
        .WaitForTableQuery(
            tableName:        CollectorIndexTable,
            filter:           $"PartitionKey eq '{BillingRecordId}'",
            minMatchingRows:  1,
            timeout:          TimeSpan.FromSeconds(120),
            name:             $"Wait for BillingRecordSummary {BillingRecordId} index row"))
    .BuildAsync();
```

### Step 5 — Build the preseed fixtures (the most error-prone part)

Each `HttpPostFile` consumes a JSON file shaped as a Collector ingestion
request. The minimal shape:

```json
{
  "Events": [
    {
      "Content": {
        "id":               "<must-match-the-PK-you-wait-on-below>",
        "accountId":        "<guid>",
        "version":          1,
        "updatedTimestamp": "2024-01-01T00:00:00Z"
      },
      "ContentType": "json",
      "EventId":     "<usually-same-as-Content.id>",
      "EventType":   "BillingGroupSummary",
      "Properties":  {}
    }
  ],
  "JournalVersion": "billing-group-2019-05-31",
  "RequestId":      "slt-preseed-bg-001"
}
```

**`Content.id` becomes the `PartitionKey` of the index row.** Match it to the
ID you put in your `Order_*.json` so FO's lookups hit your preseed.

**Pick `JournalVersion` from the matching journal name** — see the Collector
config or grep the Collector source for `JournalVersion =`. Each
`BillingRecord` event type maps to one journal name; using the wrong one
returns 404.

### Step 6 — Write the actual test

```csharp
[TestMethod]
public async Task FullFlow_BigCatOrder_CollectorWritesJournalBlobs()
{
    var orderJson = await ReadScenarioFileAsync("Order_Creation_Closed.json");

    // Wrap in EventEntityV1 envelope, base64-encode — see fo-slt-authoring.md
    await _env.Queue("bigcatproductv1").SendAsync(WrapInEventEntity(orderJson));

    await WaitForProcessingAsync(async () =>
    {
        var containers = await _env.Storage().ListContainersAsync();
        Assert.IsTrue(containers.Count > 0);
        int totalBlobs = 0;
        foreach (var c in containers)
            totalBlobs += (await _env.Storage().Blobs(c).ListBlobsAsync()).Count;
        Assert.IsTrue(totalBlobs > 0);
    }, timeoutSeconds: 90);
}
```

---

## Pitfalls (the failure modes you WILL hit)

Each pitfall below is a real bug encountered shipping this test. Read them
**before** debugging.

### Pitfall 1 — Indexer silently swallows malformed fixture data

**Symptom:** Seed step `WaitForTableQuery` times out for the
`BillingRecordSummary` (or any non-trivial entity). Container logs in the
test output look healthy. The other index rows appear, just not yours.

**Why:** `Collector.Orchestrator.Processors.Batch.BatchPostProcessor` wraps
the index write in a `try/catch` that logs but does not rethrow. So if the
indexer NREs on bad fixture data, the message is `Complete`-d, no row
appears, and only a log line in `collector-orch.log` betrays it.

**How to find it:** When seed times out, the framework writes per-app full
logs to `%TEMP%\slt-logs\<appname>.log`. Open `collector-orch.log` and grep:

```powershell
Select-String -Path "$env:TEMP\slt-logs\collector-orch.log" `
    -Pattern "adding primary indexes|NullReferenceException|BatchPostProcessor"
```

**Common offender — `line_items` shape:**
`BillingRecordSummaryIndexedProperties.GetLineItemIds()` is
`lineItem?.SelectToken("id").SelectToken("id")?.Value<string>()` — missing
`?.` after the first `SelectToken`. So `line_items[*].id` MUST be a JObject:

```json
"line_items": [
  {
    "id": { "id": "0fae9ea7-3ad6-4f8a-9cf0-1f7a4a8a9e22" },
    "billing_line_item_id": "0",
    "sku": "CFQ7TTC0LH0R-0001",
    "product_id": "CFQ7TTC0LH0R"
  }
]
```

**General fix recipe:** find the indexer for the event type (grep
`Collector.Models\Index\<EventType>IndexedProperties.cs`), read each
`Get*Ids` / `Get*Properties` method, mirror the exact JSON shape it expects.

### Pitfall 2 — `JournalVersion` 404

**Symptom:** Preseed POST returns 404 immediately. Collector logs say
`Journal version 'X' not found`.

**Why:** `JournalVersion` is a Collector-internal route key, not a date and
not the EventType. The right value is the journal name configured for that
event type, e.g. `billing-2019-10-15`, `billing-group-2019-05-31`,
`bigcatproduct-v3`.

**Fix:** Open `.slt/docker/fd-config/Configuration/config.docker.json`, find
the `Journals` block, copy the `Name` of the journal that handles your
event type. Use that exact string.

### Pitfall 3 — `UseTokenAuth()` missing on Collector wiring → workers boot but throw on first storage call

**Symptom:** Containers start, health checks pass, seed POST returns 200, no
index row ever appears. Log says
`InvalidOperationException: Unable to resolve service for type 'ITokenProvider'`
or `AuthenticationFailed` on the first blob read.

**Why:** Collector.FD's `BatchStorageRepositoryContainer` and
Collector.Orchestrator's storage adapters always go through `TokenCredential`.
They will not accept a connection string.

**Fix:** Every `.Wire(...)` for a Collector-family container MUST end with
`.UseTokenAuth()`. The framework injects `AZURE_AUTHORITY_HOST` pointing at
the local mock AAD which signs tokens Azurite accepts.

### Pitfall 4 — Default WorkerNames runs the wrong projection

**Symptom:** FD writes the blob and enqueues the message; queue is non-empty
forever; no index row appears.

**Why:** Default `WorkerNames` in `config.docker.json` may not include the
`*EventGridQueueWorker*` worker that drives the `BatchPostProcessor` →
`IndexRepository` path. Without it, nobody is consuming the queue.

**Fix:** Set `WorkerNames` env var explicitly to the EventGridQueueWorker
for each journal you're testing. Verify by checking the queue's approx
message count drops to 0 within a few seconds of the preseed POST.

### Pitfall 5 — Health check timeouts too aggressive for Orchestrator

**Symptom:** Build fails with `Container collector-orch failed to become healthy`.

**Why:** Orchestrator spins up N queue workers each with their own
dependencies — it takes longer than 60 seconds in CI on a cold image.

**Fix:** `app.WithHttpHealthCheck("/monitor", timeoutSeconds: 180);`

### Pitfall 6 — Pre-existing Azurite blobs from a prior run pass the assertion

**Symptom:** Test reports `totalBlobs > 0` but inspection shows the blobs
are from yesterday's run. Real flow may have failed silently.

**Why:** The framework does not wipe Azurite between test classes.

**Fix:** Either (a) tear down the Azurite volume in `ClassCleanup`, or
(b) assert on a stricter shape — blob name contains a timestamp ≥ test
start, or the expected blob name pattern. Prefer (b).

### Pitfall 7 — App-to-app URL is wrong

**Symptom:** FO worker can't reach Collector.FD — connection refused or DNS
resolution failure. Log: `No such host is known. (collector-fd:8080)`.

**Why:** Forgot the `.AppUrl("collector-fd", "CollectorUri")` on the
fo-worker's wiring. Or the env var name doesn't match what the FO config
reads from.

**Fix:** Check the receiving service's config for which env var binds to
`CollectorUri` (look in `IConfiguration` reads or `appsettings.json`).
Use the **exact** name in `.AppUrl(<alias>, <envVarName>)`. **See also
Pitfall 9 below** — for some apps the JSON config wins over the env var and
you must edit the JSON directly.

### Pitfall 8 — Static `azurite.crt` baked into the image missing `*.dfs.<suffix>` SAN

**Symptom:** fo-worker's calls into the MDC output processor / DataLake
storage fail with
`AuthenticationException: The remote certificate is invalid because of
errors in the certificate chain: RemoteCertificateNameMismatch`. The
strict demo test never gets to write a journal blob — assertion fails with
`'bigcatproduct-v3' should contain >= 1 journal blob. Found: 0`.

**Why:** `AdlsFileStoreAdapter.GetAccountUri()` constructs the DataLake
client URI as `https://{account}.blob.{suffix}`. The Azure DataLake SDK
then internally swaps `.blob.` → `.dfs.` for DFS REST operations. The
nginx TLS proxy reverse-proxies BOTH `*.blob.azurite.local` AND
`*.dfs.azurite.local` to Azurite. **The cert must cover both.**

Two cert generators exist and **must stay in sync**:
- `FinPlat.TestContainers/Builder/CertificateGenerator.cs` (dynamic, has
  `.dfs.` support — used when no `ExternalCertificate` is passed)
- `.slt/docker/nginx/generate-cert.ps1` (static, baked into the docker
  image as `azurite.crt` — used when `ExternalCertificate = cert` is
  passed). **This was missing `.dfs.` SANs and must be regenerated.**

**How to detect:** Exec into a running container and dump the cert SANs:

```powershell
docker exec <container-id> openssl x509 -in /etc/pki/ca-trust/source/anchors/azurite.crt -noout -ext subjectAltName
```

If the output does NOT include `DNS:*.dfs.<suffix>` and
`DNS:devstoreaccount1.dfs.<suffix>` you're hit by this pitfall.

**Fix:**
1. Edit `.slt/docker/nginx/generate-cert.ps1` so the `[alt_names]` block
   contains entries for `.dfs.` as well as `.blob./.queue./.table.`:
   ```ini
   DNS.5 = devstoreaccount1.dfs.azurite.local
   DNS.9 = *.dfs.azurite.local
   ```
2. Delete the stale `.slt/docker/nginx/azurite.crt` and `azurite.key`.
3. Re-run `generate-cert.ps1` to regenerate them.
4. **Force-rebuild the SLT images** (testcontainers may have a cached
   image with the old cert baked in):
   ```powershell
   docker rmi -f $(docker images --format "{{.ID}}" "finplat-test-*")
   docker rmi -f fo-worker-slt:latest collector-fd-slt:latest collector-orch-slt:latest
   ```
5. Re-run the test cold. SSL errors in
   `%TEMP%\slt-fo-worker.log` should drop to zero.

### Pitfall 9 — Env-var override doesn't always beat JSON config for downstream URIs

**Symptom:** You wire `.AppUrl("collector-fd", "CollectorUri")` on the
fo-worker container, but logs show fo-worker still POSTing to
`http://mock-services:8080/v1.0/ingestion` (i.e. WireMock), receiving back
`[]` (JSON array), and dying with
`JsonDeserializationException: expected JSON object but got JSON array`
when deserializing into `IngestionResponseContractV1`.

**Why:** The fo-worker host class
(`FinancialOrchestratorHost : AzureBlobOrchestratorHost<…>`) loads its
config from JSON file paths only — `AddEnvironmentVariables()` is not
in the config chain for these keys, or the JSON value wins. The
`.AppUrl(...)` wiring injects an env var (`CollectorUri=…`) that the
host never reads.

**Fix:** Edit `.slt/docker/config.docker.json` directly. Find the
`CollectorUri` (or whichever app-to-app URI is wrong) and change it to
the in-network DNS name of the receiver container:

```jsonc
"CollectorUri": "http://collector-fd:8080/",
```

Keep `.AppUrl(...)` wiring too — it's defensive and works for apps that
DO honor env vars. Verify the fix by grepping the fo-worker log:

```powershell
Select-String -Path "$env:TEMP\slt-fo-worker.log" `
    -Pattern "POST http://(collector-fd|mock-services):8080/v1.0/ingestion"
```

All `POST … /v1.0/ingestion` lines should be to `collector-fd`, not
`mock-services`.

### Pitfall 10 — WireMock-issued `FakeAccessToken` lacks `appid`/`oid` → real Collector returns 401

**Symptom:** Logs show fo-worker now correctly POSTs to
`http://collector-fd:8080/v1.0/ingestion` (per Pitfall 9 fix), but **every
call returns HTTP 401 Unauthorized**. Stack trace:
`DownstreamServiceException: Unauthorized` in
`CFS.FinancialOrchestrator.Worker.Helper.IAsyncIngest`.

**Why:** The framework's `AuthStubs.cs` stubs out
`https://login.microsoftonline.com/.../oauth2/v2.0/token` and returns a
hand-crafted JWT (the `FakeAccessToken` constant). With
`EnableS2SAuthentication=false` set on Collector.FD in
`.slt/docker/fd-config/Configuration/config.docker.json`,
`Microsoft.Crs.Financials.AspNetCore`'s `GetClientDetails()` **parses
(does not validate)** the bearer JWT and reads the `appid`/`oid` claims
directly. If those claims are missing, no partner is resolved and
authorization fails with 401.

**How to detect:** Decode the token fo-worker is sending. The
`Authorization: Bearer …` value is in the log. Pipe its payload (the
middle segment) to base64-decode — if it lacks `"appid"` and `"oid"`,
you're hit by this.

**Fix:** Update `FinPlat.TestContainers/Config/AuthStubs.cs` so
`FakeAccessToken` carries the partner claims the receiving Collector
expects:

```jsonc
// JWT payload (middle segment of the .NET-generated 3-part token)
{
  "aud":   "https://storage.azure.com",
  "iss":   "https://sts.windows.net/00000000-…/",
  "sub":   "test-subject",
  "appid": "33349fe2-44d3-47b3-b8c7-9bf8279cdf6b",  // matches PartnerConfiguration.ApplicationId
  "oid":   "d8a24b41-d537-47bb-95c6-f5aa46ead300",
  "name":  "FinancialOrchestrator",
  "iat": 1700000000, "nbf": 1700000000, "exp": 9999999999
}
```

`appid` must match a `PartnerConfiguration.Partners[*].ApplicationId`
entry in the Collector.FD config (look in
`.slt/docker/fd-config/Configuration/config.docker.json`). The signature
is not validated, so an unsigned-or-fake-signed JWT is fine. **No docker
image rebuild needed** — `FinPlat.TestContainers` is a project ref into
the test process, not into the container image; the WireMock stub it
registers runs in the WireMock container which restarts each test.

After this fix: `Select-String -Path "$env:TEMP\slt-fo-worker.log"
-Pattern "401|Unauthorized"` should be empty for the fo-worker calls.

---

## Diagnostic suite you inherit for free

On any `WaitForTableQuery` timeout, the framework prints:

1. The first 50 rows of the target table
2. Every table in Azurite with approx row counts
3. Every queue with approx message counts
4. Every blob container with approx blob counts
5. **Per-app full container logs** to `%TEMP%\slt-logs\<appname>.log`
6. Tail (last 4000 chars) of each app's log inline in the test output

**Always read the per-app log files first** when a seed times out. The
inline tail is just a nudge. The actual root cause is almost always in
`collector-orch.log` because the orchestrator runs the indexers that
silently fail on bad fixture shape.

---

## Verification checklist (before declaring "it works")

- [ ] All `[TestMethod]` runs pass in `dotnet test --filter` with verbose logger
- [ ] Containers tear down cleanly (no orphaned docker containers after class cleanup)
- [ ] The test asserts on **blobs written by the real receiver**, not on
      the request the caller made (otherwise you've just unit-tested the caller)
- [ ] Removing the `.AppUrl(...)` wiring makes the test fail (proves the
      caller is really calling the receiver — not a coincidence)
- [ ] Removing one preseed step makes the relevant test method fail with a
      meaningful assertion (proves the preseed matters)
- [ ] You're running the **strict** test (asserts on the *specific* target
      container, e.g. `bigcatproduct-v3`) and not just the loose
      marquee test (asserts `totalBlobs > 0` across *all* containers).
      The loose one passes from seed-side blobs alone and is misleading
      for "end-to-end works" claims. See Pitfall 6.
- [ ] `Select-String -Path "$env:TEMP\slt-*.log" -Pattern
      "RemoteCertificateNameMismatch|401|Unauthorized|expected JSON object but got JSON array"`
      returns zero matches in the steady-state run (transient hits during
      container warmup are ok).

If any of those bullets are weak, the test will pass on lucky days and fail
on unlucky ones. Keep iterating until all five hold.

---

## See also

* `MULTISERVICE.md` — user-facing developer doc for this pattern
* `fo-slt-authoring.md` — single-service FO worker SLT skill
* `skills.md` — single-service SLT primer
* `README.md` — full builder API reference
