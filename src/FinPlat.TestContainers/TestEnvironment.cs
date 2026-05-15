using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;
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
    private readonly CertificateMaterial? _cert;
    private readonly ManagedTlsProxyContainer? _tlsProxy;

    internal TestEnvironment(
        INetwork network,
        ManagedAzuriteContainer? azurite,
        Dictionary<string, ManagedWireMockContainer> wireMockContainers,
        Dictionary<string, ManagedAppContainer> appContainers,
        CertificateMaterial? cert = null,
        ManagedTlsProxyContainer? tlsProxy = null)
    {
        _network = network;
        _azurite = azurite;
        _wireMockContainers = wireMockContainers;
        _appContainers = appContainers;
        _cert = cert;
        _tlsProxy = tlsProxy;
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

        return new StorageAccessor(_azurite.ConnectionString);
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

    internal QueueAccessor(string connectionString, string queueName, bool bypassSsl = false)
    {
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
    /// Gets the approximate number of messages in the queue.
    /// </summary>
    public async Task<int> GetMessageCountAsync()
    {
        var properties = await _client.GetPropertiesAsync();
        return properties.Value.ApproximateMessagesCount;
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

    /// <summary>Checks if a blob exists in the container.</summary>
    public async Task<bool> ExistsAsync(string blobName)
    {
        var blobClient = _client.GetBlobClient(blobName);
        var response = await blobClient.ExistsAsync();
        return response.Value;
    }

    /// <summary>
    /// Lists all blobs in the container, optionally filtered by prefix.
    /// Useful for verifying that an application wrote expected data to storage.
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
    /// <param name="prefix">Optional prefix to filter blobs.</param>
    public async Task<int> GetBlobCountAsync(string? prefix = null)
    {
        var blobs = await ListBlobsAsync(prefix);
        return blobs.Count;
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
    /// <param name="partitionKey">Partition key for the entity.</param>
    /// <param name="rowKey">Row key for the entity.</param>
    /// <param name="properties">Additional properties to store on the entity.</param>
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
    /// </summary>
    /// <param name="partitionKey">Partition key of the entity.</param>
    /// <param name="rowKey">Row key of the entity.</param>
    /// <returns>A dictionary of properties, or null if the entity does not exist.</returns>
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

    /// <summary>Shortcut for correlation ID header (checks common casing variants).</summary>
    public string? CorrelationId =>
        Headers.GetValueOrDefault("x-correlation-id") ??
        Headers.GetValueOrDefault("X-Correlation-Id") ??
        Headers.GetValueOrDefault("X-CorrelationId");

    /// <summary>Parses the body as a JSON document for assertion.</summary>
    public System.Text.Json.JsonDocument BodyAsJson() => System.Text.Json.JsonDocument.Parse(Body);
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
/// </summary>
public class MockApiAccessor
{
    private readonly ManagedWireMockContainer _container;

    internal MockApiAccessor(ManagedWireMockContainer container)
    {
        _container = container;
    }

    /// <summary>
    /// Asserts that the specified path was called exactly the expected number of times.
    /// </summary>
    /// <param name="path">URL path to verify.</param>
    /// <param name="times">Expected call count.</param>
    public async Task AssertCalledAsync(string path, int times)
    {
        var count = await _container.GetCallCountAsync(path);
        if (count != times)
        {
            throw new InvalidOperationException(
                $"Expected {times} call(s) to '{path}', but was called {count} time(s).");
        }
    }

    /// <summary>
    /// Asserts that the specified path was called at least once.
    /// </summary>
    /// <param name="path">URL path to verify.</param>
    public async Task AssertCalledAsync(string path)
    {
        var count = await _container.GetCallCountAsync(path);
        if (count == 0)
        {
            throw new InvalidOperationException(
                $"Expected at least one call to '{path}', but none were made.");
        }
    }

    /// <summary>
    /// Gets the number of times a specific path was called.
    /// </summary>
    /// <param name="path">URL path to check.</param>
    /// <returns>The number of matched requests.</returns>
    public async Task<int> GetCallCountAsync(string path)
    {
        return await _container.GetCallCountAsync(path);
    }

    /// <summary>
    /// Gets all captured requests that matched the specified path.
    /// Returns full request details including method, URL, body, and headers.
    /// </summary>
    /// <param name="path">The URL path to find requests for.</param>
    /// <returns>Array of captured requests.</returns>
    public async Task<CapturedRequest[]> GetRequestsAsync(string path)
    {
        return await _container.GetRequestsAsync(path);
    }

    /// <summary>
    /// Gets just the request bodies for all requests matching the specified path.
    /// Convenience method for quick payload assertions.
    /// </summary>
    /// <param name="path">The URL path to find requests for.</param>
    /// <returns>Array of request body strings.</returns>
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
    /// Clears the WireMock request journal so subsequent queries only see new requests.
    /// Call this in [TestInitialize] to isolate request assertions between tests.
    /// </summary>
    public async Task ResetRequestLogAsync()
    {
        await _container.ResetRequestLogAsync();
    }
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

    internal StorageAccessor(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Gets a <see cref="BlobAccessor"/> for the specified container.
    /// The container does not need to be pre-declared via wiring.
    /// </summary>
    public BlobAccessor Blobs(string containerName)
        => new(_connectionString, containerName);

    /// <summary>
    /// Gets a <see cref="QueueAccessor"/> for the specified queue.
    /// The queue does not need to be pre-declared via wiring.
    /// </summary>
    public QueueAccessor Queues(string queueName)
        => new(_connectionString, queueName);

    /// <summary>
    /// Gets a <see cref="TableAccessor"/> for the specified table.
    /// The table does not need to be pre-declared via wiring.
    /// </summary>
    public TableAccessor Tables(string tableName)
        => new(_connectionString, tableName);

    /// <summary>
    /// Lists all blob containers in the storage account.
    /// Useful for discovering containers created by application containers.
    /// </summary>
    public async Task<List<string>> ListContainersAsync()
    {
        var serviceClient = new BlobServiceClient(_connectionString);
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
        var serviceClient = new QueueServiceClient(_connectionString);
        var names = new List<string>();
        await foreach (var queue in serviceClient.GetQueuesAsync())
        {
            names.Add(queue.Name);
        }
        return names;
    }
}
