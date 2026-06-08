# SLT Skills Guide

A practical guide for writing Service Level Tests using FinPlat.TestContainers.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  Test Process (MSTest / xUnit)                       │
│  ┌─────────────────────────────────────────────────┐ │
│  │ TestEnvironmentBuilder                          │ │
│  │   .AddAzurite()     → Azurite Container         │ │
│  │   .AddMockApi()     → WireMock Container        │ │
│  │   .AddApplication() → Your App Container        │ │
│  │   .Wire()           → Environment Variables     │ │
│  └─────────────────────────────────────────────────┘ │
│                                                       │
│  ┌─ Docker Network ─────────────────────────────────┐ │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────────┐   │ │
│  │  │ Azurite  │  │ WireMock │  │  Your App    │   │ │
│  │  │ (queues, │  │ (HTTP    │  │  (real code  │   │ │
│  │  │  blobs,  │◄─┤  mocks)  │◄─┤   in Docker) │   │ │
│  │  │  tables) │  │          │  │              │   │ │
│  │  └──────────┘  └──────────┘  └──────────────┘   │ │
│  │         ▲              ▲             │           │ │
│  │         │    nginx     │             │           │ │
│  │         └── (TLS) ─────┘             │           │ │
│  └──────────────────────────────────────────────────┘ │
│                                                       │
│  Assertions:                                          │
│    env.Queue("x").PeekAllAsync()                      │
│    env.MockApi("y").VerifyAsync("/path", Times.Once)  │
│    env.Table("z").GetAsync("pk", "rk")                │
└───────────────────────────────────────────────────────┘
```

## Skill 1: Writing Your First SLT

### Step 1: Create the test project

```bash
dotnet new mstest -n MyService.SltTests
cd MyService.SltTests
dotnet add reference ../path/to/FinPlat.TestContainers.csproj
```

### Step 2: Create a Dockerfile for your service

```dockerfile
# .slt/docker/Dockerfile.slt
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ .
COPY config.docker.json /app/config/config.docker.json
ENTRYPOINT ["dotnet", "MyService.dll"]
```

### Step 3: Write the test

```csharp
[TestClass]
public class MyWorkerSltTests
{
    private static TestEnvironment _env = null!;

    [ClassInitialize]
    public static async Task Setup(TestContext _)
    {
        _env = await new TestEnvironmentBuilder()
            .AddAzurite(opts => opts.UseTokenAuth = true)
            .AddMockApi("downstream", mock =>
            {
                mock.OnAny("/oauth2/v2.0/token", 200, TokenFixture.ValidToken);
                mock.OnPost("/api/process", 200, "{}");
            })
            .AddApplication("my-worker", app =>
            {
                app.FromDockerfile("docker/Dockerfile.slt", contextPath: ".slt/");
                app.WithEnv("WorkerName", "MyWorker");
            })
            .Wire("my-worker", wire =>
            {
                wire.Queue("input-queue").UseTokenAuth();
                wire.MockApi("downstream", "DownstreamApiUri");
            })
            .BuildAsync();
    }

    [ClassCleanup]
    public static async Task Cleanup() => await _env.DisposeAsync();

    [TestInitialize]
    public async Task ResetState() => await _env.ResetAsync();

    [TestMethod]
    public async Task HappyPath_ProcessesMessage()
    {
        // Arrange
        var message = CreateTestMessage("order-123");

        // Act
        await _env.Queue("input-queue").SendAsync(message);
        await _env.WaitForIdleAsync(timeoutSeconds: 30);

        // Assert
        await _env.MockApi("downstream").VerifyAsync("/api/process", Times.Once);
        var calls = await _env.MockApi("downstream").GetCallsAsync("/api/process");
        Assert.IsTrue(calls[0].BodyContains("order-123"));
    }

