# FinPlat.TestContainers

A .NET library for Docker-based Service Level Testing (SLT). Spins up real services (Azure Storage, WireMock, custom apps) in Docker containers for integration and end-to-end testing. Think **"Moq for microservices"** — fluent mock setup, rich assertions, typed request/response inspection.

## Features

- **Azurite Integration** — Queue, Blob, and Table storage with optional token auth (Azure Identity via nginx TLS proxy)
- **WireMock Stubs** — HTTP mock APIs with request capture and verification
- **Multi-App Support** — Run multiple Docker containers with dependency ordering, health checks, and inter-container networking
- **Mixed Auth Modes** — Per-app token auth or connection string auth against shared Azurite
- **Storage Accessors** — Read/write queues, blobs, and tables from test code
- **Moq-Style Assertions** — `Times.Once`, `VerifyAsync`, `AssertNotCalledAsync`, typed body deserialization
- **Fluent Mock Setup** — `OnPost("/api").Returns(200, body).When("filter").ReturnsSequence(...)`
- **Dead-Letter Queue** — Verify poison messages with `DeadLetterQueue("name").PeekAllAsync()`
- **WaitForIdle / Reset** — Smart polling until queues drain; full state reset between tests
- **Fluent Builder API** — Declarative test environment configuration
- **Fixture Engine** — Typed fixture builders, registry, JSON templates with variable substitution
- **Parallel Execution** — Resource isolation with Docker labels, unique naming, orphan cleanup
- **Log Assertions** — Container log verification (literal, regex, structured JSON, error detection)
- **CI Intelligence** — Git-diff-based test selection with file-to-test mapping
- **Test Dashboards** — TRX parser with markdown/JSON report generation
- **CLI Tool (`dotnet-slt`)** — Zero-config scaffolding: detects workers, APIs, storage from your project and generates all SLT boilerplate

## Quick Start

```csharp
var env = await new TestEnvironmentBuilder()
    .AddAzurite(opts => opts.UseTokenAuth = true)
    .AddMockApi("services", mock =>
    {
        mock.OnGet("/api/products", 200, productJson);
        mock.OnAny("/oauth2/v2.0/token", 200, tokenJson);
    })
    .AddApplication("my-worker", app =>
    {
        app.FromDockerfile("Dockerfile.slt", contextPath: ".slt/");
        app.WithEnv("WorkerName", "MyWorker");
    })
    .Wire("my-worker", wire =>
    {
        wire.Queue("my-queue").UseTokenAuth();
        wire.MockApi("services", "ExternalApiUri");
    })
    .BuildAsync();

// Send a message to the queue
await env.Queue("my-queue").SendAsync(base64Message);

// Wait for processing to complete
await env.WaitForIdleAsync(timeoutSeconds: 30);

// Verify the mock API was called exactly once
await env.MockApi("services").VerifyAsync("/api/products", Times.Once);

// Inspect the request body
var calls = await env.MockApi("services").GetCallsAsync("/api/products");
var body = calls[0].BodyAs<ProductRequest>();
Assert.AreEqual("SKU-123", body.ProductId);

// Check storage
var entity = await env.Table("cache").GetAsync("ORG-001", "data");
Assert.IsNotNull(entity);

// Verify nothing went to dead-letter
var dlq = await env.DeadLetterQueue("my-queue").PeekAllAsync();
Assert.AreEqual(0, dlq.Count);

// Cleanup
await env.DisposeAsync();
```

## Assertion Layer

The library provides a comprehensive assertion API inspired by Moq:

### Mock API Verification

```csharp
var mock = env.MockApi("services");

// Times-based verification (Moq-style)
await mock.VerifyAsync("/api/data", Times.Once);
await mock.VerifyAsync("/api/data", Times.Exactly(3));
await mock.VerifyAsync("/api/data", Times.AtLeast(1));
await mock.VerifyAsync("/api/data", Times.AtMost(5));
await mock.VerifyAsync("/api/data", Times.Between(2, 4));
await mock.VerifyAsync("/api/data", Times.Never);

// Convenience methods
await mock.AssertCalledAsync("/api/data");         // at least once
await mock.AssertCalledAsync("/api/data", 2);      // exactly 2 times
await mock.AssertNotCalledAsync("/api/other");     // never called

// Typed request body inspection
var calls = await mock.GetCallsAsync("/api/ingestion");
var payload = calls[0].BodyAs<IngestionEvent>();
Assert.AreEqual("Asset", payload.EventType);

// Bulk typed extraction
var allBodies = await mock.GetRequestBodiesAsAsync<OrderEvent>("/api/orders");
Assert.IsTrue(allBodies.All(o => o.Status == "Closed"));
```

