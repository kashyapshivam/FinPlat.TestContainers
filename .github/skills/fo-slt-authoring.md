# Skill: Writing SLT Tests for FinancialOrchestrator (FO)

## Overview

This document teaches an AI agent how to write Service Level Tests (SLTs) for the **CFS.FinPlat.FinancialOrchestrator** worker service from scratch. SLTs are Docker-based integration tests that spin up the real FO Worker in a container alongside Azurite (Azure Storage emulator), WireMock (HTTP mock server), and an nginx TLS proxy — then send real messages through queues and verify end-to-end processing.

**Library**: `FinPlat.TestContainers` (NuGet package)  
**Test Framework**: MSTest  
**Target Framework**: .NET 8.0+  

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  MSTest Process (Your Test Code)                                         │
│                                                                          │
│  ┌──────────────┐  ┌─────────────┐  ┌────────────────────────────────┐  │
│  │   Azurite    │  │  WireMock   │  │  FO Worker (Docker Container)  │  │
│  │  ─────────── │  │  ────────── │  │  ────────────────────────────  │  │
│  │  Queues      │  │  Collector  │  │  Real worker code              │  │
│  │  Blobs       │  │  Catalog    │  │  Token auth → TLS Proxy        │  │
│  │  Tables      │  │  Billing    │  │  Queue polling                 │  │
│  │  (HTTPS+OAuth│  │  Org APIs   │  │  Event processing              │  │
│  │   via proxy) │  │             │  │  MDC/DFS writes                │  │
│  └──────────────┘  └─────────────┘  └────────────────────────────────┘  │
│         ▲                 ▲                        │                      │
│         │                 │                        │                      │
│         └─────────────────┼────────────────────────┘                     │
│                           │                                              │
│  ┌────────────────────────┼──────────────────────────────────────────┐   │
│  │         nginx TLS Proxy (terminates TLS, routes traffic)          │   │
│  │  login.microsoftonline.com → WireMock (auth tokens)               │   │
│  │  *.blob.azurite.local  → Azurite blob port                        │   │
│  │  *.queue.azurite.local → Azurite queue port                       │   │
│  │  *.table.azurite.local → Azurite table port                       │   │
│  │  ⚠️ DFS NOT currently configured (see DFS section below)           │   │
│  └───────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Critical Concept: EventEntityV1 Message Envelope

**ALL FO workers use the same queue adapter that expects messages in this specific format:**

### Format
Messages must be **base64-encoded JSON** of an `EventEntityV1` envelope:

```json
{
  "Event": { /* your order/event JSON payload here as a nested object */ },
  "EventType": "OrderCreation",
  "Properties": {
    "EventHubMessageId": "some-guid",
    "EventHubMessageQueueDate": "2024-01-01T00:00:00Z"
  }
}
```

### Serialization Rules
- The **worker** deserializes with **Newtonsoft.Json** (expects PascalCase property names)
- Your **test helper** can use `System.Text.Json` (the `JsonNode`/`JsonObject` API) — the output JSON format is what matters, not which serializer produces it
- `Event` field is a `JObject` — the raw order JSON goes here as a nested object
- The entire JSON is then **base64-encoded** before being sent to the queue
- The queue adapter does: base64-decode → JSON deserialize as EventEntityV1 → read `Event` property

### Helper Method (copy into every SLT test class)

```csharp
private static string WrapInEventEntity(string eventJson)
{
    var eventNode = JsonNode.Parse(eventJson);
    var envelope = new JsonObject
    {
        ["Event"] = eventNode,
        ["EventType"] = "OrderCreation",
        ["Properties"] = new JsonObject
        {
            ["EventHubMessageId"] = Guid.NewGuid().ToString(),
            ["EventHubMessageQueueDate"] = DateTime.UtcNow.ToString("o")
        }
    };
    var json = envelope.ToJsonString();
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
}
```

> **Note:** The `Properties` field values are NOT validated by the worker — any GUID/datetime works. You may also see `"MessageId"` / `"EnqueuedTime"` as keys in some test files (e.g., `ExampleSltTests.cs`). Both conventions are equivalent; the worker ignores these values.

### Common Mistake
If you send raw JSON (not base64) or a flat order JSON (not wrapped in EventEntityV1), the message will sit in the queue forever — the worker cannot parse it.

---

## Step-by-Step: Writing an SLT for a New Worker

### Step 1: Identify the Worker

Each worker has:
- **WorkerName**: e.g., `BigCatProductQueueWorkerV1`  
- **QueueName**: e.g., `bigcatproduct` (always lowercase in Azure Queue Storage)
- **Event type it processes**: e.g., OrderEventV20201031

Find these in:
- `slt.json` → `workers[]` array
- `config.docker.json` → `AzureQueueWorkerConfiguration[]`
- Source: `src/FinancialOrchestrator.Worker/Configuration/{WorkerType}/`

### Step 2: Create the Test File

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinPlat.TestContainers;
using FinPlat.TestContainers.Builder;
using FinPlat.TestContainers.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FinancialOrchestrator.SltTests;

[TestClass]
[TestCategory("SLT")]
public class MyWorkerSltTests
{
    private static TestEnvironment _env = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var sltContext = GetSltContext();
        var cert = new CertificateMaterial(
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.crt")),
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.key")));

        _env = await new TestEnvironmentBuilder()
            .AddAzurite(opts =>
            {
                opts.UseTokenAuth = true;
                opts.ExternalCertificate = cert;
            })
            .AddMockApi("services", mock =>
            {
                // Configure mocks here — see Step 4
            })
            .AddApplication("fo-worker", app =>
            {
                app.FromDockerfile(
                    dockerfilePath: "docker/Dockerfile.slt",
                    contextPath: sltContext);
                app.WithEnv("Environment", "Local"); // REQUIRED — loads config.local.json
                app.WithEnv("WorkerNames", "MyWorkerNameV1");
                // ⚠️ Do NOT set KEY_VAULT_URI env var — it causes silent catalog lookup failures
                app.WithEnv("DisplayCatalogUri", "http://mock-services:8080"); // Override so mock paths start at /v8.0/...
                app.WithEnv("BillingGroupUri", "http://mock-services:8080/billinggroups");
                app.WithEnv("Logging__LogLevel__Default", "Information");
                app.WithEnv("Logging__LogLevel__Microsoft", "Warning");
            })
            .Wire("fo-worker", wire =>
            {
                wire.Queue("myqueuename")       // lowercase queue name
                    .Table("CollectorIndex")     // required for all workers
                    .Blob("featureflag")         // add if worker uses feature flags
                    .UseTokenAuth()
                    .MockApi("services", "CollectorUri");
            })
            .BuildAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_env != null) await _env.DisposeAsync();
    }

    [TestInitialize]
    public async Task TestInit()
    {
        await _env.MockApi("services").ResetRequestLogAsync();
    }

    // ... test methods ...

    #region Helpers

    private static string WrapInEventEntity(string eventJson)
    {
        var eventNode = JsonNode.Parse(eventJson);
        var envelope = new JsonObject
        {
            ["Event"] = eventNode,
            ["EventType"] = "OrderCreation",
            ["Properties"] = new JsonObject
            {
                ["EventHubMessageId"] = Guid.NewGuid().ToString(),
                ["EventHubMessageQueueDate"] = DateTime.UtcNow.ToString("o")
            }
        };
        var json = envelope.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<string> ReadScenarioFileAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", "MyWorker", fileName);
        return await File.ReadAllTextAsync(path);
    }

    private static string GetSltContext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".slt")))
            dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, ".slt")
            : throw new InvalidOperationException("Cannot find .slt directory.");
    }

    #endregion
}
```

### Step 3: Create the Scenario Fixture (JSON)

Place test data in `tests/FinancialOrchestrator.SltTests/Scenarios/{WorkerName}/happy-path.json`.

**Where to find fixture data:**
- Unit test scenarios: `tests/FinancialOrchestrator.UnitTests/Scenarios/` (organized by type)
- Real production examples from Kusto queries

**Critical fixture requirements:**
- Must have `"summary"` key for `OrderEventBaseV20201031` deserialization
- Operations must have `"type": "creation"` (or appropriate type) for polymorphic deserialization
- `program_code` must be in the operation (NOT in summary) — most validators check this
- **C10 program code is rejected** by BigCatProduct and BPLO validators — use "C30"
- **⚠️ `productType` MUST be `"BigCatProduct"` for BPLO** — NOT "SaaS" or other values. This controls processor routing.
- Dates must be valid (not in the future for some validators)
- For BPLO: include `augmented_timestamp` and `completedTimestamp` in operations

### Step 4: Configure Mock Responses

Each worker makes different external API calls. Here's how to figure out what's needed:

#### Discovery Method
1. Start with the recommended catch-all pattern:
   ```csharp
   mock.OnPost("/v1.0/events", 200, """[]""");       // event search → array
   mock.OnPost("/v1.0/ingestion", 200, """{"status":"accepted"}"""); // ingestion → object
   mock.OnUnmatched(200, """{}""");                   // everything else → object
   ```
2. Run the test — if the worker times out, check WireMock request log
3. Add specific mocks for each endpoint the worker calls

#### Common Endpoints (All Workers)

**⚠️ IMPORTANT: Mock paths depend on the effective base URI from config.**

- Default `config.docker.json` sets: `"DisplayCatalogUri": "http://mock-services:8080/displaycatalog"`
- If you override in test to: `app.WithEnv("DisplayCatalogUri", "http://mock-services:8080")`, mock paths start at root (e.g., `/v8.0/products/...`)
- If you DON'T override, mock paths start at the config path (e.g., `/displaycatalog/v8.0/products/...`)

**When NOT overriding URIs** (preferred — simpler):

| Config Key | Base Path | Example Mock Path |
|------------|-----------|-------------------|
| `CollectorUri` | `/` | `/v1.0/events`, `/v1.0/ingestion` |
| `DisplayCatalogUri` | `/displaycatalog` | `/displaycatalog/v8.0/products/{id}/{sku}` |
| `BillingGroupUri` | `/billinggroups` | `/billinggroups/...` |

**When overriding to root** (as BPLO tests do):

| Endpoint | Method | Purpose | Response Format |
|----------|--------|---------|-----------------|
| `/v1.0/events` | POST | Collector event search | `[{EventSearchResponseContractV1}]` or `[]` |
| `/v1.0/ingestion` | POST | Collector ingestion (output) | `{"status": "accepted"}` |
| `/v8.0/products/{productId}/{skuId}` | GET | DisplayCatalog | `{"Product": {ProductV8}}` |
| `/v8.0/products/{productId}/{skuId}/{availabilityId}` | GET | DisplayCatalog (BPLO) | `{"Product": {ProductV8}}` |

> **⚠️ About `ExampleSltTests.cs`:** The Asset test uses broad base-path mocks (`/collector`, `/displaycatalog`, `/tax`, etc.) that don't match actual request paths — they serve only as catch-all placeholders via `OnUnmatched`. **Do NOT copy this pattern for new workers.** Follow the BPLO pattern for accurate endpoint mocking.

#### EventSearchResponseContractV1 Format

```json
[
    {
        "EventId": "some-guid",
        "AccountId": "account-guid",
        "EventType": "BillingGroupSummary",
        "EventSource": "billing-group-2019-05-31",
        "Version": 1,
        "CreatedTimestamp": "2024-01-01T00:00:00Z",
        "EntryTimestamp": "2024-01-01T00:00:00Z",
        "Content": { /* actual event data as JObject */ }
    }
]
```

The `Content` field is extracted by `EventSearchResponseExtensions.ToJObject()` which does:
`response.Select(e => e.Content).ToList()`

#### Body-Pattern Matching (for conditional responses)

Workers make multiple POST calls to `/v1.0/events` with different search criteria. Use body-matching to return different responses:

```csharp
// Return order data only when searching for orders
mock.OnPost("/v1.0/events", "includeOpenOrder", 200, orderResponse, priority: 1);