    [TestMethod]
    public async Task InvalidMessage_GoesToDeadLetter()
    {
        // Arrange
        var badMessage = "invalid-json-garbage";

        // Act
        await _env.Queue("input-queue").SendAsync(badMessage);
        await Task.Delay(5000); // Wait for processing attempt

        // Assert
        await _env.MockApi("downstream").AssertNotCalledAsync("/api/process");
        var dlq = await _env.DeadLetterQueue("input-queue").PeekAllAsync();
        Assert.IsTrue(dlq.Count > 0);
    }
}
```

## Skill 2: Mock API Setup Patterns

### Pattern: Conditional responses based on body content

```csharp
await _env.MockApi("search").SetupAsync(m =>
{
    // When searching for "gold" products, return results
    m.OnPost("/v1/search").When("gold").Returns(200, goldProducts);

    // Default: empty results
    m.OnPost("/v1/search").Returns(200, "[]");
});
```

### Pattern: Simulating retry scenarios

```csharp
await _env.MockApi("flaky").SetupAsync(m =>
{
    // First call fails, second succeeds (tests retry logic)
    m.OnPost("/api/submit").ReturnsSequence(
        (500, new { error = "internal" }),
        (200, new { id = "created-123" })
    );
});
```

### Pattern: Auth token mock (required for most workers)

```csharp
mock.OnAny("/oauth2/v2.0/token", 200, JsonSerializer.Serialize(new
{
    access_token = "test-token",
    token_type = "Bearer",
    expires_in = 3600
}));
```

## Skill 3: Assertion Patterns

### Verify exact call count

```csharp
await _env.MockApi("api").VerifyAsync("/ingestion", Times.Exactly(2));
```

### Verify no calls (negative test)

```csharp
await _env.MockApi("api").AssertNotCalledAsync("/should-not-call");
```

### Inspect request body

```csharp
var calls = await _env.MockApi("api").GetCallsAsync("/ingestion");
var payload = calls[0].BodyAs<IngestionPayload>();
Assert.AreEqual("Asset", payload.EventType);
Assert.AreEqual("ORG-001", payload.OrganizationId);
```

### Verify table was written

```csharp
var entity = await _env.Table("OrgCache").GetAsync("ORG-001", "cache");
Assert.IsNotNull(entity);
Assert.AreEqual("Microsoft", entity["Name"]);
```

### Verify blob was created

```csharp
Assert.IsTrue(await _env.Blob("output").ExistsAsync("result.json"));
var result = await _env.Blob("output").DownloadAsAsync<ProcessResult>("result.json");
Assert.AreEqual("success", result.Status);
```

### Verify dead-letter is empty (happy path)

```csharp
var dlq = await _env.DeadLetterQueue("orders").PeekAllAsync();
Assert.AreEqual(0, dlq.Count, "Valid message should not dead-letter");
```

## Skill 4: Test Isolation

### Reset between tests

```csharp
[TestInitialize]
public async Task ResetState()
{
    // Minimum: clear queues and mock logs
    await _env.ResetAsync();
}
```

### Full reset (when tests share blob/table state)

```csharp
[TestInitialize]
public async Task FullReset()
{
    await _env.ResetAsync(clearBlobs: true, clearTables: true);
}
```

## Skill 5: Organization Cache Pattern

Many FO workers check if an organization exists in cache before processing:

```csharp
[TestMethod]
public async Task CachedOrg_SkipsLookup()
{
    // Pre-populate cache
    await _env.Table("OrgCache").UpsertAsync("ORG-001", "cache",
        new Dictionary<string, object>
        {
            ["Name"] = "TestOrg",
            ["Status"] = "Active"
        });

    // Send event for same org
    await _env.Queue("worker-queue").SendAsync(eventForOrg001);
    await _env.WaitForIdleAsync();

    // Should NOT call org-search API (cache hit)
    await _env.MockApi("services").AssertNotCalledAsync("/v1/org-search");

    // Should still process downstream
    await _env.MockApi("services").VerifyAsync("/v1/ingestion", Times.Once);
}
```

## Skill 6: Debugging Failed Tests

### Check container logs

```csharp
var logs = await _env.GetLogsAsync("my-worker");
Console.WriteLine(logs);  // Printed in test output
```

### Check Azurite logs

```csharp
var azLogs = await _env.GetAzuriteLogsAsync();
Console.WriteLine(azLogs);
```

### Check proxy logs

```csharp
var proxyLogs = await _env.GetProxyLogsAsync();
Console.WriteLine(proxyLogs);
```

### Common issues

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| Worker doesn't start | Config missing/wrong | Check `config.docker.json` paths |
| 401 on storage | Token auth misconfigured | Verify nginx proxy + cert trust |
| 0 API calls | Worker crashed silently | Check `GetLogsAsync` for exceptions |
| Timeout in WaitForIdle | Message stuck in queue | Worker may have errored — check logs |
| DLQ has messages | Deserialization failure | Check message format matches worker expectation |

## Skill 7: Using the CLI

```bash
# Scan repo and generate everything
dotnet-slt init