### CapturedRequest Deep Inspection

```csharp
var request = (await mock.GetCallsAsync("/api/data"))[0];

// Typed deserialization
var body = request.BodyAs<MyDto>();

// String matching
Assert.IsTrue(request.BodyContains("order-123"));

// JSON path queries (dot-notation)
var eventType = request.JsonPathValue("$.events.0.type");
Assert.AreEqual("OrderClosed", eventType);

// Header access
Assert.IsNotNull(request.Authorization);
Assert.AreEqual("application/json", request.ContentType);
Assert.IsNotNull(request.CorrelationId);
```

### Queue Assertions

```csharp
// Typed message peek
var messages = await env.Queue("orders").PeekAllAsync();
Assert.AreEqual(1, messages.Count);
var order = messages[0].BodyAs<OrderEvent>();
Assert.AreEqual("ORD-123", order.OrderId);

// Dead-letter queue verification
var dlq = await env.DeadLetterQueue("orders").PeekAllAsync();
Assert.AreEqual(0, dlq.Count, "Valid messages should not dead-letter");

// Or access DLQ from queue accessor directly
var dlq2 = env.Queue("orders").DeadLetter();
Assert.IsTrue(await dlq2.IsEmptyAsync());
```

### Table Assertions

```csharp
// Get single entity
var entity = await env.Table("OrgCache").GetAsync("ORG-001", "data");
Assert.IsNotNull(entity);
Assert.AreEqual("Active", entity["Status"]);

// Query with OData filter
var results = await env.Table("OrgCache").QueryAsync("PartitionKey eq 'ORG-001'");
Assert.AreEqual(1, results.Count);

// Count entities
var count = await env.Table("OrgCache").CountAsync();
Assert.AreEqual(5, count);

// Check existence
Assert.IsTrue(await env.Table("OrgCache").ExistsAsync("ORG-001", "data"));
```

### Blob Assertions

```csharp
// Typed blob deserialization
var config = await env.Blob("configs").DownloadAsAsync<AppConfig>("settings.json");
Assert.AreEqual("production", config.Environment);

// JSON document for flexible querying
using var doc = await env.Blob("data").DownloadAsJsonAsync("output.json");
Assert.AreEqual("ok", doc.RootElement.GetProperty("status").GetString());

// Existence and count
Assert.IsTrue(await env.Blob("output").ExistsAsync("result.json"));
Assert.AreEqual(3, await env.Blob("output").GetBlobCountAsync());
```

### WaitForIdle & Reset

```csharp
// Wait until all queues are drained (messages processed)
await env.WaitForIdleAsync(timeoutSeconds: 30);

// Wait for specific queues only
await env.WaitForIdleAsync(new[] { "orders", "notifications" }, timeoutSeconds: 60);

// Reset state between tests
await env.ResetAsync();                        // Clear queues + reset mock logs
await env.ResetAsync(clearBlobs: true);        // Also clear blobs
await env.ResetAsync(clearTables: true);       // Also clear tables
await env.ResetAsync(clearBlobs: true, clearTables: true);  // Full reset
```

## Fluent Mock Setup

Configure mock responses with a builder pattern (Moq-style):

```csharp
await env.MockApi("services").SetupAsync(m =>
{
    // Simple response
    m.OnPost("/v1/ingestion").Returns(200, new { status = "accepted" });

    // Conditional matching — only when body contains specific text
    m.OnPost("/v1/search")
        .When("includeOpenOrder")
        .Returns(200, searchResultWithOrders);

    // Default fallback (no condition)
    m.OnPost("/v1/search").Returns(200, "[]");

    // Response sequences — different response per call
    m.OnGet("/v1/status").ReturnsSequence(
        (202, new { status = "processing" }),
        (202, new { status = "processing" }),
        (200, new { status = "complete" })
    );

    // Path pattern matching (regex)
    m.OnGet("/v1/orders/.*").AsPathPattern().Returns(200, orderDetail);

    // Verifiable — VerifyAll will check this was called
    m.OnPost("/v1/notify").Returns(200).Verifiable();
});
```

## Multi-App (Full-Flow) Example