// Return org data only when searching for OrganizationSummary
mock.OnPost("/v1.0/events", "OrganizationSummary", 200, orgResponse, priority: 1);

// Default: return empty for all other searches
mock.OnPost("/v1.0/events", 200, """[]""");
```

#### CatalogV8 Response Format

**CRITICAL**: The response must be wrapped in `{"Product": {...}}`:

```json
{
    "Product": {
        "ProductId": "CFQ7TTC0H9MP",
        "DisplaySkuAvailabilities": [
            {
                "Sku": { "SkuId": "0001" },
                "Availabilities": [
                    { "AvailabilityId": "CFQ7TTC0KQ2Z" }
                ]
            }
        ],
        "...": "... 32KB of full ProductV8 data ..."
    }
}
```

**Source**: Copy from `tests/FinancialOrchestrator.UnitTests/Scenarios/Product/CFQ7TTC0H9MP/ProductV8.json` and wrap in `{"Product": ...}`

### Step 5: Write Test Methods

> **Two wait strategies exist:**  
> - `WaitForIdleAsync` (preferred, used by BPLO) — monitors queue length internally with timeout  
> - Manual polling loop with `GetMessageCountAsync()` + retry (used by Asset tests) — equivalent but more verbose

#### Happy Path Pattern (with debugging support)

```csharp
[TestMethod]
public async Task Worker_ClosedOrder_ProcessesAndCallsCollector()
{
    var orderJson = await ReadScenarioFileAsync("happy-path.json");
    await _env.Queue("myqueuename").SendAsync(WrapInEventEntity(orderJson));

    try
    {
        // Wait for queue to drain
        await _env.WaitForIdleAsync(new[] { "myqueuename" }, timeoutSeconds: 60);
    }
    catch (TimeoutException)
    {
        // Dump worker logs on timeout for debugging
        var logs = await _env.Application("fo-worker").GetLogsAsync();
        Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
        throw;
    }

    // Assert queue is empty
    Assert.IsTrue(await _env.Queue("myqueuename").IsEmptyAsync(),
        "Message should be dequeued after processing");

    // Verify Collector ingestion was called
    var mock = _env.MockApi("services");
    await mock.VerifyAsync("/v1.0/ingestion", Times.AtLeastOnce);
}
```

#### Malformed Message Pattern

```csharp
[TestMethod]
public async Task Worker_MalformedMessage_DequeuedWithoutCrash()
{
    await _env.Queue("myqueuename").SendAsync(WrapInEventEntity("{}"));
    await _env.WaitForIdleAsync(new[] { "myqueuename" }, timeoutSeconds: 30);

    Assert.IsTrue(await _env.Queue("myqueuename").IsEmptyAsync(),
        "Worker should dequeue malformed messages gracefully");
}
```

#### Worker Health Pattern

```csharp
[TestMethod]
public async Task Worker_RemainsHealthy_AfterProcessing()
{
    var msg = await ReadScenarioFileAsync("happy-path.json");
    
    // First message
    await _env.Queue("myqueuename").SendAsync(WrapInEventEntity(msg));
    await _env.WaitForIdleAsync(new[] { "myqueuename" }, timeoutSeconds: 60);
    Assert.AreEqual(0, await _env.Queue("myqueuename").GetMessageCountAsync());

    // Second message proves worker still alive
    await _env.Queue("myqueuename").SendAsync(WrapInEventEntity(msg));
    await _env.WaitForIdleAsync(new[] { "myqueuename" }, timeoutSeconds: 60);
    Assert.AreEqual(0, await _env.Queue("myqueuename").GetMessageCountAsync(),
        "Worker should still be alive after first message");
}
```

### Step 6: Ensure csproj Configuration

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="$(ProjectDir)..\..\props\Test.props" />
  <PropertyGroup>
    <RootNamespace>FinancialOrchestrator.SltTests</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.4.0" />
    <PackageReference Include="MSTest.TestAdapter" Version="4.2.1" />
    <PackageReference Include="MSTest.TestFramework" Version="4.2.1" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FinPlat.TestContainers" Version="1.0.0" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Scenarios\**\*.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

---

## Worker-Specific Knowledge

### BigCatProduct Worker

| Setting | Value |
|---------|-------|
| WorkerName | `BigCatProductQueueWorkerV1` |
| Queue | `bigcatproduct` |
| Required blobs | `featureflag`, `catalogcachedatacfq7ttc0h9mp0001cfq7ttc0kq2z`, `bigcatproductv4` |
| Required tables | `CollectorIndex` |
| DFS filesystem | `bigcatproductv4` (MDC output — see DFS caveat below) |

**Validation rules:**
- `program_code` must NOT be "C10" (always rejected)
- Needs `operation` present for closed orders
- Gets `program_code` from: `Operations.LastOrDefault(adjustment) ?? Operations.LastOrDefault(creation)`

**Processing chain:**
1. Dequeue → base64 decode → EventEntityV1 parse → Validate
2. Search CollectorIndex table (empty is OK)
3. BillingGroupSummary: tries blob → falls back to `POST /v1.0/events`
4. Catalog: tries blob cache → then `GET /v8.0/products/{productId}/{skuId}`
5. MDC write via DFS (nginx mock)
6. `POST /v1.0/ingestion` (final output)

### Asset Worker

| Setting | Value |
|---------|-------|
| WorkerName | `AssetQueueWorkerV1` |
| Queue | `asset` |
| Required blobs | *(none needed for basic happy path — add `featureflag`, `assetv4` only if worker fails requesting them)* |
| Required tables | `CollectorIndex`, `DeltaSchedulerIndex` |
| DFS filesystem | `assetv4` (MDC output — see DFS caveat below) |

**Key dependency:** Requires an Order event response from Collector search. The body-matching pattern is `"includeOpenOrder":true`.

**Processing chain:**
1. Dequeue → AssetSummaryWithEvents parse → Validate (state, dates, event type)
2. Search for augmented order: `POST /v1.0/events` with `includeOpenOrder:true`
3. If empty, tries open order search
4. Match order to asset by product info
5. MDC write via DFS → `POST /v1.0/ingestion`

### BillingPurchaseLineOrganization (BPLO) Worker

| Setting | Value |
|---------|-------|
| WorkerName | `BillingPurchaseLineOrganizationQueueWorkerV1` |
| Queue | `billingpurchaselineorganization` |
| Required blobs | `featureflag`, `catalogcachedatacfq7ttc0h9mp0001cfq7ttc0kq2z` |
| Required tables | `CollectorIndex` |

**Key complexity:** BPLO has 3 parallel sub-processor chains:
1. **PurchaseLineItem** — needs BillingGroupSummary, BillingRecordSummary, catalog
2. **BillingItem** — needs catalog, OrderExtension
3. **Organization** — needs OrganizationSummary (REQUIRED, will throw if missing)

**⚠️ IMPORTANT — Correct assertions for BPLO:**
- Do NOT assert on `/v1.0/ingestion` for BPLO happy-path — ingestion requires ALL 3 sub-processors to succeed perfectly
- Instead verify: `/v1.0/events` called `Times.AtLeast(2)` (SkipDecision + org lookup) and `/v8.0/products` called `Times.AtLeastOnce` (catalog processor ran)
- These prove the worker processed the message without crashing and ran the critical processing chains
- If you MUST verify ingestion, ensure ALL mock responses are pixel-perfect (org, billing group, billing record, catalog)

**Proven env var overrides (MANDATORY):**
```csharp
app.WithEnv("Environment", "Local");
app.WithEnv("WorkerNames", "BillingPurchaseLineOrganizationQueueWorkerV1");
// ⚠️ Do NOT set KEY_VAULT_URI — it causes silent catalog lookup failures
app.WithEnv("DisplayCatalogUri", "http://mock-services:8080");
app.WithEnv("BillingGroupUri", "http://mock-services:8080/billinggroups");
app.WithEnv("Logging__LogLevel__Default", "Information");
app.WithEnv("Logging__LogLevel__Microsoft", "Warning");
```

**Fixture tips:**
- Set `billingRecord` to `null` to skip billing record fetch
- Set `billingGroup.id` to `""` to skip BillingGroupSummary fetch
- Must have `augmented_timestamp` and `completedTimestamp` in operations
- Organization endpoint: mock `/v1.0/events` with body containing "OrganizationSummary"
- Catalog uses 3-part path: `/v8.0/products/{productId}/{skuId}/{availabilityId}`
- Catch-all strategy:
  - `mock.OnPost("/v1.0/events", 200, """[]""")` — event search default returns empty array
  - `mock.OnUnmatched(200, """{}""")` — global catch-all returns object for non-search endpoints
  - This way, event searches get arrays while other endpoint calls (catalog, billing, etc.) get empty objects

**Organization mock response format:**
```json
[{
    "EventId": "org-001",
    "AccountId": "account-guid",
    "EventType": "OrganizationSummary",
    "EventSource": "account-organization-2019-05-31",
    "Version": 1,
    "CreatedTimestamp": "2024-01-01T00:00:00Z",
    "EntryTimestamp": "2024-01-01T00:00:00Z",
    "Content": {
        "id": "org-001",
        "accountId": "account-guid",
        "version": 1,
        "state": "active",
        "createdTimestamp": "2024-01-01T00:00:00Z",
        "updatedTimestamp": "2024-01-01T00:00:00Z",
        "organizationType": "organization",
        "audience": "commercial",
        "legalEntity": {
            "address": {
                "country": "US",
                "city": "Redmond",
                "region": "WA",
                "postalCode": "98052"
            }
        }
    }
}]
```

### NormalizedOrder Worker

| Setting | Value |
|---------|-------|
| WorkerName | `NormalizedOrderQueueWorker` |
| Queue | `normalizedorder` |
| Required blobs | *(none for basic happy path — add if worker requests them)* |
| Required tables | `CollectorIndex` |

**Key complexity:** NormalizedOrder has several required dependency lookups:

1. **BillingRecordSummary** — POST `/v1.0/events` with body containing `"BillingRecordSummary"` → return array of event search results
2. **BillingGroupSummary** — POST `/v1.0/events` with body containing `"BillingGroupSummary"` → return array
3. **OrganizationSummary** — POST `/v1.0/events` with body containing `"OrganizationSummary"` → **REQUIRED, throws if missing**
4. **Tax service** — may call external tax endpoint depending on order type
5. **Prior order versions** — for orders with `operation.version > 1`, searches for previous version

**Fixture tips:**
- Use a simple `creation` operation with `version: 1` to avoid prior-version lookups
- Include valid `program_code` (e.g., "C30")
- Set `billingRecord` and `billingGroup` fields to trigger or skip those dependency chains

**Mock setup pattern:**
```csharp
mock.OnPost("/v1.0/events", "BillingRecordSummary", 200, billingRecordResponse, priority: 1);
mock.OnPost("/v1.0/events", "BillingGroupSummary", 200, billingGroupResponse, priority: 1);
mock.OnPost("/v1.0/events", "OrganizationSummary", 200, orgResponse, priority: 1);
mock.OnPost("/v1.0/events", 200, """[]""");  // default for other searches
mock.OnPost("/v1.0/ingestion", 200, """{"status":"accepted"}""");
mock.OnUnmatched(200, """{}""");
```

---

### Certificate Architecture

The FO Worker uses **token auth** (WorkloadIdentity) to access Azure Storage. This requires HTTPS with a trusted certificate.

**Static cert location:** `.slt/docker/nginx/azurite.crt` and `.key`

**⚠️ CRITICAL: Do NOT generate certs from scratch unless absolutely necessary.** Copy working certs from a reference directory (see below). Two agents failed because their generated certs had wrong SANs.

**SANs (Subject Alternative Names) the cert MUST cover (ALL of these):**
- `*.azurite.local` ← **CRITICAL: wildcard root — missing this causes silent TLS failures**
- `*.blob.azurite.local`
- `*.queue.azurite.local`
- `*.table.azurite.local`
- `azurite.local`
- `localhost`
- `login.microsoftonline.com`

**Where to copy working certs from (in priority order):**
1. Another SLT project in the same repo that already has `.slt/docker/nginx/azurite.crt` and `.key`
2. Reference: `C:\Users\shivkashyap\Downloads\workspace_Repo\financial_copy1\CFS.FinPlat.FinancialOrchestrator\.slt\docker\nginx\`
3. Reference: `C:\Users\shivkashyap\Downloads\workspace_Repo\CFS.FinPlat.FinancialOrchestrator\.slt\docker\nginx\`

**If you MUST generate new certs**, use this exact command:
```bash
openssl req -x509 -newkey rsa:2048 -keyout azurite.key -out azurite.crt -days 3650 -nodes \
  -subj "/CN=azurite.local" \
  -addext "subjectAltName=DNS:*.azurite.local,DNS:*.blob.azurite.local,DNS:*.queue.azurite.local,DNS:*.table.azurite.local,DNS:azurite.local,DNS:localhost,DNS:login.microsoftonline.com"
