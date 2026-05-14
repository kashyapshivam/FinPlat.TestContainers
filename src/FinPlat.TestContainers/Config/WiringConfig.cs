using System.Collections.Generic;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Holds the wiring configuration for an application container, describing which
/// Azure Storage resources and mock APIs it depends on.
/// </summary>
public class WiringConfig
{
    /// <summary>
    /// Queue names to pre-create in Azurite and expose to the application.
    /// </summary>
    public List<string> Queues { get; } = new();

    /// <summary>
    /// Blob container names to pre-create in Azurite and expose to the application.
    /// </summary>
    public List<string> BlobContainers { get; } = new();

    /// <summary>
    /// Table names to pre-create in Azurite and expose to the application.
    /// </summary>
    public List<string> Tables { get; } = new();

    /// <summary>
    /// Maps mock API names to the environment variable key under which their
    /// internal URL should be injected (e.g., "collector" → "CollectorUri").
    /// </summary>
    public Dictionary<string, string> MockApiBindings { get; } = new();

    /// <summary>
    /// When true, the application uses token-based authentication (Azure Identity)
    /// instead of connection strings for storage access.
    /// </summary>
    public bool UseTokenAuth { get; set; }
}
