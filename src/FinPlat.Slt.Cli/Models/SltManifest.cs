using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinPlat.Slt.Cli.Models;

/// <summary>
/// The .slt/slt.json manifest — source of truth for SLT configuration.
/// Auto-detected on init, user-editable afterward.
/// The generator reads ONLY this manifest — zero service-specific knowledge baked in.
/// </summary>
public class SltManifest
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/kashyapshivam/FinPlat.TestContainers/main/schemas/slt.schema.json";

    /// <summary>Service name (used for Docker image tag, log labels).</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Service project path relative to repo root (for publish).</summary>
    public string ServiceProject { get; set; } = "";

    /// <summary>Docker configuration.</summary>
    public DockerConfig Docker { get; set; } = new();

    /// <summary>Detected workers and their queues.</summary>
    public List<WorkerConfig> Workers { get; set; } = [];

    /// <summary>External API dependencies to mock.</summary>
    public List<ApiDependency> ExternalApis { get; set; } = [];

    /// <summary>Test project configuration.</summary>
    public TestConfig Tests { get; set; } = new();

    /// <summary>Infrastructure settings (Azurite, nginx, storage).</summary>
    public InfraConfig Infrastructure { get; set; } = new();

    /// <summary>Message envelope format configuration.</summary>
    public MessageEnvelopeConfig MessageEnvelope { get; set; } = new();

    public static SltManifest LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SltManifest>(json, SerializerOptions) ?? new();
    }

    public void SaveToFile(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public class DockerConfig
{
    /// <summary>Path to Dockerfile (relative to .slt/). Null = auto-generate.</summary>
    public string? Dockerfile { get; set; }

    /// <summary>Base Docker image (e.g., "mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0").</summary>
    public string BaseImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:8.0";

    /// <summary>Entry point DLL name (e.g., "MyService.dll"). Null = derived from project name.</summary>
    public string? EntryDll { get; set; }

    /// <summary>Target path for config file inside container (e.g., "Configuration/config.local.json").</summary>
    public string ConfigTargetPath { get; set; } = "config.docker.json";

    /// <summary>CA cert install command (varies by base image OS).</summary>
    public CertInstallConfig CertInstall { get; set; } = new();

    /// <summary>Build context path relative to .slt/ (default: entire .slt/ dir).</summary>
    public string BuildContext { get; set; } = ".";

    /// <summary>Target project for dotnet publish (relative to repo root).</summary>
    public string? PublishProject { get; set; }

    /// <summary>Port the service listens on inside container.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Static environment variables for the container.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>Whether the service uses workload identity (adds AZURE_* env vars).</summary>
    public bool UseWorkloadIdentity { get; set; } = true;
}

public class CertInstallConfig
{
    /// <summary>Path to copy the cert to inside the container.</summary>
    public string CertDestination { get; set; } = "/usr/local/share/ca-certificates/azurite-ca.crt";

    /// <summary>Command to run after copying cert (e.g., "update-ca-certificates" or "update-ca-trust").</summary>
    public string UpdateCommand { get; set; } = "update-ca-certificates";
}

public class WorkerConfig
{
    /// <summary>Worker class name (as registered in DI/config).</summary>
    public string Name { get; set; } = "";

    /// <summary>Queue name this worker reads from.</summary>
    public string QueueName { get; set; } = "";

    /// <summary>Worker type: "queue", "eventhub", "timer".</summary>
    public string Type { get; set; } = "queue";

    /// <summary>Whether to create a dead-letter queue.</summary>
    public bool DeadLetterQueue { get; set; } = true;
}

public class ApiDependency
{
    /// <summary>Config key name (e.g., "CollectorUri", "CatalogUri").</summary>
    public string ConfigKey { get; set; } = "";

    /// <summary>Base path for WireMock stub (e.g., "/v1.0/events").</summary>
    public string BasePath { get; set; } = "/";

    /// <summary>HTTP method for the primary mock (GET, POST, ANY).</summary>
    public string Method { get; set; } = "ANY";

    /// <summary>Default mock response body.</summary>
    public string DefaultResponse { get; set; } = "{}";

    /// <summary>Default mock HTTP status code.</summary>
    public int DefaultStatusCode { get; set; } = 200;

    /// <summary>Additional routes for this API (e.g., ingestion endpoint).</summary>
    public List<ApiRoute>? AdditionalRoutes { get; set; }
}

public class ApiRoute
{
    public string Path { get; set; } = "/";
    public string Method { get; set; } = "ANY";
    public string Response { get; set; } = "{}";
    public int StatusCode { get; set; } = 200;
}

public class TestConfig
{
    /// <summary>Test project path (relative to repo root). Null = auto-create.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>Test namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>Test framework: "mstest", "nunit", "xunit".</summary>
    public string Framework { get; set; } = "mstest";

    /// <summary>Processing timeout in seconds (how long to wait for worker to process).</summary>
    public int ProcessingTimeoutSeconds { get; set; } = 30;

    /// <summary>Polling interval in seconds for assertion retries.</summary>
    public int PollingIntervalSeconds { get; set; } = 2;
}

public class InfraConfig
{
    /// <summary>Whether Azurite requires OAuth token auth.</summary>
    public bool UseTokenAuth { get; set; } = true;

    /// <summary>Whether to generate nginx HTTPS proxy for Azurite.</summary>
    public bool UseHttpsProxy { get; set; } = true;

    /// <summary>Whether to proxy auth endpoints (login.microsoftonline.com) through nginx.</summary>
    public bool ProxyAuthEndpoint { get; set; } = true;

    /// <summary>Blob containers to pre-create in Azurite.</summary>
    public List<string> BlobContainers { get; set; } = [];

    /// <summary>Table storage tables to pre-create.</summary>
    public List<string> Tables { get; set; } = [];

    /// <summary>Additional config sections to include in docker config (raw JSON merge).</summary>
    public Dictionary<string, object>? ExtraConfig { get; set; }
}

public class MessageEnvelopeConfig
{
    /// <summary>
    /// Envelope format: "base64-json" (wrap in JSON envelope, base64 encode),
    /// "raw-json" (send JSON directly), "base64-raw" (base64 encode raw content).
    /// </summary>
    public string Format { get; set; } = "base64-json";

    /// <summary>
    /// Envelope template (JSON). Use {event} placeholder for the inner message.
    /// Only used when format = "base64-json".
    /// Example: { "Event": {event}, "EventType": "OrderCreation", "Properties": {...} }
    /// </summary>
    public string? EnvelopeTemplate { get; set; }

    /// <summary>Default EventType value in envelope.</summary>
    public string EventType { get; set; } = "Event";
}