```

**How it works:**
1. Cert is baked into the Docker image at build time (`COPY` + `update-ca-trust`)
2. At test runtime, the same cert is passed via `ExternalCertificate` to the library
3. The library uses this cert for Azurite HTTPS, TLS proxy, and app container trust

### DFS (Data Lake Storage) Mock

**⚠️ CURRENT STATUS:** The checked-in `nginx.conf` does **NOT** include a DFS server block. Workers that write MDC output via `AdlsFileStoreAdapter` will fail unless you add the DFS mock.

**To add DFS support**, append this server block to `.slt/docker/nginx/nginx.conf`:

```nginx
# DFS service — Azurite does not support DFS, so nginx returns mock success responses
server {
    listen 443 ssl;
    server_name ~^(?<account>.+)\.dfs\.azurite\.local$;
    ssl_certificate /etc/nginx/certs/azurite.crt;
    ssl_certificate_key /etc/nginx/certs/azurite.key;

    # CreatePath
    location ~ "^/[^/]+/[^?]+" {
        if ($request_method = PUT) { return 201; }
        if ($request_method = HEAD) { return 200; }
        # PATCH with action=append or action=flush
        if ($request_method = PATCH) { return 202; }
    }
}
```

**DFS filesystem naming:**
- The DFS filesystem name = `typeof(T).Name.ToLowerInvariant()` where T is the output type
- Examples: `bigcatproductv4`, `assetv4`

**Workaround if you don't add DFS**: Some workers may pass SLTs without DFS if they catch the write failure gracefully. Test with a catch-all first; if the worker hangs, add the DFS block.

### Azurite OAuth Requirements

Azurite `--oauth basic` mode requires JWT tokens with ALL of:
- `nbf` (not before)
- `exp` (expiration)
- `iat` (issued at)
- `iss` must match `https://sts.windows.net/` prefix
- `aud` must match `https://storage.azure.com`

The library's `AuthStubs` provides a fake JWT that satisfies these. JWT signature is NOT validated in basic mode.

### Docker Network Aliases

The library creates a Docker network with these aliases:
- `azurite` — Azurite container
- `mock-services` — WireMock container (**used by application containers** for direct HTTP calls)
- `wiremock` — Also WireMock container (**used internally by nginx** for auth proxy routing)
- `tls-proxy` — nginx TLS proxy

**Rule:** Application env vars (e.g., `CollectorUri`, `DisplayCatalogUri`) should use `http://mock-services:8080`. The nginx config routes auth traffic to `http://wiremock:8080` internally.

### config.docker.json Key Settings

| Setting | Value | Purpose |
|---------|-------|---------|
| `StorageAccountEndpointSuffix` | `azurite.local` | Routes storage through TLS proxy |
| `StorageAccountConfiguration[].Name` | `devstoreaccount1` | Azurite default account |
| `UseWorkloadIdentity` | `true` | Token auth mode |
| `CollectorUri` | `http://mock-services:8080/` | Direct HTTP to WireMock |
| `DisplayCatalogUri` | `http://mock-services:8080/displaycatalog` | Catalog via WireMock |
| `TableIndexAzureTableKeyValueStoreConfiguration` | See below | Table index config |
| `MdcPrivateConfiguration.Name` | `devstoreaccount1` | DFS/MDC output |

Table index config example:
```json
{
    "AccountName": "devstoreaccount1",
    "EndpointSuffix": "azurite.local",
    "TableName": "CollectorIndex"
}
```

### Dockerfile.slt Structure

**Note:** The checked-in Dockerfile uses `aspnet:10.0-azurelinux3.0` and Azure Linux cert paths. The `slt.json` metadata may show different values (e.g., `aspnet:8.0`, Debian cert paths) — always trust the actual Dockerfile over the manifest metadata.

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0

WORKDIR /app

# Trust the TLS certificate
COPY docker/nginx/azurite.crt /etc/pki/ca-trust/source/anchors/azurite.crt
RUN update-ca-trust

# Copy pre-published app
COPY publish/ .

# Inject Docker-specific config
COPY docker/config.docker.json Configuration/config.local.json

# Workload identity simulation
ENV AZURE_CLIENT_ID=00000000-0000-0000-0000-000000000000
ENV AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000
ENV AZURE_CLIENT_SECRET=dummy-secret-for-slt
ENV AZURE_FEDERATED_TOKEN_FILE=/app/federated-token.txt
RUN echo "dummy-federated-token" > /app/federated-token.txt

