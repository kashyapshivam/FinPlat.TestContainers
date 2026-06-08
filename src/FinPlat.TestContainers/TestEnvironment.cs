using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using FinPlat.TestContainers.Builder;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;
using FinPlat.TestContainers.Fluent;
using DotNet.Testcontainers.Networks;

namespace FinPlat.TestContainers;

/// <summary>
/// The runtime test environment returned by <see cref="Builder.TestEnvironmentBuilder.BuildAsync"/>.
/// Provides accessors for sending messages, verifying state, and inspecting containers.
/// Dispose this object to tear down all containers and the Docker network.
/// </summary>
public class TestEnvironment : IAsyncDisposable
{
    private readonly INetwork _network;
    private readonly ManagedAzuriteContainer? _azurite;
    private readonly Dictionary<string, ManagedWireMockContainer> _wireMockContainers;
    private readonly Dictionary<string, ManagedAppContainer> _appContainers;
    private readonly Dictionary<string, int> _appHttpPorts;
    private readonly CertificateMaterial? _cert;
    private readonly ManagedTlsProxyContainer? _tlsProxy;
    private readonly Dictionary<string, ContainerDebugInfo> _debugInfos;

    internal TestEnvironment(
        INetwork network,
        ManagedAzuriteContainer? azurite,
        Dictionary<string, ManagedWireMockContainer> wireMockContainers,
        Dictionary<string, ManagedAppContainer> appContainers,
        Dictionary<string, int> appHttpPorts,
        CertificateMaterial? cert = null,
        ManagedTlsProxyContainer? tlsProxy = null,
        Dictionary<string, ContainerDebugInfo>? debugInfos = null)
    {
        _network = network;
        _azurite = azurite;
        _wireMockContainers = wireMockContainers;
        _appContainers = appContainers;
        _appHttpPorts = appHttpPorts;
        _cert = cert;
        _tlsProxy = tlsProxy;
        _debugInfos = debugInfos ?? new Dictionary<string, ContainerDebugInfo>();
    }