```csharp
var env = await new TestEnvironmentBuilder()
    .AddAzurite(opts => opts.UseTokenAuth = true)
    .AddMockApi("stubs", mock => { /* ... */ })
    .AddApplication("service-a", app =>
    {
        app.FromDockerfile("docker/a/Dockerfile.slt", contextPath: ".slt/");
        app.WithInternalPort(8080);
        app.WithHttpHealthCheck("/health", timeoutSeconds: 60);
    })
    .AddApplication("service-b", app =>
    {
        app.FromDockerfile("docker/b/Dockerfile.slt", contextPath: ".slt/");
        app.WithInternalPort(8080);
        app.WithHttpHealthCheck("/ping", timeoutSeconds: 60);
    })
    .Wire("service-a", wire =>
    {
        wire.Queue("input-queue").UseTokenAuth();
        wire.AppUrl("service-b", "DownstreamUri");  // a → b
    })
    .Wire("service-b", wire =>
    {
        wire.StorageConnectionString();  // direct Azurite (no TLS)
    })
    .BuildAsync();
```

The builder automatically:
1. Resolves start order via topological sort (dependencies first)
2. Waits for health checks before starting dependent apps
3. Injects inter-container URLs as environment variables

## CLI Tool (`dotnet-slt`)

Zero-config scaffolding for new SLT projects:

```bash
# Install
dotnet tool install --global FinPlat.Slt.Cli

# Initialize — auto-detects workers, APIs, storage from your .NET project
dotnet-slt init

# Validate setup
dotnet-slt doctor

# Run tests
dotnet-slt run
```

### What `init` generates

From scanning your repo, the CLI generates a complete `.slt/` directory:
- `docker/Dockerfile.slt` — Multi-stage build with cert trust
- `docker/config.docker.json` — All config pointing to mock/azurite endpoints
- `docker/nginx/` — TLS proxy for token auth
- `tests/YourService.SltTests/` — Test project with scenarios
- `slt.json` — Manifest file (edit to customize)

### Manifest (`slt.json`)

The CLI is **fully manifest-driven** — no hardcoded service knowledge:

```json
{
  "serviceName": "FinancialOrchestrator",
  "docker": {
    "baseImage": "mcr.microsoft.com/dotnet/aspnet:8.0-azurelinux3.0",
    "entryDll": "FinancialOrchestrator.dll",
    "configTargetPath": "/app/config/config.docker.json"
  },
  "workers": [
    { "name": "AssetWorker", "queue": "asset-queue-v1" }
  ],
  "externalApis": [
    { "name": "CollectorUri", "path": "/v1.0/ingestion", "method": "POST" }
  ],
  "tables": ["OrgCache"],
  "blobContainers": ["output-data"]
}
```

## API Reference

### TestEnvironmentBuilder

| Method | Description |
|--------|-------------|
| `AddAzurite(Action<AzuriteOptions>)` | Add Azurite storage emulator |
| `AddMockApi(name, Action<MockApiConfig>)` | Add WireMock HTTP stub |
| `AddApplication(name, Action<ApplicationBuilder>)` | Add custom Docker app |
| `Wire(appName, Action<WiringBuilder>)` | Configure app's dependencies |
| `BuildAsync()` | Start all containers and return `TestEnvironment` |

### TestEnvironment

| Method | Description |
|--------|-------------|
| `Queue(name)` | Get queue accessor |
| `Blob(name)` | Get blob accessor |
| `Table(name)` | Get table accessor |
| `MockApi(name)` | Get mock API accessor (assertions + setup) |
| `Storage()` | Get generic storage accessor (discover resources) |
| `DeadLetterQueue(name)` | Get dead-letter queue accessor |
| `WaitForIdleAsync(timeout)` | Poll until all queues empty |
| `ResetAsync(blobs, tables)` | Reset state between tests |
| `GetLogsAsync(appName)` | Retrieve container logs |
| `DisposeAsync()` | Stop and remove all containers |

### MockApiAccessor

| Method | Description |
|--------|-------------|
| `VerifyAsync(path, Times)` | Moq-style call count verification |
| `AssertCalledAsync(path)` | Assert called at least once |
| `AssertNotCalledAsync(path)` | Assert never called |
| `GetCallsAsync(path)` | Get typed request list |
| `GetRequestBodiesAsAsync<T>(path)` | Get deserialized bodies |
| `SetupAsync(Action<MockSetupBuilder>)` | Fluent mock configuration |
| `ResetAllAsync()` | Reset mappings + request log |

### QueueAccessor

