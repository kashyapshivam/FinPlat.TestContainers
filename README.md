# FinPlat.TestContainers

A .NET library for Docker-based Service Level Testing (SLT). Spins up real services (Azure Storage, WireMock, custom apps) in Docker containers for integration and end-to-end testing.

## Features

- **Azurite Integration** — Queue, Blob, and Table storage with optional token auth (Azure Identity via nginx TLS proxy)
- **WireMock Stubs** — HTTP mock APIs with request capture and verification
- **Multi-App Support** — Run multiple Docker containers with dependency ordering, health checks, and inter-container networking
- **Mixed Auth Modes** — Per-app token auth or connection string auth against shared Azurite
- **Storage Accessors** — Read/write queues, blobs, and tables from test code
- **Fluent Builder API** — Declarative test environment configuration
- **Fixture Engine** — Typed fixture builders, registry, JSON templates with variable substitution
- **Parallel Execution** — Resource isolation with Docker labels, unique naming, orphan cleanup
- **Log Assertions** — Container log verification (literal, regex, structured JSON, error detection)
- **CI Intelligence** — Git-diff-based test selection with file-to-test mapping
- **Test Dashboards** — TRX parser with markdown/JSON report generation
- **Scaffolding** — Generate Dockerfile, config, and test class boilerplate for new services

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

// Verify the mock API was called
await env.MockApi("services").AssertCalledAsync("/api/products");

// Check storage
var blobs = await env.Storage().Blobs("my-container").ListBlobsAsync();
Assert.IsTrue(blobs.Count > 0);

// Cleanup
await env.DisposeAsync();
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

## API Reference

### TestEnvironmentBuilder

| Method | Description |
|--------|-------------|
| `AddAzurite(Action<AzuriteOptions>)` | Add Azurite storage emulator |
| `AddMockApi(name, Action<MockApiConfig>)` | Add WireMock HTTP stub |
| `AddApplication(name, Action<ApplicationBuilder>)` | Add custom Docker app |
| `Wire(appName, Action<WiringBuilder>)` | Configure app's dependencies |
| `BuildAsync()` | Start all containers and return `TestEnvironment` |

### ApplicationBuilder

| Method | Description |
|--------|-------------|
| `FromDockerfile(path, contextPath)` | Build from Dockerfile |
| `WithEnv(key, value)` | Set environment variable |
| `WithInternalPort(port)` | Declare the app's listening port |
| `WithHttpHealthCheck(path, timeoutSeconds)` | HTTP health check endpoint |

### WiringBuilder

| Method | Description |
|--------|-------------|
| `Queue(name)` | Wire app to an Azurite queue |
| `UseTokenAuth()` | Use Azure Identity token auth |
| `MockApi(name, envVar)` | Inject mock API URL as env var |
| `AppUrl(targetApp, envVar)` | Inject another app's URL as env var |
| `StorageConnectionString()` | Inject Azurite connection string (no TLS) |

### TestEnvironment

| Method | Description |
|--------|-------------|
| `Queue(name)` | Get queue accessor for sending/reading messages |
| `MockApi(name)` | Get mock API for assertions and request capture |
| `Storage()` | Get storage accessor for blobs/tables |
| `App(name)` | Get app container (ports, logs) |
| `GetLogsAsync(appName)` | Retrieve container stdout/stderr |
| `DisposeAsync()` | Stop and remove all containers |

### StorageAccessor

| Method | Description |
|--------|-------------|
| `Blobs(containerName)` | Get blob accessor for a container |
| `ListContainersAsync()` | List all blob containers |

### BlobAccessor

| Method | Description |
|--------|-------------|
| `ListBlobsAsync()` | List all blobs in the container |
| `GetBlobCountAsync()` | Count blobs in the container |

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
    .MapFiles("*.csproj", "BigCatProductSltTests", "AssetSltTests")  // config change → run all
    .SelectAsync();

Console.WriteLine($"Changed: {result.ChangedFiles.Count} files");
Console.WriteLine($"Running: {string.Join(", ", result.SelectedTests)}");
Console.WriteLine($"Filter: dotnet test --filter \"{result.ToDotnetTestFilter()}\"");
```

## Test Dashboards

Parse TRX results and generate reports:

```csharp
var report = TestResultParser.ParseTrx("TestResults/results.trx");
Console.WriteLine(report.ToMarkdownSummary());  // Markdown table with pass rate, failures, slowest tests
File.WriteAllText("report.json", report.ToJsonSummary());

// Merge multiple TRX files
var merged = TestResultParser.ParseMultipleTrx(
    TestResultParser.FindTrxFiles("TestResults/"));
```

## Scaffolding

Generate SLT boilerplate for a new service:

```csharp
await SltScaffolder.GenerateAsync(new ScaffoldOptions
{
    ServiceName = "order-service",
    OutputDirectory = ".slt",
    TestProjectDirectory = "tests/OrderService.SltTests",
    QueueNames = { "order-queue-v1" },
    MockApis = { "CatalogUri", "BillingUri" },
    ServicePort = 8080,
    HealthCheckPath = "/health",
});
// Generates: .slt/docker/Dockerfile.slt, config.docker.json,
//            tests/OrderService.SltTests/OrderServiceSltTests.cs,
//            .slt/Scenarios/OrderService/happy-path.json
```

## Requirements

- .NET 8.0+
- Docker Desktop (Linux containers)
- For token auth: nginx TLS proxy + Azurite HTTPS support