ENTRYPOINT ["dotnet", "CFS.FinancialOrchestrator.WorkerService.dll"]
```

> **⚠️ CRITICAL:** The config file is named `config.local.json`. The worker only loads this file when `Environment=Local`. You **MUST** set `app.WithEnv("Environment", "Local")` in your test — otherwise the worker ignores the Docker config entirely and fails with missing configuration errors.

---

## Common Issues & Solutions

### Issue 1: Message stays in queue forever

**Symptom:** `GetMessageCountAsync()` returns 1 after timeout.

**Causes:**
1. **Not base64 encoded** — must wrap in EventEntityV1 AND base64 encode
2. **Wrong JSON structure** — `Event` field must contain the order as nested object
3. **Missing required resources** — worker retries until dead-letter

**Debug:** Add timeout handler that dumps worker logs:
```csharp
catch (TimeoutException)
{
    var logs = await _env.Application("worker").GetLogsAsync();
    Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
    throw;
}
```

### Issue 1b: Message disappears but no output (no ingestion call)

**Symptom:** Queue drains but `/v1.0/ingestion` was never called.

**Causes:**
1. **Validation rejected the message** — worker validated and deleted it silently
2. **Fixture has invalid data** — wrong `program_code`, missing required fields, bad dates

**Debug:** Check worker logs for validation messages and inspect what the worker logged about the message.

### Issue 2: Worker throws RequiredResourceNotFoundException

**Symptom:** Worker processes message but throws exception, message gets retried.

**Causes:**
- Missing mock response for a required dependency
- Mock response has wrong format (e.g., flat object instead of array)
- Body-matching pattern doesn't match actual request

**Fix:** Dump worker logs to see what failed — the audit event shows the exact "Reason":
```csharp
var logs = await _env.Application("fo-worker").GetLogsAsync();
// Look for "All state metadata processors failed" with Reason details
Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
```

> **⚠️ NOTE:** `GetAllRequestsAsync()` does NOT exist in the library. Use `GetLogsAsync()` for diagnostics and `VerifyAsync(path, Times)` / `GetCallCountAsync(path)` for assertions.

### Issue 3: Catalog "not found" despite returning 200

**Cause:** Response format wrong. Must be `{"Product": {...}}` not flat or array.

**Fix:** Use the real `ProductV8.json` from unit tests wrapped in `{"Product": ...}` envelope.

### Issue 4: Container crashes on startup

**Causes:**
- Certificate not trusted (cert SAN doesn't cover required hostnames)
- Config file not found (wrong path in Dockerfile COPY)
- Missing queue/table in Azurite (add to `.Wire()` configuration)
- **OpenTelemetry/Geneva not disabled** — see Issue 4b below

### Issue 4b: Worker crashes with "metricAccountName cannot be null" or "keyVaultUri cannot be null"

**Symptom:** Worker container exits immediately with `ArgumentException` in OpenTelemetry or KeyVault initialization.

**Cause:** Newer worker builds require external infrastructure config that doesn't exist in SLT environments.

**Fix:** The `config.docker.json` MUST have these settings to prevent startup crashes:
```json
{
    "OpenTelemetryConsoleEnabled": false,
    "OpenTelemetryCounterProviderEnabled": false,
    "OpenTelemetryGenevaEnabled": false,
    "OpenTelemetryTraceProviderEnabled": false,
    "GenevaCounterProviderEnabled": false,
    "KeyVaultUri": "http://mock-services:8080/keyvault",
    "KeyVaultMonitorEnabled": false
}
```

Additionally, ensure `KeyVaultMonitorEnabled` is `false` in config.docker.json. **Do NOT set `KEY_VAULT_URI` as an env var** — it causes silent catalog lookup failures where the worker tries to resolve secrets via KeyVault instead of using WireMock.

> **⚠️ WARNING:** If you create a new `config.docker.json` from scratch, you WILL hit these crashes. Always start from the existing checked-in config which already has these disabled.

### Issue 5: Auth failures (403 from Azurite)

**Cause:** Azurite's OAuth validator requires `nbf` claim in JWT token.

**Fix:** Already fixed in library's `AuthStubs.cs`. Ensure latest version of FinPlat.TestContainers.

### Issue 6: program_code validation rejects message

**Cause:** BigCatProduct rejects `C10`, BPLO has complex validation rules.

**Fix:** Use `"C30"` as program_code in test fixtures. Ensure it's in the **operation**, not the summary.

### Issue 7: DFS write fails (no DFS endpoint)

**Cause:** Azurite doesn't support DFS. Workers writing MDC output via AdlsFileStoreAdapter will fail.

**Fix:** Add the DFS server block to `.slt/docker/nginx/nginx.conf` (see DFS section above). Ensure cert has `*.dfs.azurite.local` SAN and `MdcPrivateConfiguration.Name` is set to `devstoreaccount1`.

### Issue 8: WireMock body-matching not working

**Cause:** The search body uses different casing or format than expected.

**Fix:** Use partial string matching (contains), not exact match. Example: `"OrganizationSummary"` will match regardless of surrounding JSON.

### Issue 9: `A compatible .NET SDK was not found` (every dotnet command fails)

**Symptom:** Right after `git clone`, `dotnet --version` / `dotnet build` / `dotnet publish` / `dotnet test` all fail with:
```
A compatible .NET SDK was not found.
Requested SDK version: 10.0.300
global.json file: ...\global.json
Install the [10.0.300] .NET SDK or update ...\global.json to match an installed SDK.
```

**Cause:** `global.json` pins an SDK feature band you don't have installed. `rollForward: latestFeature` only rolls within the SAME feature band (the hundreds digit of patch), so installed `10.0.100` will NOT satisfy a pin of `10.0.300`. This bites every agent that clones FO on a machine that hasn't been refreshed since the last SDK bump — it surfaces at the very first `dotnet` invocation and blocks the entire SLT workflow.

**Fix (in order of preference):**
1. **Install the pinned SDK:** `winget install Microsoft.DotNet.SDK.10` or download from <https://dotnet.microsoft.com/download> and pick the exact version. Best for CI and shared machines.
2. **Local sandbox edit (do NOT commit):** lower `global.json` `version` to your highest installed feature band (e.g. `10.0.100`). Acceptable for a throwaway test clone like `slt_testing\`.
3. **Permissive roll-forward (do NOT commit casually):** change `rollForward` from `latestFeature` to `latestMajor`. This will accept any 10.x SDK.

Always confirm the fix with `dotnet --version` from the repo root before continuing — every other step downstream (build, publish, test) will fail with the identical message until the SDK resolves.

---

## File Layout

```
CFS.FinPlat.FinancialOrchestrator/
├── .slt/
│   ├── slt.json                          # Manifest (workers, APIs, infra config)
│   ├── publish.ps1                       # Script to dotnet publish worker
│   ├── .gitignore                        # Ignores publish/ directory
│   ├── docker/
│   │   ├── Dockerfile.slt                # Worker Docker image
│   │   ├── config.docker.json            # Config overlay for Docker
│   │   └── nginx/
│   │       ├── nginx.conf                # TLS proxy routing config
│   │       ├── azurite.crt               # Static TLS certificate
│   │       └── azurite.key               # Private key
├── tests/
│   └── FinancialOrchestrator.SltTests/
│       ├── FinancialOrchestrator.SltTests.csproj
│       ├── BploSltTests.cs               # BPLO test class ← CANONICAL EXAMPLE
│       ├── ExampleSltTests.cs            # Asset/example test class (generated/minimal)
│       └── Scenarios/
│           ├── Asset/
│           │   └── happy-path.json       # Asset order fixture
│           └── Bplo/
│               ├── happy-path.json       # BPLO order fixture
│               └── CatalogV8.json        # Full catalog response
└── nuget.config                          # Has NuGet source for library
```

> **⚠️ Reference file guidance:** `BploSltTests.cs` is the canonical example for endpoint mocking patterns (body-matching, specific responses, correct paths). `ExampleSltTests.cs` is auto-generated/minimal and uses broad base-path mocks (e.g., `/collector`, `/displaycatalog`) that may not match actual endpoint paths. When in doubt, follow BPLO patterns.

---

## Running SLT Tests

### Prerequisites
1. **Docker Desktop running** (Linux containers mode) — tests will fail immediately if Docker is not running
2. Worker published to `.slt/publish/` — see publish step below
3. FinPlat.TestContainers NuGet package available (from internal feed)
4. **.NET SDK that satisfies `global.json`** (FO `main` currently pins `10.0.300` with `rollForward: latestFeature`)

> **⚠️ SDK PIN DRIFT (very common on fresh clones):** If `dotnet --version` from the repo root prints `A compatible .NET SDK was not found. Requested SDK version: <X.Y.Z>`, you are missing the SDK that `global.json` requires. `rollForward: latestFeature` only rolls within the **same feature band** (the hundreds digit), so e.g. installed `10.0.100` will NOT satisfy a pin of `10.0.300`.
> Pick one fix:
> - **Recommended in CI / shared machines:** install the exact SDK (`winget install Microsoft.DotNet.SDK.10` or download from <https://dotnet.microsoft.com/download>).
> - **Quick local sandbox fix (do NOT commit):** edit `global.json` to your highest installed feature band, e.g. `"version": "10.0.100"`.
> - **Permissive (also do NOT commit unless intended):** change `rollForward` to `"latestMajor"`.
> Verify with `dotnet --version` before publishing or building — every subsequent step will fail with the same message until this is resolved.

> **⚠️ FIRST RUN WARNING:** The very first test run will pull Docker images (~5-10 minutes) for Azurite, WireMock, nginx, and the worker base image. Subsequent runs reuse cached images and complete in ~60-90 seconds.

### Publish the Worker
```powershell
# From repo root — MUST be done before running tests:
dotnet publish src/FinancialOrchestrator.WorkerService/FinancialOrchestrator.WorkerService.csproj -c Release -o .slt/publish
```

> **⚠️ IMPORTANT:** You must re-publish after ANY code changes to the worker. The Docker image is built from `.slt/publish/` — stale binaries mean your tests run old code.

### Run Tests
```bash
# Run specific worker's SLT tests
dotnet test tests/FinancialOrchestrator.SltTests --filter "TestCategory=SLT&FullyQualifiedName~BploSltTests"

