using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinPlat.Slt.Cli.Models;

/// <summary>
/// The .slt/slt.json manifest — source of truth for SLT configuration.
/// Auto-detected on init, user-editable afterward.
/// </summary>
public class SltManifest
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/kashyapshivam/FinPlat.TestContainers/main/schemas/slt.schema.json";

    /// <summary>Service project path relative to repo root.</summary>
    public string ServiceProject { get; set; } = "";

    /// <summary>Service name (derived from project name).</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Docker configuration.</summary>
    public DockerConfig Docker { get; set; } = new();

    /// <summary>Detected workers and their queues.</summary>
    public List<WorkerConfig> Workers { get; set; } = [];

    /// <summary>External API dependencies to mock.</summary>
    public List<ApiDependency> ExternalApis { get; set; } = [];

    /// <summary>Test project configuration.</summary>
    public TestConfig Tests { get; set; } = new();

    /// <summary>Infrastructure settings.</summary>
    public InfraConfig Infrastructure { get; set; } = new();

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

    /// <summary>Build context path relative to repo root.</summary>
    public string BuildContext { get; set; } = ".";

    /// <summary>Target project for dotnet publish (relative to repo root).</summary>
    public string? PublishProject { get; set; }

    /// <summary>Extra dotnet publish args.</summary>
    public string? PublishArgs { get; set; }

    /// <summary>Port the service listens on inside container.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Extra environment variables for the container.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

public class WorkerConfig
{
    /// <summary>Worker class name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Queue name this worker reads from.</summary>
    public string QueueName { get; set; } = "";

    /// <summary>Worker type: "queue", "eventhub", "timer".</summary>
    public string Type { get; set; } = "queue";

    /// <summary>Whether to generate a dead-letter queue.</summary>
    public bool DeadLetterQueue { get; set; } = true;
}

public class ApiDependency
{
    /// <summary>Config key name (e.g., "CollectorUri").</summary>
    public string ConfigKey { get; set; } = "";

    /// <summary>Base path for WireMock stub (e.g., "/v1.0/events").</summary>
    public string BasePath { get; set; } = "/";

    /// <summary>Default mock response body.</summary>
    public string DefaultResponse { get; set; } = "{}";
}

public class TestConfig
{
    /// <summary>Test project path (relative to repo root). Null = auto-create.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>Test namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>Test framework: "mstest", "nunit", "xunit".</summary>
    public string Framework { get; set; } = "mstest";
}

public class InfraConfig
{
    /// <summary>Whether Azurite requires OAuth token auth.</summary>
    public bool UseTokenAuth { get; set; } = true;

    /// <summary>Whether to generate nginx HTTPS proxy for Azurite.</summary>
    public bool UseHttpsProxy { get; set; } = true;

    /// <summary>Additional blob containers to pre-create.</summary>
    public List<string> BlobContainers { get; set; } = [];

    /// <summary>Additional table storage tables.</summary>
    public List<string> Tables { get; set; } = [];
}
