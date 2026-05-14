using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FinPlat.TestContainers.Config;
using AzuriteContainerType = global::Testcontainers.Azurite.AzuriteContainer;
using AzuriteBuilderType = global::Testcontainers.Azurite.AzuriteBuilder;

namespace FinPlat.TestContainers.Containers;

/// <summary>
/// Manages an Azurite container providing Azure Storage emulation (Blob, Queue, Table).
/// Supports two modes:
/// - Simple mode: plain HTTP, connection string access
/// - Token auth mode: HTTPS with OAuth, bearer token access
/// </summary>
public class ManagedAzuriteContainer : IAsyncDisposable
{
    private AzuriteContainerType? _simpleContainer;
    private IContainer? _httpsContainer;
    private const string NetworkAlias = "azurite";
    private const int BlobPort = 10000;
    private const int QueuePort = 10001;
    private const int TablePort = 10002;
    private const string AccountName = "devstoreaccount1";
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly AzuriteOptions? _options;

    /// <summary>
    /// Creates Azurite in simple mode (HTTP, connection string).
    /// </summary>
    public ManagedAzuriteContainer() : this(null) { }

    /// <summary>
    /// Creates Azurite with the specified options. When <see cref="AzuriteOptions.UseTokenAuth"/>
    /// is true, Azurite starts with HTTPS and OAuth support.
    /// </summary>
    public ManagedAzuriteContainer(AzuriteOptions? options)
    {
        _options = options;
    }

    /// <summary>
    /// Whether this instance is running in token auth (HTTPS) mode.
    /// </summary>
    public bool IsTokenAuthMode => _options?.UseTokenAuth == true;

    /// <summary>
    /// Gets the connection string for accessing Azurite from the test host (external access via mapped ports).
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the connection string for app containers on the same Docker network (uses container hostname).
    /// </summary>
    public string InternalConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Starts the Azurite container and attaches it to the specified Docker network.
    /// In token auth mode, also requires certificate material for HTTPS.
    /// </summary>
    /// <param name="network">The shared Docker network for container-to-container communication.</param>
    /// <param name="cert">Certificate material for HTTPS mode (required when UseTokenAuth is true).</param>
    public async Task StartAsync(INetwork network, CertificateMaterial? cert = null)
    {
        if (IsTokenAuthMode)
        {
            if (cert is null)
                throw new InvalidOperationException("Certificate material is required for token auth mode.");

            await StartHttpsAsync(network, cert);
        }
        else
        {
            await StartSimpleAsync(network);
        }
    }

    /// <summary>
    /// Pre-creates a queue in Azurite.
    /// </summary>
    public async Task CreateQueueAsync(string queueName)
    {
        var client = new QueueClient(ConnectionString, queueName);
        await client.CreateIfNotExistsAsync();
    }

    /// <summary>
    /// Pre-creates a blob container in Azurite.
    /// </summary>
    public async Task CreateBlobContainerAsync(string containerName)
    {
        var client = new BlobContainerClient(ConnectionString, containerName);
        await client.CreateIfNotExistsAsync();
    }

    /// <summary>
    /// Pre-creates a table in Azurite.
    /// </summary>
    public async Task CreateTableAsync(string tableName)
    {
        var serviceClient = new TableServiceClient(ConnectionString);
        await serviceClient.CreateTableIfNotExistsAsync(tableName);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_simpleContainer is not null)
        {
            await _simpleContainer.DisposeAsync();
            _simpleContainer = null;
        }

        if (_httpsContainer is not null)
        {
            await _httpsContainer.DisposeAsync();
            _httpsContainer = null;
        }
    }

    private async Task StartSimpleAsync(INetwork network)
    {
        _simpleContainer = new AzuriteBuilderType()
            .WithNetwork(network)
            .WithNetworkAliases(NetworkAlias)
            .Build();

        await _simpleContainer.StartAsync();

        ConnectionString = _simpleContainer.GetConnectionString();
        InternalConnectionString = BuildInternalConnectionString(useHttps: false);
    }

    private async Task StartHttpsAsync(INetwork network, CertificateMaterial cert)
    {
        var (certPath, keyPath) = cert.WriteTempFiles();

        var command = new List<string>
        {
            "azurite",
            "--blobHost", "0.0.0.0",
            "--queueHost", "0.0.0.0",
            "--tableHost", "0.0.0.0",
            "--cert", "/certs/cert.pem",
            "--key", "/certs/key.pem",
            "--oauth", "basic",
            "--loose"
        };

        if (_options!.SkipApiVersionCheck)
        {
            command.Add("--skipApiVersionCheck");
        }

        _httpsContainer = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite")
            .WithCommand(command.ToArray())
            .WithNetwork(network)
            .WithNetworkAliases(NetworkAlias)
            .WithPortBinding(BlobPort, true)
            .WithPortBinding(QueuePort, true)
            .WithPortBinding(TablePort, true)
            .WithResourceMapping(certPath, "/certs/cert.pem")
            .WithResourceMapping(keyPath, "/certs/key.pem")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(QueuePort))
            .Build();

        await _httpsContainer.StartAsync();

        // Build connection string using mapped ports (for test host access)
        var host = _httpsContainer.Hostname;
        var blobMapped = _httpsContainer.GetMappedPublicPort(BlobPort);
        var queueMapped = _httpsContainer.GetMappedPublicPort(QueuePort);
        var tableMapped = _httpsContainer.GetMappedPublicPort(TablePort);

        ConnectionString = $"DefaultEndpointsProtocol=https;AccountName={AccountName};AccountKey={AccountKey};" +
                          $"BlobEndpoint=https://{host}:{blobMapped}/{AccountName};" +
                          $"QueueEndpoint=https://{host}:{queueMapped}/{AccountName};" +
                          $"TableEndpoint=https://{host}:{tableMapped}/{AccountName};";

        InternalConnectionString = BuildInternalConnectionString(useHttps: true);
    }

    private static string BuildInternalConnectionString(bool useHttps)
    {
        var protocol = useHttps ? "https" : "http";
        var blobEndpoint = $"{protocol}://{NetworkAlias}:{BlobPort}/{AccountName}";
        var queueEndpoint = $"{protocol}://{NetworkAlias}:{QueuePort}/{AccountName}";
        var tableEndpoint = $"{protocol}://{NetworkAlias}:{TablePort}/{AccountName}";

        return $"DefaultEndpointsProtocol={protocol};AccountName={AccountName};AccountKey={AccountKey};" +
               $"BlobEndpoint={blobEndpoint};QueueEndpoint={queueEndpoint};TableEndpoint={tableEndpoint};";
    }
}