# Run all SLT tests
dotnet test tests/FinancialOrchestrator.SltTests --filter "TestCategory=SLT"
```

### Timeout Guidance
- **First run (image pull):** 5-10 minutes — do NOT assume tests are hanging
- **Container startup (cached images):** ~30-60 seconds (image build + app warmup)
- **Queue processing:** ~10-30 seconds per message
- **Total test suite (5 tests, cached):** ~60-90 seconds
- **Recommended test timeout:** 45 seconds per `WaitForIdleAsync` call

---

## Adding a New Worker SLT (Checklist)

### Mechanical Discovery Steps (for agents)

Before writing any code, run these discovery steps:

1. **Find worker in slt.json**: `grep -i "workerName" .slt/slt.json` → get exact `name` and `queueName`
2. **Check effective queue name**: Look in `config.docker.json` → `AzureQueueWorkerConfiguration[]` for the queue mapping
3. **Find processor code**: Search for the worker's processor (folder structure varies):
   - Try: `src/FinancialOrchestrator.Worker/Processors/{Domain}/{SubDomain}/`
   - Better: search with `rg "class.*Processor" src/FinancialOrchestrator.Worker/Processors/ --files-with-matches | grep -i "<domain>"`
   - Example: NormalizedOrder is at `Processors/Normalized/Order/`, NOT `Processors/NormalizedOrderQueueWorker/`
4. **Identify blob/table dependencies**: Search processor code for:
   - `IBlobStore` / `IFileStore` / `GetBlobAsync` → need `.Blob("name")` in Wire()
   - `ITableStore` / `GetEntityAsync` / `QueryAsync` → need `.Table("name")` in Wire()
   - `IAdlsFileStore` / `AppendAsync` / `FlushAsync` → needs DFS mock in nginx
5. **Identify external API calls**: Search processor for `HttpClient` / `ICollector` / `ICatalog` / `IOrganization` calls
6. **Start minimal**: Wire with just `Queue + CollectorIndex + UseTokenAuth + MockApi("services", "CollectorUri")`
7. **Run with catch-all**: Use the standard catch-all pattern (see Discovery Method above) and inspect WireMock logs
8. **Add mocks incrementally**: Add specific mocks only for endpoints that cause failures
9. **Dump request bodies**: Use the debugging snippet below when body-matching is needed

1. [ ] Identify worker name, queue name from `slt.json` workers[] array
2. [ ] Find or create order fixture JSON (from unit tests at `tests/FinancialOrchestrator.UnitTests/Scenarios/`)
3. [ ] Determine required blob containers (check worker's processor code for blob access patterns)
4. [ ] Determine required mock responses:
   - Run with catch-all, inspect WireMock request log
   - Add specific mocks for each endpoint returning non-empty data
5. [ ] Create test file following the template in Step 2
6. [ ] Add scenario JSON files to `Scenarios/{WorkerName}/` with `CopyToOutputDirectory=Always` in csproj
7. [ ] Run test with timeout handler for initial debugging
8. [ ] Once passing, remove debug code and add assertion-based tests

---

## Debugging Tips

### Capture Worker Container Logs
```csharp
var logs = await _env.Application("fo-worker").GetLogsAsync();
Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
```

> **NOTE:** The application name must match what you used in `.AddApplication("fo-worker", ...)`. Always use `"fo-worker"` for the FO worker.

### Capture WireMock Requests
```csharp
// GetAllRequestsAsync does NOT exist — use these instead:
var callCount = await _env.MockApi("services").GetCallCountAsync("/v1.0/events");
Console.WriteLine($"Event search calls: {callCount}");

// For assertions:
await _env.MockApi("services").VerifyAsync("/v1.0/events", Times.AtLeast(2));
await _env.MockApi("services").VerifyAsync("/v8.0/products", Times.AtLeastOnce);

// Reset between test phases:
await _env.MockApi("services").ResetRequestLogAsync();
```

### Check Azurite Resources
```csharp
var queues = await _env.Storage().ListQueuesAsync();
var containers = await _env.Storage().ListContainersAsync();
var messages = await _env.Queue("myqueue").PeekAllAsync();
```

### Rebuild Docker Image After Code Changes
```powershell
dotnet publish src/FinancialOrchestrator.WorkerService -c Release -o .slt/publish
```
The library rebuilds the Docker image automatically when context changes.

---

## Quick Reference: Worker → Queue → Dependencies

| Worker | Queue | Key Mock Dependencies |
|--------|-------|----------------------|
| BigCatProductQueueWorkerV1 | bigcatproduct | BillingGroupSummary, Catalog, DFS |
| AssetQueueWorkerV1 | asset | Order (augmented), DFS |
| BillingPurchaseLineOrganizationQueueWorkerV1 | billingpurchaselineorganization | Organization, Catalog, BillingGroup |
| NormalizedOrderQueueWorker | normalizedorder | Organization (required), BillingRecord, BillingGroup, Tax |
| SubscriptionConversionQueueWorkerV1 | subscriptionconversion | Varies — use discovery method |
| ChargeQueueWorkerV1 | charge | Varies — use discovery method |
| QuoteQueueWorkerV1 | quote | Varies — use discovery method |
| CommitmentQueueWorkerV1 | commitment | Varies — use discovery method |

For workers not yet tested, follow the **Discovery Method** in Step 4 (catch-all + WireMock log inspection).

> **Note:** This table shows commonly-tested workers. The full `slt.json` manifest contains **38+ workers** including multiple Charge variants (`chargebilled`, `chargecollected`), `CustomerBilled`, `BalanceLot`, `DeltaLots`, `Scheduler`, `SellinSales`, `BillingGroupHierarchy`, and more. Always verify the exact worker name and queue from `slt.json`.

---

## Key Packages & Contracts

| Package | Key Types | Relevance |
|---------|-----------|-----------|
| `Microsoft.Crs.Financials.Orchestrator.StorageContracts` | `EventEntityV1` | Message envelope format |
| `Microsoft.Crs.Financials.Contracts.Order.V20201031` | `OrderEventBaseV20201031`, `OrderEventV20201031` | Order deserialization |
| `Microsoft.Crs.Financials.Contracts.Catalog.V8` | `CatalogV8`, `ProductV8` | Catalog response format |
| `Microsoft.Crs.Financials.Contracts.Organization.V8` | `OrganizationSummaryV8` | Org response (camelCase) |
| `CFS.DeltaScheduler.Worker` | DeltaScheduler config | Dead letter management |

---

## Summary of Hard-Won Knowledge

1. **Always base64-encode** the EventEntityV1 JSON before sending to queue
2. **PascalCase** for EventEntityV1 properties, **camelCase** for Organization responses
3. **`{"Product": {...}}`** wrapper for catalog — not flat, not array
4. **Body-matching** is essential for `/v1.0/events` which receives multiple different searches
5. **DFS must be added to nginx** — the checked-in `nginx.conf` does NOT have a DFS block; add it for MDC workers
6. **C10 = rejected** for BigCatProduct — always use C30 in fixtures
7. **Catch-all returns `{}`** for BPLO (object), but event search `/v1.0/events` should always return `[]` (array)
8. **`CollectorIndex` table** must exist in Azurite for all workers (even if empty)
9. **Queue names are lowercase** — Azure Storage lowercases automatically
10. **Docker network aliases**: apps use `mock-services`, nginx uses `wiremock` — both resolve to WireMock
11. **`GetAllRequestsAsync()` does NOT exist** — use `VerifyAsync(path, Times)`, `GetCallCountAsync(path)`, and `ResetRequestLogAsync()`
12. **Application name must match**: `.AddApplication("fo-worker", ...)` must match `.Wire("fo-worker", ...)` and `_env.Application("fo-worker")`
13. **OpenTelemetry/Geneva MUST be disabled** in config.docker.json — otherwise worker crashes with `metricAccountName cannot be null`
14. **Do NOT set `KEY_VAULT_URI` env var** — it causes silent catalog lookup failures (worker resolves secrets via KeyVault instead of WireMock). Use `KeyVaultMonitorEnabled: false` in config.docker.json instead.
15. **`DisplayCatalogUri` override is critical** — without it, mock paths need `/displaycatalog/v8.0/...` prefix
16. **Don't assert on `/v1.0/ingestion` for BPLO** — assert on `/v1.0/events` and `/v8.0/products` instead (proves processing ran)
17. **Docker must be running** before test execution — no helpful error message if Docker is stopped
18. **Re-publish after code changes** — `dotnet publish ... -o .slt/publish` must be re-run if worker code changed

---

## Appendix A: Complete Proven BPLO Test (Copy-Paste Ready)

This is the **exact code that passes all 5 tests** (verified). Copy this as your starting point:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinPlat.TestContainers;
using FinPlat.TestContainers.Builder;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FinancialOrchestrator.SltTests;

[TestClass]
[TestCategory("SLT")]
public class BploSltTests
{
    private static TestEnvironment _env = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var catalogResponse = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Scenarios", "Bplo", "CatalogResponse_PowerBIPro.json"));

        var sltContext = GetSltContext();
        var cert = new CertificateMaterial(
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.crt")),
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.key")));

        var orgSearchResponse = """
        [
            {
                "EventId": "org-slt-001",
                "AccountId": "a5db621e-6c87-4060-94d9-af3baec2fd4c",
                "EventType": "OrganizationSummary",
                "EventSource": "account-organization-2019-05-31",
                "Version": 1,
                "CreatedTimestamp": "2024-01-01T00:00:00Z",
                "EntryTimestamp": "2024-01-01T00:00:00Z",
                "Content": {
                    "id": "org-slt-001",
                    "accountId": "a5db621e-6c87-4060-94d9-af3baec2fd4c",
                    "version": 1,
                    "state": "active",
                    "createdTimestamp": "2024-01-01T00:00:00Z",
                    "updatedTimestamp": "2024-01-01T00:00:00Z",
                    "organizationType": "organization",
                    "audience": "commercial",
                    "legalEntity": {
                        "address": {
                            "country": "US",
                            "city": "Redmond",
                            "region": "WA",
                            "postalCode": "98052"
                        }
                    }
                }
            }
        ]
        """;

        _env = await new TestEnvironmentBuilder()
            .AddAzurite(opts =>
            {
                opts.UseTokenAuth = true;
                opts.ExternalCertificate = cert;
            })
            .AddMockApi("services", mock =>
            {
                mock.OnPost("/v1.0/events", "OrganizationSummary", 200, orgSearchResponse, priority: 1);
                mock.OnPost("/v1.0/events", 200, """[]""");
                mock.OnPost("/v1.0/ingestion", 200, """{ "status": "accepted" }""");
                mock.OnGet("/v8.0/products/CFQ7TTC0H9MP/0001/CFQ7TTC0KQ2Z", 200, catalogResponse);
                mock.OnAny("/billinggroups", 200, """[{ "fedGovSAP": 1 }]""");
                mock.OnUnmatched(200, """{}""");
            })
            .AddApplication("fo-worker", app =>
            {
                app.FromDockerfile(
                    dockerfilePath: "docker/Dockerfile.slt",
                    contextPath: GetSltContext());
                app.WithEnv("Environment", "Local");
                app.WithEnv("WorkerNames", "BillingPurchaseLineOrganizationQueueWorkerV1");
                // ⚠️ Do NOT set KEY_VAULT_URI — it causes silent catalog lookup failures
                app.WithEnv("DisplayCatalogUri", "http://mock-services:8080");
                app.WithEnv("BillingGroupUri", "http://mock-services:8080/billinggroups");
                app.WithEnv("Logging__LogLevel__Default", "Information");
                app.WithEnv("Logging__LogLevel__Microsoft", "Warning");
            })
            .Wire("fo-worker", wire =>
            {
                wire.Queue("billingpurchaselineorganization")
                    .Table("CollectorIndex")
                    .Blob("featureflag")
                    .Blob("catalogcachedatacfq7ttc0h9mp0001cfq7ttc0kq2z")
                    .UseTokenAuth()
                    .MockApi("services", "CollectorUri");
            })
            .BuildAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_env != null)
            await _env.DisposeAsync();
    }

    [TestInitialize]
    public async Task TestInit()
    {
        await _env.MockApi("services").ResetRequestLogAsync();
    }

    [TestMethod]
    public async Task BPLO_ClosedOrder_ProcessesAndCallsCollector()
    {
        var orderJson = await ReadScenarioFileAsync("Order_Creation_Closed.json");
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(orderJson));

        try
        {
            await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45);
        }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine("=== WORKER LOGS (timeout) ===");
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        Assert.IsTrue(await _env.Queue("billingpurchaselineorganization").IsEmptyAsync(),
            "Message should be dequeued from main queue");

        var mock = _env.MockApi("services");
        await mock.VerifyAsync("/v1.0/events", Times.AtLeast(2));
        await mock.VerifyAsync("/v8.0/products", Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task BPLO_MalformedMessage_DequeuedWithoutCrash()
    {
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity("{}"));
        await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 30);

        Assert.IsTrue(await _env.Queue("billingpurchaselineorganization").IsEmptyAsync(),
            "The worker should dequeue the malformed message without getting stuck");
    }

    [TestMethod]
    public async Task BPLO_WorkerRemainsHealthy_AfterProcessing()
    {
        var orderJson = await ReadScenarioFileAsync("Order_Creation_Closed.json");
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(orderJson));

        try { await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        var order2 = orderJson.Replace("SLT-BPLO-001", "SLT-BPLO-002");
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(order2));

        try { await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        Assert.IsTrue(await _env.Queue("billingpurchaselineorganization").IsEmptyAsync(),
            "Worker should still be alive and process the second message");
    }

    [TestMethod]
    public async Task BPLO_ClosedOrder_CallsCatalogApi()
    {
        var orderJson = await ReadScenarioFileAsync("Order_Creation_Closed.json");
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(orderJson));

        try { await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        await _env.MockApi("services").VerifyAsync("/v8.0/products", Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task BPLO_OrganizationCache_SecondEventSkipsReprocessing()
    {
        var orderJson = await ReadScenarioFileAsync("Order_Creation_Closed.json");
        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(orderJson));

        try { await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        var mock = _env.MockApi("services");
        var firstEventSearchCount = await mock.GetCallCountAsync("/v1.0/events");
        await mock.ResetRequestLogAsync();

        await _env.Queue("billingpurchaselineorganization").SendAsync(WrapInEventEntity(orderJson));

        try { await _env.WaitForIdleAsync(new[] { "billingpurchaselineorganization" }, timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            var logs = await _env.Application("fo-worker").GetLogsAsync();
            Console.WriteLine(string.Join("\n", logs.Split('\n').TakeLast(200)));
            throw;
        }

        Assert.IsTrue(await _env.Queue("billingpurchaselineorganization").IsEmptyAsync(),
            "Second message should be dequeued from main queue");

        await mock.VerifyAsync("/v1.0/events", Times.AtLeast(2));

        var ingestionCount = await mock.GetCallCountAsync("/v1.0/ingestion");
        var searchCount = await mock.GetCallCountAsync("/v1.0/events");
        Console.WriteLine($"Second event: {searchCount} search calls, {ingestionCount} ingestion calls");
        Console.WriteLine($"First event had: {firstEventSearchCount} search calls");
    }

    #region Helpers

    private static string WrapInEventEntity(string eventJson)
    {
        var eventNode = JsonNode.Parse(eventJson);
        var envelope = new JsonObject
        {
            ["Event"] = eventNode,
            ["EventType"] = "OrderCreation",
            ["Properties"] = new JsonObject
            {
                ["EventHubMessageId"] = Guid.NewGuid().ToString(),
                ["EventHubMessageQueueDate"] = DateTime.UtcNow.ToString("o")
            }
        };
        var json = envelope.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<string> ReadScenarioFileAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", "Bplo", fileName);
        return await File.ReadAllTextAsync(path);
    }

    private static string GetSltContext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".slt")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException(
                "Cannot find repo root with .slt directory.");

        return Path.Combine(dir.FullName, ".slt");
    }

    #endregion
}
```