| Method | Description |
|--------|-------------|
| `SendAsync(message)` | Send message to queue |
| `SendBatchAsync(messages)` | Send multiple messages |
| `PeekMessagesAsync(max)` | Peek raw message strings |
| `PeekAllAsync()` | Peek as typed `QueueMessage` objects |
| `GetMessageCountAsync()` | Get approximate message count |
| `DeadLetter(suffix)` | Get DLQ accessor for this queue |
| `ClearAsync()` | Clear all messages |
| `IsEmptyAsync()` | Check if queue is empty |

### BlobAccessor

| Method | Description |
|--------|-------------|
| `UploadAsync(name, content)` | Upload blob |
| `DownloadAsStringAsync(name)` | Download as string |
| `DownloadAsAsync<T>(name)` | Download and deserialize |
| `DownloadAsJsonAsync(name)` | Download as JsonDocument |
| `ExistsAsync(name)` | Check blob exists |
| `ListBlobsAsync(prefix)` | List blob names |
| `GetBlobCountAsync(prefix)` | Count blobs |
| `ClearAsync()` | Delete all blobs |

### TableAccessor

| Method | Description |
|--------|-------------|
| `UpsertAsync(pk, rk, props)` | Upsert entity |
| `GetAsync(pk, rk)` | Get entity (null if not found) |
| `QueryAsync(filter)` | OData filter query |
| `CountAsync(filter)` | Count entities |
| `ExistsAsync(pk, rk)` | Check entity exists |
| `ClearAsync()` | Delete all entities |

### Times (Moq-style)

| Method | Matches when... |
|--------|-----------------|
| `Times.Once` | Called exactly 1 time |
| `Times.Never` | Never called |
| `Times.AtLeastOnce` | Called 1+ times |
| `Times.Exactly(n)` | Called exactly n times |
| `Times.AtLeast(n)` | Called n+ times |
| `Times.AtMost(n)` | Called ≤n times |
| `Times.Between(min, max)` | Called between min-max times |

## Fixture Engine

Create typed, deterministic test fixtures with the builder pattern:

```csharp
// Fluent builder
var order = new FixtureBuilder<OrderEvent>()
    .With(o => o.OrderId = "ORD-12345")
    .With(o => o.Status = "Closed")
    .Build();

// Serialize for queue messages
string json = new FixtureBuilder<OrderEvent>()
    .With(o => o.OrderId = "test-001")
    .BuildBase64Json();

// Registry for reusable templates
var registry = new FixtureRegistry();
registry.Register("closed-order", () => new OrderEvent { Status = "Closed" });
var fixture = registry.Create<OrderEvent>("closed-order");

// JSON template files with variable substitution
var message = FixtureFile.LoadBase64("Scenarios/order.json",
    new Dictionary<string, string> { ["orderId"] = "ORD-999" });
```

## Parallel Execution

Run tests in parallel without Docker resource collisions:

```csharp
var env = await new TestEnvironmentBuilder()
    .WithInstanceId()  // auto-generates unique prefix
    .AddAzurite()
    .AddApplication("my-service", app => { /* ... */ })
    .BuildAsync();
```

Clean up orphaned resources from failed test runs:

```csharp
var result = await CleanupEngine.CleanupOrphanedAsync(TimeSpan.FromHours(1));
Console.WriteLine($"Removed {result.TotalRemoved} orphaned resources");
```

## Log Assertions

Verify container log output with fluent assertions:

```csharp
var logAssert = await env.AssertLogsAsync("my-service");
logAssert
    .ContainsLine("Application started")
    .DoesNotContain("FATAL")
    .HasNoErrors()
    .HasSequence("Initializing", "Ready", "Processing")
    .Verify();

// Polling — wait for a log line to appear
await env.WaitForLogLineAsync("my-service", "Processing complete", TimeSpan.FromSeconds(30));

// Structured JSON log analysis
var entries = logAssert.ParseStructuredLogs();
logAssert.HasNoStructuredLogsAtOrAbove(LogSeverity.Error).Verify();
```

## CI Intelligence

Select tests based on git changes:

```csharp
var result = await new TestSelector()
    .WithRepoRoot(".")
    .WithBaseBranch("origin/main")
    .MapDirectory("src/Workers/BigCat", "BigCatProductSltTests")
    .MapDirectory("src/Workers/Asset", "AssetSltTests")
    .MapFiles("*.csproj", "BigCatProductSltTests", "AssetSltTests")
    .SelectAsync();

Console.WriteLine($"Filter: dotnet test --filter \"{result.ToDotnetTestFilter()}\"");
```

## Requirements

- .NET 8.0+
- Docker Desktop (Linux containers)
- For token auth: nginx TLS proxy + Azurite HTTPS support