# Check for issues
dotnet-slt doctor

# Edit slt.json to customize (add more workers, APIs, etc.)
# Then re-run init to regenerate
dotnet-slt init

# Run the tests
dotnet-slt run
```

## Skill 8: Project Structure

```
my-service/
├── src/
│   └── MyService/
│       ├── Workers/
│       │   ├── OrderWorker.cs
│       │   └── AssetWorker.cs
│       └── MyService.csproj
├── tests/
│   └── MyService.SltTests/
│       ├── OrderWorkerSltTests.cs
│       ├── AssetWorkerSltTests.cs
│       └── MyService.SltTests.csproj
├── .slt/
│   ├── docker/
│   │   ├── Dockerfile.slt
│   │   ├── config.docker.json
│   │   └── nginx/
│   │       ├── Dockerfile.nginx
│   │       ├── nginx.conf
│   │       └── certs/
│   ├── Scenarios/
│   │   ├── OrderWorker/
│   │   │   └── happy-path.json
│   │   └── AssetWorker/
│   │       └── happy-path.json
│   └── publish.ps1
└── slt.json  ← manifest (source of truth)
```

## Skill 9: Token Auth Setup

For workers using Azure.Identity (DefaultAzureCredential):

1. **nginx TLS proxy** terminates HTTPS and adds `Authorization: Bearer` header
2. **Azurite** accepts the token (in OAuth mode)
3. **Worker** uses DefaultAzureCredential → gets token from mock endpoint → accesses storage via TLS

```csharp
.AddAzurite(opts => opts.UseTokenAuth = true)
.Wire("my-worker", wire =>
{
    wire.Queue("my-queue").UseTokenAuth();  // Routes through TLS proxy
})
```

Environment variables injected:
- `AZURE_TENANT_ID` = test tenant
- `AZURE_CLIENT_ID` = test client  
- `AZURE_CLIENT_SECRET` = test secret
- Storage endpoints → `https://nginx-proxy:10000/...`

## Skill 10: Talking to Live Containers from Test Code

Sometimes a test needs to reach into a running app container directly — to hit a health endpoint, an admin API, or to inspect logs after a failure. Use the `Application(...)` accessor on `TestEnvironment`.

### Hit an HTTP endpoint on an app container

The framework binds each app's exposed port to a random host port. Resolve it via `GetMappedPort` and build a `http://localhost:<port>` URL — the host can always reach it.

```csharp
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

var port = _env.Application("my-service").GetMappedPort(8080);
using var response = await httpClient.GetAsync($"http://localhost:{port}/v1.0/ping");

Assert.AreEqual(200, (int)response.StatusCode,
    "Service should still be healthy after processing");
```

**Use this when:**
- Verifying a downstream service stayed alive after a failure scenario (malformed message, retry storm, etc.)
- Calling an admin/diagnostics endpoint not exposed via a queue/mock-api flow
- Asserting service readiness independently of the `WithHttpHealthCheck` boot-time check

**Do NOT use this when** another container needs to call the app — that's what `wire.AppUrl("target-app", "EnvVar")` is for (inter-container DNS via the shared network).

### Read container logs from a test

```csharp
var logs = await _env.Application("my-service").GetLogsAsync();
Console.WriteLine(logs);   // surfaces in test output

// Or use the higher-level log assertions
var assertions = await _env.AssertLogsAsync("my-service");
assertions.DoesNotContain("FATAL").HasNoErrors().Verify();
```

### `ManagedAppContainer` API (returned by `Application(...)`)

| Member | Use |
|---|---|
| `Name` | Container name / network alias |
| `GetMappedPort(int containerPort)` | Host-side port for an exposed container port |
| `GetLogsAsync()` | Combined stdout + stderr as a string |