### Required Fixture: `Scenarios/Bplo/Order_Creation_Closed.json`

This fixture works with the BPLO worker. Key requirements:
- `project.accountId` must match the org mock's `AccountId` (e.g., `"a5db621e-6c87-4060-94d9-af3baec2fd4c"`)
- `organizationId` can be any value (e.g., `"org-001"`) — doesn't need to match org mock's `id`
- **⚠️ `productType` MUST be `"BigCatProduct"`** — NOT `"SaaS"`, NOT `"Subscription"`, NOT anything else. The BPLO PurchaseLineItem processor routes based on this value and `"BigCatProduct"` is the only type that triggers the catalog lookup via `/v8.0/products`. Using any other value causes silent processing failures where the worker dequeues the message but skips catalog/ingestion.
- `operations[0].type` must be `"creation"` with `state: "closed"`
- `program_code` must NOT be `"C10"` — use `"C30"` or similar
- Product IDs (`CFQ7TTC0H9MP`, `0001`, `CFQ7TTC0KQ2Z`) must match catalog mock path

### Required Fixture: `Scenarios/Bplo/CatalogResponse_PowerBIPro.json`

Use a **real** DisplayCatalog V8 response (~32KB). Source: copy from unit tests or from another working SLT. Must contain `ProductId`, `DisplaySkuAvailabilities`, etc. The full 32KB response is needed because the worker deserializes deeply.

---

## Appendix B: Library API Quick Reference

| Method | Object | Purpose |
|--------|--------|---------|
| `SendAsync(base64msg)` | `_env.Queue("name")` | Send message to queue |
| `IsEmptyAsync()` | `_env.Queue("name")` | Check queue is drained |
| `GetMessageCountAsync()` | `_env.Queue("name")` | Get current message count |
| `WaitForIdleAsync(queues[], timeoutSeconds)` | `_env` | Wait for queues to drain |
| `VerifyAsync(path, Times)` | `_env.MockApi("name")` | Assert mock was called N times |
| `GetCallCountAsync(path)` | `_env.MockApi("name")` | Get exact call count for path |
| `ResetRequestLogAsync()` | `_env.MockApi("name")` | Clear request log (test isolation) |
| `GetLogsAsync()` | `_env.Application("name")` | Get container stdout/stderr |
| `DisposeAsync()` | `_env` | Tear down all containers |

**Methods that DO NOT EXIST (will cause compile errors):**
- ~~`GetAllRequestsAsync()`~~ — use `GetCallCountAsync` + `VerifyAsync` instead
- ~~`DeadLetterQueue("name")`~~ — no dead-letter queue accessor in the library
- ~~`PeekAllAsync()`~~ — not available on queue accessor

---

## Appendix C: config.docker.json Minimum Required Settings

If you ever need to create or update `config.docker.json`, these settings are **MANDATORY** to prevent worker crashes:

```json
{
    "OpenTelemetryConsoleEnabled": false,
    "OpenTelemetryCounterProviderEnabled": false,
    "OpenTelemetryGenevaEnabled": false,
    "OpenTelemetryTraceProviderEnabled": false,
    "GenevaCounterProviderEnabled": false,
    "KeyVaultUri": "http://mock-services:8080/keyvault",
    "KeyVaultMonitorEnabled": false,
    "StorageAccountEndpointSuffix": "azurite.local",
    "UseWorkloadIdentity": true,
    "CollectorUri": "http://mock-services:8080/",
    "DisplayCatalogUri": "http://mock-services:8080/displaycatalog"
}
```

Without the first 7 settings, the worker will crash on startup with configuration/null-reference errors. Always copy from the existing checked-in config rather than creating from scratch.

---

# Part 2: Multi-Service Full-Flow SLT (FO Worker + Real Collector.FD)

> **All sections above describe SINGLE-SERVICE SLT** — FO Worker only, with WireMock answering `/v1.0/ingestion`. **Part 2 describes MULTI-SERVICE FULL-FLOW SLT** where the real `Collector.FD` container replaces the mock and actually writes journal blobs to Azurite. Read Part 1 first; Part 2 builds on top of it.

## When to use multi-service vs single-service

| Need | Use |
| --- | --- |
| Verify FO worker dequeues, transforms, sends ingestion request | **Single-service** (Part 1) |
| Verify the wire contract between FO and Collector (real serialization, real auth headers, real validation) | **Multi-service** |
| Verify Collector writes the expected journal blob layout (`bigcatproduct-v3` etc.) | **Multi-service** |
| Catch regressions when either side changes its DTOs | **Multi-service** |
| Fast iteration on FO worker logic only | Single-service |