    /// <summary>
    /// Gets a <see cref="QueueAccessor"/> for sending and peeking messages in the specified queue.
    /// </summary>
    /// <param name="queueName">Name of the queue (must have been declared via wiring).</param>
    public QueueAccessor Queue(string queueName)
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        return new QueueAccessor(_azurite.ConnectionString, queueName, _cert is not null);
    }

    /// <summary>
    /// Gets a <see cref="BlobAccessor"/> for uploading and downloading blobs in the specified container.
    /// </summary>
    /// <param name="containerName">Name of the blob container (must have been declared via wiring).</param>
    public BlobAccessor Blob(string containerName)
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        return new BlobAccessor(_azurite.ConnectionString, containerName, _cert is not null);
    }

    /// <summary>
    /// Gets a <see cref="TableAccessor"/> for upserting and querying entities in the specified table.
    /// </summary>
    /// <param name="tableName">Name of the table (must have been declared via wiring).</param>
    public TableAccessor Table(string tableName)
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        return new TableAccessor(_azurite.ConnectionString, tableName, _cert is not null);
    }

    /// <summary>
    /// Gets a <see cref="MockApiAccessor"/> for asserting calls made to the specified mock API.
    /// </summary>
    /// <param name="name">Name of the mock API (must match name passed to AddMockApi).</param>
    public MockApiAccessor MockApi(string name)
    {
        if (!_wireMockContainers.TryGetValue(name, out var container))
            throw new InvalidOperationException($"Mock API '{name}' was not registered.");

        return new MockApiAccessor(container);
    }

    /// <summary>
    /// Gets the managed application container by name.
    /// </summary>
    /// <param name="name">The application name passed to AddApplication.</param>
    public ManagedAppContainer Application(string name)
    {
        if (!_appContainers.TryGetValue(name, out var container))
            throw new InvalidOperationException($"Application '{name}' was not registered.");

        return container;
    }

    /// <summary>
    /// Gets a <see cref="StorageAccessor"/> for accessing Azure Storage resources in Azurite
    /// without pre-declaring specific containers/queues/tables.
    /// Useful for verifying data written by application containers.
    /// </summary>
    public StorageAccessor Storage()
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        return new StorageAccessor(_azurite.ConnectionString, _cert is not null);
    }

    /// <summary>
    /// Executes one or more seed steps (table rows, blobs, HTTP POSTs) against the
    /// running environment. Use this for per-test seeding when build-time seeding
    /// isn't appropriate (e.g. fixtures depend on the test name).
    /// </summary>
    /// <param name="configure">Action that registers seed steps via <see cref="Builder.SeedBuilder"/>.</param>
    public async Task SeedAsync(Action<Builder.SeedBuilder> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var builder = new Builder.SeedBuilder();
        configure(builder);
        if (builder.Config.Steps.Count == 0) return;

        await Builder.SeedExecutor.ExecuteAsync(
            builder.Config,
            _azurite,
            _appContainers,
            _appHttpPorts,
            bypassSsl: _azurite?.IsTokenAuthMode ?? false,
            Console.Out);
    }

    /// <summary>
    /// Gets container logs for the specified application. Useful for debugging failures.
    /// </summary>
    /// <param name="appName">Name of the application.</param>
    public async Task<string> GetLogsAsync(string appName)
    {
        if (!_appContainers.TryGetValue(appName, out var container))
            throw new InvalidOperationException($"Application '{appName}' was not registered.");

        return await container.GetLogsAsync();
    }

    /// <summary>
    /// Returns the in-container debug attach details for an application that was registered
    /// with <see cref="Builder.TestEnvironmentBuilder.AttachableInDebugger"/>.
    /// Returns <c>null</c> if the application is not configured for in-container debugging.
    /// </summary>
    /// <param name="appName">Name of the application.</param>
    public ContainerDebugInfo? GetContainerDebugInfo(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("App name is required.", nameof(appName));

        return _debugInfos.TryGetValue(appName, out var info) ? info : null;
    }

    /// <summary>
    /// Blocks the calling thread until a <c>vsdbg</c> process appears inside the named
    /// application's container, indicating that an external debugger (VS Code / Visual Studio)
    /// has successfully attached. Useful at the start of a test to pause execution while
    /// the user lines up their breakpoints.
    /// </summary>
    /// <param name="appName">Name of the application registered via AttachableInDebugger.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 5 minutes.</param>
    /// <exception cref="TimeoutException">If no vsdbg process is observed within the timeout.</exception>
    public async Task WaitForDebuggerAsync(string appName, TimeSpan? timeout = null)
    {
        var info = GetContainerDebugInfo(appName)
            ?? throw new InvalidOperationException(
                $"Application '{appName}' is not registered for in-container debugging. " +
                "Call AttachableInDebugger(\"" + appName + "\") on the builder before BuildAsync.");

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        Console.WriteLine($"[FinPlat.TestContainers] Waiting for debugger to attach to '{appName}' (container: {info.ContainerName})...");

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsVsDbgRunningAsync(info.ContainerName))
            {
                Console.WriteLine($"[FinPlat.TestContainers] Debugger attached to '{appName}'. Continuing test execution.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"Timed out waiting for a debugger to attach to '{appName}' (container: {info.ContainerName}). " +
            "Open the launch.json snippet printed during BuildAsync in VS Code and press F5 to attach.");
    }

    private static async Task<bool> IsVsDbgRunningAsync(string containerName)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"exec {containerName} sh -c \"pgrep -f vsdbg >/dev/null && echo yes || echo no\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return false;
        var stdout = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 && stdout == "yes";
    }

    /// <summary>
    /// Retrieves the TLS proxy (nginx) container logs for debugging.
    /// </summary>
    public async Task<string> GetProxyLogsAsync()
    {
        if (_tlsProxy is null) return string.Empty;
        return await _tlsProxy.GetLogsAsync();
    }

    /// <summary>
    /// Retrieves the Azurite container logs for debugging.
    /// </summary>
    public async Task<string> GetAzuriteLogsAsync()
    {
        if (_azurite is null) return string.Empty;
        return await _azurite.GetLogsAsync();
    }

    /// <summary>
    /// Gets a dead-letter queue accessor for the specified queue.
    /// Convention: DLQ name is "{queueName}-deadletter".
    /// </summary>
    /// <param name="queueName">Base queue name (without the -deadletter suffix).</param>
    /// <param name="suffix">DLQ suffix (default: "-deadletter").</param>
    public QueueAccessor DeadLetterQueue(string queueName, string suffix = "-deadletter")
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        return new QueueAccessor(_azurite.ConnectionString, queueName + suffix, _cert is not null);
    }

    /// <summary>
    /// Waits until all monitored queues are empty (messages processed).
    /// Polls at regular intervals until timeout. Useful after sending test messages.
    /// </summary>
    /// <param name="queueNames">Queue names to monitor. If null, monitors all known queues.</param>
    /// <param name="timeoutSeconds">Maximum seconds to wait (default: 30).</param>
    /// <param name="pollingIntervalMs">Milliseconds between polls (default: 500).</param>
    /// <exception cref="TimeoutException">Thrown if queues aren't drained within timeout.</exception>
    public async Task WaitForIdleAsync(
        IEnumerable<string>? queueNames = null,
        int timeoutSeconds = 30,
        int pollingIntervalMs = 500)
    {
        if (_azurite is null)
            throw new InvalidOperationException("Azurite was not added to the test environment.");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var queuesToMonitor = queueNames?.ToList();

        // If no specific queues provided, discover all queues in Azurite
        if (queuesToMonitor == null || queuesToMonitor.Count == 0)
        {
            var storage = new StorageAccessor(_azurite.ConnectionString, _cert is not null);
            queuesToMonitor = await storage.ListQueuesAsync();
            // Exclude dead-letter queues from monitoring
            queuesToMonitor = queuesToMonitor
                .Where(q => !q.EndsWith("-deadletter") && !q.EndsWith("-poison"))
                .ToList();
        }

        while (DateTime.UtcNow < deadline)
        {
            var allEmpty = true;
            foreach (var queueName in queuesToMonitor)
            {
                var queue = new QueueAccessor(_azurite.ConnectionString, queueName, _cert is not null);
                var count = await queue.GetMessageCountAsync();
                if (count > 0)
                {
                    allEmpty = false;
                    break;
                }
            }

            if (allEmpty) return;

            await Task.Delay(pollingIntervalMs);
        }

        // Build diagnostic message
        var remaining = new List<string>();
        foreach (var queueName in queuesToMonitor)
        {
            var queue = new QueueAccessor(_azurite.ConnectionString, queueName, _cert is not null);
            var count = await queue.GetMessageCountAsync();
            if (count > 0)
                remaining.Add($"{queueName}={count}");
        }

        throw new TimeoutException(
            $"WaitForIdleAsync timed out after {timeoutSeconds}s. " +
            $"Remaining messages: [{string.Join(", ", remaining)}]");
    }

    /// <summary>
    /// Resets the test environment state between tests. Clears all queues,
    /// resets WireMock request logs, and optionally clears storage.
    /// Call this in [TestInitialize] for test isolation.
    /// </summary>
    /// <param name="clearBlobs">If true, deletes all blobs in known containers.</param>
    /// <param name="clearTables">If true, deletes all entities in known tables.</param>
    public async Task ResetAsync(bool clearBlobs = false, bool clearTables = false)
    {
        // Reset all WireMock request journals
        foreach (var mock in _wireMockContainers.Values)
        {
            await mock.ResetRequestLogAsync();
        }

        // Clear all queues if Azurite is available
        if (_azurite is not null)
        {
            var storage = new StorageAccessor(_azurite.ConnectionString, _cert is not null);
            var queues = await storage.ListQueuesAsync();
            foreach (var queueName in queues)
            {
                var queue = new QueueAccessor(_azurite.ConnectionString, queueName, _cert is not null);
                await queue.ClearAsync();
            }

            if (clearBlobs)
            {
                var containers = await storage.ListContainersAsync();
                foreach (var container in containers)
                {
                    var blob = new BlobAccessor(_azurite.ConnectionString, container, _cert is not null);
                    await blob.ClearAsync();
                }
            }

            if (clearTables)
            {
                var tables = await storage.ListTablesAsync();
                foreach (var tableName in tables)
                {
                    var table = new TableAccessor(_azurite.ConnectionString, tableName, _cert is not null);
                    await table.ClearAsync();
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Dispose app containers first
        foreach (var app in _appContainers.Values)
        {
            await app.DisposeAsync();
        }

        // Dispose TLS proxy
        if (_tlsProxy is not null)
        {
            await _tlsProxy.DisposeAsync();
        }

        // Dispose WireMock containers
        foreach (var mock in _wireMockContainers.Values)
        {
            await mock.DisposeAsync();
        }

        // Dispose Azurite
        if (_azurite is not null)
        {
            await _azurite.DisposeAsync();
        }

        // Dispose certificate material (temp files)
        _cert?.Dispose();

        // Dispose the Docker network
        await _network.DisposeAsync();
    }
}

/// <summary>
/// Provides methods for sending and peeking messages in an Azure Storage Queue via Azurite.
/// </summary>
public class QueueAccessor
{
    private readonly QueueClient _client;
    private readonly string _connectionString;
    private readonly string _queueName;
    private readonly bool _bypassSsl;

    internal QueueAccessor(string connectionString, string queueName, bool bypassSsl = false)
    {
        _connectionString = connectionString;
        _queueName = queueName;
        _bypassSsl = bypassSsl;
        var options = new QueueClientOptions();
        if (bypassSsl) SslHelper.ConfigureSslBypass(options);
        _client = new QueueClient(connectionString, queueName, options);
    }

    /// <summary>Sends a text message to the queue.</summary>
    public async Task SendAsync(string message)
    {
        await _client.SendMessageAsync(message);
    }

    /// <summary>Sends a binary message to the queue.</summary>
    public async Task SendAsync(BinaryData message)
    {
        await _client.SendMessageAsync(message);
    }

    /// <summary>Sends multiple messages to the queue.</summary>
    public async Task SendBatchAsync(IEnumerable<string> messages)
    {
        foreach (var msg in messages)
            await _client.SendMessageAsync(msg);
    }

    /// <summary>
    /// Peeks at messages in the queue without removing them.
    /// </summary>
    /// <param name="maxMessages">Maximum number of messages to peek (up to 32).</param>
    /// <returns>Array of message body strings.</returns>
    public async Task<string[]> PeekMessagesAsync(int maxMessages = 32)
    {
        var messages = await _client.PeekMessagesAsync(maxMessages);
        var result = new List<string>();
        foreach (var msg in messages.Value)
        {
            result.Add(msg.Body.ToString());
        }
        return result.ToArray();
    }

    /// <summary>
    /// Peeks all messages and returns typed objects.
    /// </summary>
    public async Task<List<QueueMessage>> PeekAllAsync(int maxMessages = 32)
    {
        var messages = await _client.PeekMessagesAsync(maxMessages);
        var result = new List<QueueMessage>();
        foreach (var msg in messages.Value)
        {
            result.Add(new QueueMessage
            {
                Id = msg.MessageId,
                Body = msg.Body.ToString(),
                InsertedOn = msg.InsertedOn
            });
        }
        return result;
    }

    /// <summary>
    /// Gets the approximate number of messages in the queue.
    /// Returns 0 if the queue does not exist.
    /// </summary>
    public async Task<int> GetMessageCountAsync()
    {
        try
        {
            var properties = await _client.GetPropertiesAsync();
            return properties.Value.ApproximateMessagesCount;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets a dead-letter queue accessor for this queue.
    /// Convention: DLQ name is "{queueName}-deadletter" or "{queueName}-poison".
    /// </summary>
    /// <param name="suffix">DLQ suffix (default: "-deadletter").</param>
    public QueueAccessor DeadLetter(string suffix = "-deadletter")
    {
        return new QueueAccessor(_connectionString, _queueName + suffix, _bypassSsl);
    }

    /// <summary>
    /// Clears all messages from the queue. Useful in test reset.
    /// </summary>
    public async Task ClearAsync()
    {
        await _client.ClearMessagesAsync();
    }

    /// <summary>
    /// Checks if the queue is empty (0 messages).
    /// </summary>
    public async Task<bool> IsEmptyAsync()
    {
        return await GetMessageCountAsync() == 0;
    }
}

/// <summary>
/// Provides methods for uploading and downloading blobs in an Azure Blob Storage container via Azurite.
/// </summary>
public class BlobAccessor
{
    private readonly BlobContainerClient _client;

    internal BlobAccessor(string connectionString, string containerName, bool bypassSsl = false)
    {
        var options = new BlobClientOptions();
        if (bypassSsl) SslHelper.ConfigureSslBypass(options);
        _client = new BlobContainerClient(connectionString, containerName, options);
    }

    /// <summary>Uploads a string as a blob.</summary>
    public async Task UploadAsync(string blobName, string content)
    {
        var blobClient = _client.GetBlobClient(blobName);
        await blobClient.UploadAsync(BinaryData.FromString(content), overwrite: true);
    }

    /// <summary>Uploads binary data as a blob.</summary>
    public async Task UploadAsync(string blobName, BinaryData content)
    {
        var blobClient = _client.GetBlobClient(blobName);
        await blobClient.UploadAsync(content, overwrite: true);
    }

    /// <summary>Downloads a blob and returns its content as a string.</summary>
    public async Task<string> DownloadAsStringAsync(string blobName)
    {
        var blobClient = _client.GetBlobClient(blobName);
        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    /// <summary>Downloads a blob and deserializes to the specified type.</summary>
    public async Task<T?> DownloadAsAsync<T>(string blobName)
    {
        var content = await DownloadAsStringAsync(blobName);
        return System.Text.Json.JsonSerializer.Deserialize<T>(content);
    }

    /// <summary>
    /// Downloads a blob and returns it as a JSON document for assertion queries.
    /// </summary>
    public async Task<System.Text.Json.JsonDocument> DownloadAsJsonAsync(string blobName)
    {
        var content = await DownloadAsStringAsync(blobName);
        return System.Text.Json.JsonDocument.Parse(content);
    }

    /// <summary>Checks if a blob exists in the container.</summary>
    public async Task<bool> ExistsAsync(string blobName)
    {
        var blobClient = _client.GetBlobClient(blobName);
        var response = await blobClient.ExistsAsync();
        return response.Value;
    }

    /// <summary>
    /// Lists all blobs in the container, optionally filtered by prefix.
    /// </summary>
    /// <param name="prefix">Optional prefix to filter blobs.</param>
    /// <returns>List of blob names.</returns>
    public async Task<List<string>> ListBlobsAsync(string? prefix = null)
    {
        var names = new List<string>();
        await foreach (var item in _client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, System.Threading.CancellationToken.None))
        {
            names.Add(item.Name);
        }
        return names;
    }

    /// <summary>
    /// Gets the number of blobs in the container, optionally filtered by prefix.
    /// </summary>
    public async Task<int> GetBlobCountAsync(string? prefix = null)
    {
        var blobs = await ListBlobsAsync(prefix);
        return blobs.Count;
    }

    /// <summary>
    /// Deletes all blobs in the container. Useful for test reset.
    /// </summary>
    public async Task ClearAsync()
    {
        await foreach (var item in _client.GetBlobsAsync())
        {
            await _client.DeleteBlobIfExistsAsync(item.Name);
        }
    }
}

/// <summary>
/// Provides methods for upserting and retrieving entities in an Azure Table Storage table via Azurite.
/// </summary>
public class TableAccessor
{
    private readonly TableClient _client;

    internal TableAccessor(string connectionString, string tableName, bool bypassSsl = false)
    {
        var options = new TableClientOptions();
        if (bypassSsl) SslHelper.ConfigureSslBypass(options);
        _client = new TableClient(connectionString, tableName, options);
    }

    /// <summary>
    /// Upserts an entity into the table.
    /// </summary>
    public async Task UpsertAsync(string partitionKey, string rowKey, IDictionary<string, object> properties)
    {
        var entity = new TableEntity(partitionKey, rowKey);
        foreach (var (key, value) in properties)
        {
            entity[key] = value;
        }
        await _client.UpsertEntityAsync(entity);
    }

    /// <summary>
    /// Gets an entity by partition key and row key.
    /// Returns null if not found.
    /// </summary>
    public async Task<IDictionary<string, object>?> GetAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _client.GetEntityAsync<TableEntity>(partitionKey, rowKey);
            var entity = response.Value;
            var result = new Dictionary<string, object>();
            foreach (var key in entity.Keys)
            {
                if (entity[key] is not null)
                {
                    result[key] = entity[key]!;
                }
            }
            return result;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Queries entities using an OData filter string.
    /// Example: "PartitionKey eq 'order-123'"
    /// </summary>
    public async Task<List<IDictionary<string, object>>> QueryAsync(string? filter = null)
    {
        var results = new List<IDictionary<string, object>>();
        await foreach (var entity in _client.QueryAsync<TableEntity>(filter))
        {
            var dict = new Dictionary<string, object>();
            foreach (var key in entity.Keys)
            {
                if (entity[key] is not null)
                    dict[key] = entity[key]!;
            }
            results.Add(dict);
        }
        return results;
    }

    /// <summary>
    /// Counts entities in the table, optionally filtered.
    /// </summary>
    public async Task<int> CountAsync(string? filter = null)
    {
        var entities = await QueryAsync(filter);
        return entities.Count;
    }

    /// <summary>
    /// Checks if an entity exists with the given key.
    /// </summary>
    public async Task<bool> ExistsAsync(string partitionKey, string rowKey)
    {
        return await GetAsync(partitionKey, rowKey) != null;
    }

    /// <summary>
    /// Deletes all entities in the table. Useful for test reset.
    /// </summary>
    public async Task ClearAsync()
    {
        await foreach (var entity in _client.QueryAsync<TableEntity>())
        {
            await _client.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
        }
    }
}

/// <summary>
/// Represents a captured HTTP request recorded by WireMock.
/// Includes method, URL, body, and headers for deep assertion.
/// </summary>
public class CapturedRequest
{
    /// <summary>HTTP method (GET, POST, PUT, etc.).</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Full URL path including query string.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Raw request body as a string.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Request headers (name → first value).</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Shortcut for Content-Type header.</summary>
    public string? ContentType => Headers.GetValueOrDefault("Content-Type");

    /// <summary>Shortcut for Authorization header.</summary>
    public string? Authorization => Headers.GetValueOrDefault("Authorization");

    /// <summary>Shortcut for correlation ID header.</summary>
    public string? CorrelationId =>
        Headers.GetValueOrDefault("x-correlation-id") ??
        Headers.GetValueOrDefault("X-Correlation-Id") ??
        Headers.GetValueOrDefault("X-CorrelationId");

    /// <summary>Deserializes the body as the specified type.</summary>
    public T? BodyAs<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(Body);

    /// <summary>Parses the body as a JSON document for assertion.</summary>
    public System.Text.Json.JsonDocument BodyAsJson() => System.Text.Json.JsonDocument.Parse(Body);

    /// <summary>Returns true if the body contains the specified substring.</summary>
    public bool BodyContains(string substring) => Body.Contains(substring, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asserts a JSON path value in the body.
    /// Path format: "$.property.nested" (simple dot-notation, no full JSONPath).
    /// </summary>
    public string? JsonPathValue(string dotPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Body);
        var segments = dotPath.TrimStart('$', '.').Split('.');
        System.Text.Json.JsonElement current = doc.RootElement;

        foreach (var segment in segments)
        {
            if (current.ValueKind == System.Text.Json.JsonValueKind.Object &&
                current.TryGetProperty(segment, out var next))
            {
                current = next;
            }
            else if (current.ValueKind == System.Text.Json.JsonValueKind.Array &&
                     int.TryParse(segment, out var index) && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind == System.Text.Json.JsonValueKind.String
            ? current.GetString()
            : current.GetRawText();
    }
}

/// <summary>
/// Represents a peeked queue message with metadata.
/// </summary>
public class QueueMessage
{
    /// <summary>Message ID.</summary>
    public string Id { get; set; } = "";

    /// <summary>Message body as raw string.</summary>
    public string Body { get; set; } = "";

    /// <summary>When the message was inserted.</summary>
    public DateTimeOffset? InsertedOn { get; set; }

    /// <summary>Deserializes the body as the specified type.</summary>
    public T? BodyAs<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(Body);

    /// <summary>Returns true if the body contains the specified substring.</summary>
    public bool BodyContains(string substring) => Body.Contains(substring, StringComparison.OrdinalIgnoreCase);
}

file static class SslHelper
{
    internal static void ConfigureSslBypass(Azure.Core.ClientOptions options)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        options.Transport = new Azure.Core.Pipeline.HttpClientTransport(handler);
    }
}

/// <summary>
/// Provides assertion and verification methods for a WireMock mock API.
/// Supports Moq-style Times verification patterns.
/// </summary>
public class MockApiAccessor
{
    private readonly ManagedWireMockContainer _container;

    internal MockApiAccessor(ManagedWireMockContainer container)
    {
        _container = container;
    }

    /// <summary>
    /// Verifies that the specified path was called according to the Times constraint.
    /// Usage: await env.MockApi("name").VerifyAsync("/path", Times.Once);
    /// </summary>
    public async Task VerifyAsync(string path, Times times)
    {
        var count = await _container.GetCallCountAsync(path);
        if (!times.Matches(count))
        {
            throw new SltVerificationException(
                $"Mock API verification failed for '{path}': " +
                $"expected {times}, but was called {count} time(s).");
        }
    }

    /// <summary>
    /// Asserts that the specified path was called exactly the expected number of times.
    /// </summary>
    public async Task AssertCalledAsync(string path, int times)
    {
        var count = await _container.GetCallCountAsync(path);
        if (count != times)
        {
            throw new SltVerificationException(
                $"Expected {times} call(s) to '{path}', but was called {count} time(s).");
        }
    }

    /// <summary>
    /// Asserts that the specified path was called at least once.
    /// </summary>
    public async Task AssertCalledAsync(string path)
    {
        var count = await _container.GetCallCountAsync(path);
        if (count == 0)
        {
            throw new SltVerificationException(
                $"Expected at least one call to '{path}', but none were made.");
        }
    }

    /// <summary>
    /// Asserts that the specified path was NEVER called.
    /// </summary>
    public async Task AssertNotCalledAsync(string path)
    {
        var count = await _container.GetCallCountAsync(path);
        if (count > 0)
        {
            throw new SltVerificationException(
                $"Expected no calls to '{path}', but was called {count} time(s).");
        }
    }

    /// <summary>
    /// Gets the number of times a specific path was called.
    /// </summary>
    public async Task<int> GetCallCountAsync(string path)
    {
        return await _container.GetCallCountAsync(path);
    }

    /// <summary>
    /// Gets all captured requests that matched the specified path.
    /// Returns full request details including method, URL, body, and headers.
    /// </summary>
    public async Task<CapturedRequest[]> GetRequestsAsync(string path)
    {
        return await _container.GetRequestsAsync(path);
    }

    /// <summary>
    /// Gets captured requests as a typed list for LINQ assertions.
    /// </summary>
    public async Task<List<CapturedRequest>> GetCallsAsync(string path)
    {
        var requests = await _container.GetRequestsAsync(path);
        return new List<CapturedRequest>(requests);
    }

    /// <summary>
    /// Gets just the request bodies for all requests matching the specified path.
    /// </summary>
    public async Task<string[]> GetRequestBodiesAsync(string path)
    {
        var requests = await _container.GetRequestsAsync(path);
        var bodies = new List<string>();
        foreach (var req in requests)
        {
            bodies.Add(req.Body);
        }
        return bodies.ToArray();
    }

    /// <summary>
    /// Gets request bodies deserialized as the specified type.
    /// </summary>
    public async Task<List<T>> GetRequestBodiesAsAsync<T>(string path)
    {
        var requests = await _container.GetRequestsAsync(path);
        var result = new List<T>();
        foreach (var req in requests)
        {
            var obj = System.Text.Json.JsonSerializer.Deserialize<T>(req.Body);
            if (obj is not null) result.Add(obj);
        }
        return result;
    }

    /// <summary>
    /// Clears the WireMock request journal so subsequent queries only see new requests.
    /// Call this in [TestInitialize] to isolate request assertions between tests.
    /// </summary>
    public async Task ResetRequestLogAsync()
    {
        await _container.ResetRequestLogAsync();
    }

    /// <summary>
    /// Sets up mock responses using a fluent builder pattern.
    /// Usage: await env.MockApi("name").SetupAsync(m => { m.OnPost("/api").Returns(200, data); });
    /// </summary>
    public async Task SetupAsync(Action<Fluent.MockSetupBuilder> configure)
    {
        var builder = new Fluent.MockSetupBuilder(_container);
        configure(builder);
        await builder.ApplyAsync();
    }

    /// <summary>
    /// Verifies that all verifiable mock setups were called at least once.
    /// Requires that setups were configured with .Verifiable().
    /// </summary>
    public async Task VerifyAllAsync(Fluent.MockSetupBuilder? builder = null)
    {
        if (builder == null) return;

        var errors = new List<string>();
        foreach (var rule in builder.Rules)
        {
            if (!rule.IsVerifiable) continue;

            var count = await _container.GetCallCountAsync(rule.Path);
            if (count == 0)
            {
                errors.Add($"{rule.Method} {rule.Path} - never called");
            }
        }

        if (errors.Count > 0)
        {
            throw new SltVerificationException(
                $"VerifyAll failed. The following verifiable setups were never matched:\n" +
                string.Join("\n  ", errors));
        }
    }

    /// <summary>
    /// Resets all mappings and scenarios (for complete mock reconfiguration between tests).
    /// </summary>
    public async Task ResetAllAsync()
    {
        await _container.ResetRequestLogAsync();
        await _container.ResetMappingsAsync();
        await _container.ResetScenariosAsync();
    }
}

/// <summary>
/// Represents a verification constraint for mock API call counts.
/// Mirrors Moq.Times for familiar API.
/// </summary>
public class Times
{
    private readonly Func<int, bool> _predicate;
    private readonly string _description;

    private Times(Func<int, bool> predicate, string description)
    {
        _predicate = predicate;
        _description = description;
    }

    /// <summary>Matches if called exactly once.</summary>
    public static Times Once => new(n => n == 1, "exactly once");

    /// <summary>Matches if never called.</summary>
    public static Times Never => new(n => n == 0, "never");

    /// <summary>Matches if called at least once.</summary>
    public static Times AtLeastOnce => new(n => n >= 1, "at least once");

    /// <summary>Matches if called exactly n times.</summary>
    public static Times Exactly(int count) => new(n => n == count, $"exactly {count} time(s)");

    /// <summary>Matches if called at least n times.</summary>
    public static Times AtLeast(int count) => new(n => n >= count, $"at least {count} time(s)");

    /// <summary>Matches if called at most n times.</summary>
    public static Times AtMost(int count) => new(n => n <= count, $"at most {count} time(s)");

    /// <summary>Matches if called between min and max times (inclusive).</summary>
    public static Times Between(int min, int max) =>
        new(n => n >= min && n <= max, $"between {min} and {max} time(s)");

    /// <summary>Checks if the actual count satisfies the constraint.</summary>
    internal bool Matches(int actual) => _predicate(actual);

    /// <inheritdoc />
    public override string ToString() => _description;
}

/// <summary>
/// Exception thrown when an SLT verification assertion fails.
/// </summary>
public class SltVerificationException : Exception
{
    public SltVerificationException(string message) : base(message) { }
    public SltVerificationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Provides generic access to Azure Storage services in Azurite.
/// Unlike <see cref="BlobAccessor"/>, <see cref="QueueAccessor"/>, and <see cref="TableAccessor"/>
/// which require pre-declared resources, this accessor can access any container/queue/table
/// created by application containers at runtime.
/// </summary>
public class StorageAccessor
{
    private readonly string _connectionString;
    private readonly bool _bypassSsl;

    internal StorageAccessor(string connectionString, bool bypassSsl = false)
    {
        _connectionString = connectionString;
        _bypassSsl = bypassSsl;
    }

    /// <summary>
    /// Gets the raw Azurite connection string (host-side, with mapped ports).
    /// Useful for plugging into Azure Storage Explorer or external CLI tools
    /// during a manual walkthrough/debug session.
    /// </summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// Gets a <see cref="BlobAccessor"/> for the specified container.
    /// The container does not need to be pre-declared via wiring.
    /// </summary>
    public BlobAccessor Blobs(string containerName)
        => new(_connectionString, containerName, _bypassSsl);

    /// <summary>
    /// Gets a <see cref="QueueAccessor"/> for the specified queue.
    /// The queue does not need to be pre-declared via wiring.
    /// </summary>
    public QueueAccessor Queues(string queueName)
        => new(_connectionString, queueName, _bypassSsl);

    /// <summary>
    /// Gets a <see cref="TableAccessor"/> for the specified table.
    /// The table does not need to be pre-declared via wiring.
    /// </summary>
    public TableAccessor Tables(string tableName)
        => new(_connectionString, tableName, _bypassSsl);

    /// <summary>
    /// Lists all blob containers in the storage account.
    /// Useful for discovering containers created by application containers.
    /// </summary>
    public async Task<List<string>> ListContainersAsync()
    {
        var options = new BlobClientOptions();
        if (_bypassSsl) SslHelper.ConfigureSslBypass(options);
        var serviceClient = new BlobServiceClient(_connectionString, options);
        var names = new List<string>();
        await foreach (var container in serviceClient.GetBlobContainersAsync())
        {
            names.Add(container.Name);
        }
        return names;
    }

    /// <summary>
    /// Lists all queues in the storage account.
    /// </summary>
    public async Task<List<string>> ListQueuesAsync()
    {
        var options = new QueueClientOptions();
        if (_bypassSsl) SslHelper.ConfigureSslBypass(options);
        var serviceClient = new QueueServiceClient(_connectionString, options);
        var names = new List<string>();
        await foreach (var queue in serviceClient.GetQueuesAsync())
        {
            names.Add(queue.Name);
        }
        return names;
    }

    /// <summary>
    /// Lists all tables in the storage account.
    /// </summary>
    public async Task<List<string>> ListTablesAsync()
    {
        var options = new TableClientOptions();
        if (_bypassSsl) SslHelper.ConfigureSslBypass(options);
        var serviceClient = new TableServiceClient(_connectionString, options);
        var names = new List<string>();
        await foreach (var table in serviceClient.QueryAsync())
        {
            names.Add(table.Name);
        }
        return names;
    }
}