Multi-service tests take ~4 min total (vs ~30 s for single-service) because Docker has to build *two* images and start them sequentially. Use them as a smoke layer on top of the BPLO-style suite, not a replacement.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  MSTest Process                                                              │
│                                                                              │
│  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  ┌─────────────────────┐  │
│  │ Azurite  │  │ WireMock │  │  FO Worker (img) │  │ Collector.FD (img) │  │
│  │ (queue,  │  │  ─────── │  │  ─────────────── │  │  ───────────────── │  │
│  │  blob,   │  │ catalog  │  │  Dequeues msg    │  │  /v1.0/ingestion   │  │
│  │  table)  │  │ billing  │  │  HTTP POST →     │──▶  validates +       │  │
│  │  via TLS │  │ (no      │  │  CollectorUri    │  │  writes journals   │  │
│  │  proxy   │  │ ingest!) │  │                  │  │  to Azurite blob   │  │
│  └────┬─────┘  └────┬─────┘  └────────┬─────────┘  └─────────┬──────────┘  │
│       │             │                 │                       │             │
│       └─────────────┴─────────────────┴───────────────────────┘             │
└─────────────────────────────────────────────────────────────────────────────┘
```

Notes:
- **WireMock no longer answers `/v1.0/ingestion`** — that path is owned by the real Collector container (named `collector-fd`).
- `Wire.AppUrl("collector-fd", "CollectorUri")` injects the FD's container URL into the worker as the `CollectorUri` env var. The library handles network aliasing for you.
- Both Azurite (storage) and WireMock (upstream lookups) are still required.
- FD uses **HTTP-only on 8080** for the SLT — auth bypass is done via `IsAuthorizationRequired: false` + `ASPNETCORE_ENVIRONMENT=Development`.

## Prerequisites (in addition to Part 1)

You need a local clone of the Collector repo to build the FD image:

```
C:\Users\<you>\source\repos\
├── slt_testing\                       (your FO clone — same as Part 1)
└── BFG.Collector.EventCollector\      (Collector clone — needed for FD bits)
```

```powershell
git clone https://msazure.visualstudio.com/One/_git/BFG.Collector.EventCollector
cd BFG.Collector.EventCollector
# Pin SDK to match what's installed (same Issue 9 fix as FO)
@'
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
'@ | Set-Content global.json
```

You will need to **publish Collector.FD into your FO repo's `.slt/publish-fd/` directory**:

```powershell
# From BFG repo:
dotnet publish src\Collector.FD -c Release -o "C:\Users\<you>\source\repos\slt_testing\.slt\publish-fd"

# CRITICAL: dotnet publish PreserveNewest does NOT overwrite older-mtime configs.
# Force-copy after every republish:
Copy-Item "src\Collector.FD\Configuration\*.json" "...\slt_testing\.slt\publish-fd\Configuration\" -Force
Copy-Item "src\Collector.Orchestrator\Configuration\*.json" "...\slt_testing\.slt\publish-fd\OrchestratorConfiguration\" -Force
```

## ⚠️ Two REQUIRED BFG patches (local-only — do not commit)

BFG as-shipped will not boot Collector.FD in SLT mode. You must apply these two surgical patches to `BFG.Collector.EventCollector` locally:

### Patch 1 — Allow `Environment=docker` (`ServiceConfigurationTools.cs`)

The base code only knows about prod env names (`pme.prod.wus2`, `ame.prod.wus2`, etc.) and throws `ArgumentException("Unsupported value for environment variable 'Environment': docker")` otherwise. Add a `docker` case to **both** switches in this file (one in `LoadConfigurationForEnvironment`, one in `LoadAllConfigurationsForEnvironment`).

**File:** `src\Collector.Service.Common\Configuration\ServiceConfigurationTools.cs`

```diff
 case "pme.prod.wus2":
     AddSubConfigFile(configFilePaths, configFolder, "config.pme.prod.wus2", subConfigName);
     break;
+case "docker":
+    AddSubConfigFile(configFilePaths, configFolder, "config.docker", subConfigName);
+    break;
 default:
```

```diff
 case EnvironmentNames.PmeProdWestUs2:
     serviceConfigurations.Add(LoadConfigurationForEnvironment<TServiceConfiguration>(subConfigName, pathPrefix, EnvironmentNames.PmeProdWestUs2));
     break;
+case "docker":
+    serviceConfigurations.Add(LoadConfigurationForEnvironment<TServiceConfiguration>(subConfigName, pathPrefix, "docker"));
+    break;
 default:
```

### Patch 2 — Skip KeyVault adapter creation in Dev (`ServiceConfigurationUpdater.cs`)

In Dev/SLT mode, `DefaultAzureCredentialTokenProvider`'s ctor explodes with `"At least one credential type must be included"` because the package's `DefaultAzureCredentialOptions` excludes all sources by default. Move the KV/service-context construction **inside** the existing `!= "Development"` gate:

**File:** `src\Collector.Service.Common\Configuration\ServiceConfigurationUpdater.cs` (around line ~206)

```diff
-ILoggerFactory loggerFactory = serviceConfiguration.CreateLoggerFactory();
-IServiceContextFactory serviceContextFactory = serviceConfiguration.CreateServiceContextFactory();
-IVaultAdapter vaultAdapter = serviceConfiguration.CreateKeyVaultAdapter(
-    Environment.GetEnvironmentVariable("KEY_VAULT_URI") ?? serviceConfiguration.KeyVaultUri,
-    null, serviceContextFactory, loggerFactory,
-    serviceConfiguration.ManagedIdentityClientId, null, serviceConfiguration.WorkloadIdentityClientId);
-
-IServiceContext serviceContext = serviceContextFactory.CreateServiceContext();
-
 if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
 {
+    ILoggerFactory loggerFactory = serviceConfiguration.CreateLoggerFactory();
+    IServiceContextFactory serviceContextFactory = serviceConfiguration.CreateServiceContextFactory();
+    IVaultAdapter vaultAdapter = serviceConfiguration.CreateKeyVaultAdapter(
+        Environment.GetEnvironmentVariable("KEY_VAULT_URI") ?? serviceConfiguration.KeyVaultUri,
+        null, serviceContextFactory, loggerFactory,
+        serviceConfiguration.ManagedIdentityClientId, null, serviceConfiguration.WorkloadIdentityClientId);
+    IServiceContext serviceContext = serviceContextFactory.CreateServiceContext();
+
     // existing prod-only branch using vaultAdapter / serviceContext...
 }
```

Both patches together are ~10 lines. They are **defensible** (the dev-gate one is a real bug — KV creation should never run in Dev mode) but **do not commit them upstream** without a separate review. Republish FD after applying.

## File layout additions (on top of Part 1)

```
.slt/
  publish-fd/                              # output of `dotnet publish Collector.FD`
    Microsoft.Crs.Financials.Collector.FD.dll
    Configuration/                         # populated by force-copy from BFG
      config.base.json
      config.docker.json                   # ← from fd-config/Configuration, dropped in by Dockerfile
    OrchestratorConfiguration/
      config.base.json
      config.docker.json                   # ← from fd-config/OrchestratorConfiguration
    ...
  docker/
    Dockerfile.slt                         # FO worker (Part 1, unchanged)
    Dockerfile.fd.slt                      # NEW — Collector.FD image
    config.docker.json                     # FO worker config (Part 1 — register the worker here too!)
    fd-config/                             # NEW — FD overlays the dockerfile drops in
      Configuration/config.docker.json
      OrchestratorConfiguration/config.docker.json
    nginx/                                 # shared (Part 1)
tests/
  FinancialOrchestrator.SltTests/
    BploSltTests.cs                        # single-service (Part 1)
    FullFlowSltTests.cs                    # NEW — multi-service
    Scenarios/
      Bplo/                                # single-service
      BigCatProduct/                       # NEW — full-flow scenarios
        Order_Creation_Closed.json
        CatalogResponse_PowerBIPro.json
```

## `Dockerfile.fd.slt`

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0
WORKDIR /app

# Trust the self-signed storage-proxy cert (same as FO worker)
COPY docker/nginx/azurite.crt /etc/pki/ca-trust/source/anchors/azurite.crt
RUN update-ca-trust

# Published FD bits
COPY publish-fd/ .

# Drop SLT overlays in — these are loaded because Environment=docker
COPY docker/fd-config/Configuration/config.docker.json Configuration/config.docker.json
COPY docker/fd-config/OrchestratorConfiguration/config.docker.json OrchestratorConfiguration/config.docker.json

# Workload-identity stub so DefaultAzureCredential's options validation doesn't trip
RUN echo "dummy-federated-token" > /app/federated-token.txt

ENV Environment=docker
ENV ASPNETCORE_ENVIRONMENT=Development
ENV AZURE_CLIENT_ID=00000000-0000-0000-0000-000000000000
ENV AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000
ENV AZURE_CLIENT_SECRET=dummy-secret-for-slt
ENV AZURE_AUTHORITY_HOST=https://login.microsoftonline.com
ENV AZURE_FEDERATED_TOKEN_FILE=/app/federated-token.txt

EXPOSE 8080
ENTRYPOINT ["dotnet", "Microsoft.Crs.Financials.Collector.FD.dll"]
```

## `fd-config/Configuration/config.docker.json` (MANDATORY)

```json
{
    "IsAuthorizationRequired": false,
    "EnableHttp": true,
    "EnableHttps": false,
    "HttpPort": 8080,
    "EnableS2SAuthentication": false,
    "EnableCookieAuthentication": false,

    "KeyVaultUri": "https://dummy.vault.azure.net/",
    "KeyVaultMonitorEnabled": false,

    "OpenTelemetryGenevaEnabled": false,
    "OpenTelemetryTraceProviderEnabled": false,
    "OpenTelemetryCounterProviderEnabled": false,
    "OpenTelemetryConsoleEnabled": true,
    "EnableAuditLog": false,
    "UseWorkloadIdentity": false,
    "BlobStorageTimeout": "0.00:02:00.000",

    "KnownIngestionEventTypes": {
        "bigcatproduct-v3": [ "BigCatProduct" ]
    },

    "PartnerConfiguration": [
        { "ApplicationId": "00000000-0000-0000-0000-000000000001", "ObjectId": "00000000-0000-0000-0000-000000000001", "Name": "SLT" },
        { "ApplicationId": "33349fe2-44d3-47b3-b8c7-9bf8279cdf6b", "ObjectId": "d8a24b41-d537-47bb-95c6-f5aa46ead300", "Name": "FinancialOrchestrator" }
    ],
    "PartnerJournalIngestionMapping": {
        "SLT":                    { "IsUniversalIngestionSupported": true },
        "FinancialOrchestrator":  { "IsUniversalIngestionSupported": true }
    }
}
```

**Things you must NOT change:**
- `KnownIngestionEventTypes` keys must be **valid `JournalVersion` enum names** (lookup the enum in `Collector.Contracts`). Anything else → `JsonSerializationException: Could not convert string '...' to dictionary key type 'JournalVersion'`.
- Every `PartnerConfiguration` entry **must** have non-null `ApplicationId` AND `ObjectId`. `PartnerConfiguration.GetHashCode()` does `HashCodeExtensions.CombineHashCodes(ApplicationId, ObjectId, Name)` and NREs on nulls when the `HashSet` adds it.
- `KeyVaultUri` cannot be null/empty even though it's never used — an `IsNotNullOrWhiteSpace` guard throws otherwise. Use a dummy URI.
- `IsAuthorizationRequired: false` + `ASPNETCORE_ENVIRONMENT=Development` together activate `AllowAnonymousAuthorizationHandler`. Setting only one is insufficient.

## `fd-config/OrchestratorConfiguration/config.docker.json` (MANDATORY, minimal)

```json
{
    "StorageAccountConfiguration": [
        { "Name": "devstoreaccount1" }
    ],
    "KeyVaultUri": "https://dummy.vault.azure.net/",
    "KeyVaultMonitorEnabled": false,
    "OpenTelemetryGenevaEnabled": false,
    "OpenTelemetryTraceProviderEnabled": false,
    "OpenTelemetryCounterProviderEnabled": false
}
```

**Do NOT include:**
- `StorageConnectionString` — not on `OrchestratorServiceConfiguration` anymore; throws on bind.
- Any `EventGridAzureQueueWorkerConfiguration` entries — the base config has none, and any override entry triggers `[JsonRequired] EntityChangedPublisherConfiguration` validation, which you don't want to satisfy in SLT.

## Update the FO worker's `config.docker.json` too

The single-service tests only registered the worker you cared about (e.g., `BillingPurchaseLineOrganizationQueueWorkerV1`). For multi-service you need the additional worker that will be exercised end-to-end. Add it alongside the others:

```json
"AzureQueueWorkerConfiguration": [
    { "Name": "BillingPurchaseLineOrganizationQueueWorkerV1", "WorkerInstances": 1, "DeadLetterWorkerInstances": 0, "TestWorkerInstances": 0, "TestDeadLetterWorkerInstances": 0 },
    { "Name": "BigCatProductQueueWorkerV1",                    "WorkerInstances": 1, "DeadLetterWorkerInstances": 0, "TestWorkerInstances": 0, "TestDeadLetterWorkerInstances": 0 }
]
```

## The full-flow test pattern (`FullFlowSltTests.cs`)

The novel calls compared to Part 1 are:
- `AddApplication("collector-fd", …)` — a second container with its own dockerfile.
- `Wire("collector-fd", w => w.StorageConnectionString(...))` — FD also needs Azurite access.
- `Wire("fo-worker", w => w.AppUrl("collector-fd", "CollectorUri"))` — injects the FD's URL as the worker's `CollectorUri` env var, **overriding** the value in `config.docker.json`.
- The mock API **must not** define `/v1.0/ingestion` anymore — that's owned by the real FD.

```csharp
[TestClass]
[TestCategory("SLT"), TestCategory("FullFlow")]
public class FullFlowSltTests
{
    private const string JournalBlobContainer = "bigcatproduct-v3";
    private const string MockServicesAlias = "mock-services:8080";
    private static TestEnvironment _env = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var catalogResponse = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Scenarios", "BigCatProduct", "CatalogResponse_PowerBIPro.json"));

        var sltContext = GetSltContext(); // walks up to find `.slt`
        var cert = new CertificateMaterial(
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.crt")),
            await File.ReadAllTextAsync(Path.Combine(sltContext, "docker", "nginx", "azurite.key")));

        _env = await new TestEnvironmentBuilder()
            .AddAzurite(o => { o.UseTokenAuth = true; o.ExternalCertificate = cert; })
            .AddMockApi("services", mock =>
            {
                mock.OnGet("/v8.0/products/CFQ7TTC0H9MP/0001", 200, catalogResponse);
                mock.OnAny("/billinggroups", 200, """[{ "fedGovSAP": 1 }]""");
                mock.OnUnmatched(200, "[]");
                // DO NOT mock /v1.0/ingestion — the real Collector.FD owns it.
            })
            .AddApplication("collector-fd", app =>
            {
                app.FromDockerfile("docker/Dockerfile.fd.slt", sltContext);
                app.WithInternalPort(8080);
                app.WithHttpHealthCheck("/v1.0/ping", timeoutSeconds: 120);
            })
            .AddApplication("fo-worker", app =>
            {
                app.FromDockerfile("docker/Dockerfile.slt", sltContext);
                app.WithEnv("Environment", "Local");
                app.WithEnv("WorkerNames", "BigCatProductQueueWorkerV1");
                app.WithEnv("DisplayCatalogUri", $"http://{MockServicesAlias}/displaycatalog");
                app.WithEnv("BillingGroupUri",   $"http://{MockServicesAlias}/billinggroups");
            })
            .Wire("collector-fd", w =>
            {
                w.StorageConnectionString("StorageConnectionString")
                 .Blob(JournalBlobContainer);
            })
            .Wire("fo-worker", w =>
            {
                w.Queue("bigcatproductv1")
                 .Table("CollectorIndex")
                 .Blob("featureflag")
                 .Blob("catalogcachedatacfq7ttc0h9mp0001cfq7ttc0kq2z")
                 .Blob("bigcatproductv4")
                 .UseTokenAuth()
                 .AppUrl("collector-fd", "CollectorUri");
            })
            .BuildAsync();
    }

    [TestMethod]
    public async Task FullFlow_BigCatOrder_CollectorWritesJournalBlobs()
    {
        var order = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Scenarios", "BigCatProduct", "Order_Creation_Closed.json"));

        await _env.Queue("bigcatproductv1").SendAsync(WrapInEventEntity(order));

        await WaitForProcessingAsync(async () =>
        {
            var containers = await _env.Storage().ListContainersAsync();
            Assert.IsTrue(containers.Count > 0, "Collector.FD should have created journal containers");
            int total = 0;
            foreach (var c in containers)
                total += (await _env.Storage().Blobs(c).ListBlobsAsync()).Count;
            Assert.IsTrue(total > 0, "Collector.FD should have written at least one journal blob");
        }, timeoutSeconds: 90);
    }

    // ...QueueIsDrained and CollectorRemainsHealthy_AfterIngestion follow the same shape...

    [ClassCleanup]
    public static async Task ClassCleanup() { if (_env != null) await _env.DisposeAsync(); }

    private static string WrapInEventEntity(string eventJson)
    {
        var envelope = new JsonObject
        {
            ["Event"] = JsonNode.Parse(eventJson),
            ["EventType"] = "OrderCreation",
            ["Properties"] = new JsonObject
            {
                ["EventHubMessageId"] = Guid.NewGuid().ToString(),
                ["EventHubMessageQueueDate"] = DateTime.UtcNow.ToString("o")
            }
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
    }

    private static string GetSltContext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".slt"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Cannot find .slt root");
        return Path.Combine(dir.FullName, ".slt");
    }
}
```

## Common multi-service issues (the 6 fixes, ordered by how often they bite)

### MS-1: `ArgumentException: Unsupported value for environment variable 'Environment': docker`
Cause: BFG `ServiceConfigurationTools.cs` doesn't know `docker`. **Apply Patch 1 above.**

### MS-2: `InvalidOperationException: At least one credential type must be included in the authentication flow`
Cause: `DefaultAzureCredential` ctor in `CreateKeyVaultAdapter` even in Dev. **Apply Patch 2 above.**

### MS-3: `JsonSerializationException: Could not convert string 'foo-vX' to dictionary key type 'JournalVersion'`
Cause: `KnownIngestionEventTypes` key isn't a valid `JournalVersion` enum value. Look up the enum in `Collector.Contracts` and use only its declared members (e.g., `bigcatproduct-v3`, `billingpurchaselineorganization-v1`).

### MS-4: `NullReferenceException` at `PartnerConfiguration.GetHashCode` while building DI
Cause: a `PartnerConfiguration` entry has only `Name` set. Fill in dummy GUIDs for `ApplicationId` and `ObjectId`.

### MS-5: `StorageConnectionString` bind error on orchestrator config
Cause: `OrchestratorServiceConfiguration` no longer exposes `StorageConnectionString`. Remove it from `OrchestratorConfiguration/config.docker.json` — only `StorageAccountConfiguration` is needed.

### MS-6: `EntityChangedPublisherConfiguration` required validation failure
Cause: An `EventGridAzureQueueWorkerConfiguration` entry was added to the overlay. **Don't define any** in `OrchestratorConfiguration/config.docker.json` — base has none for a reason.

### MS-7 (operational): stale configs in `publish-fd/`
After re-running `dotnet publish` you might notice the new config didn't take effect. `PreserveNewest` only copies when source mtime is newer than dest. Always `Copy-Item -Force` your `Configuration/*.json` and `OrchestratorConfiguration/*.json` into `publish-fd/` after a publish.

## Running and expected timings

```powershell
# From repo root (after Patch 1+2 applied to BFG and FD published into .slt/publish-fd):
dotnet publish src\FinancialOrchestrator.WorkerService -o .slt\publish
dotnet test tests\FinancialOrchestrator.SltTests --filter "TestCategory=FullFlow"
```

Expected: 3 tests, **~4 minutes total** on a warm machine (Docker has to build both images first time). Running the full suite (BPLO single-service + FullFlow multi-service) is ~4 min combined because both test classes share the Docker build cache.

## Verification checklist

- [ ] `dotnet test --filter "TestCategory=FullFlow"` → 3/3 pass
- [ ] `dotnet test` (no filter) → all BPLO single-service tests still pass (no regression)
- [ ] FD container logs show `/v1.0/ping` returning 200 during the health-check phase
- [ ] After a test, `_env.Storage().ListContainersAsync()` reports `bigcatproduct-v3` (or your journal container) with ≥1 blob

## Cross-reference

- TLS cert / SAN list / nginx config / Azurite OAuth: see Part 1 ("Certificate Architecture", "Azurite OAuth Requirements")
- Mock API patterns (`OnGet`, `OnAny`, `OnUnmatched`): see Part 1 ("Step 4: Configure Mock Responses")
- EventEntityV1 envelope rules: see Part 1 ("Critical Concept: EventEntityV1 Message Envelope")
- Issue 9 (SDK pin): applies to BFG too — see Part 1

