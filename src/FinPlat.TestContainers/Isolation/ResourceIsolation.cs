using System;
using System.Text.RegularExpressions;

namespace FinPlat.TestContainers.Isolation;

/// <summary>
/// Generates unique instance IDs and isolated resource names for parallel test execution.
/// Each test environment gets a unique prefix to avoid Docker network, container,
/// and Azure Storage resource name collisions when running tests in parallel.
/// </summary>
public static class ResourceIsolation
{
    /// <summary>
    /// Generates a short unique instance ID (8 hex characters).
    /// </summary>
    public static string GenerateInstanceId()
        => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Creates an isolated resource name by prefixing with "slt-{instanceId}-".
    /// </summary>
    public static string Prefix(string instanceId, string name)
        => $"slt-{instanceId}-{name}";

    /// <summary>
    /// Docker labels applied to all SLT-managed containers and networks.
    /// Used by <see cref="CleanupEngine"/> to identify orphaned resources.
    /// </summary>
    public static class Labels
    {
        public const string Managed = "finplat.slt";
        public const string InstanceId = "finplat.slt.instance-id";
        public const string CreatedAt = "finplat.slt.created-at";
        public const string Role = "finplat.slt.role";

        /// <summary>
        /// Returns the standard label set for an SLT-managed resource.
        /// </summary>
        public static (string Key, string Value)[] ForResource(string instanceId, string role)
        {
            return new[]
            {
                (Managed, "true"),
                (InstanceId, instanceId),
                (CreatedAt, DateTimeOffset.UtcNow.ToString("o")),
                (Role, role),
            };
        }
    }

    /// <summary>
    /// Sanitizes a name to be valid for the target resource type.
    /// </summary>
    public static string SanitizeName(string name, ResourceType type)
    {
        return type switch
        {
            ResourceType.DockerContainer => SanitizeDockerName(name),
            ResourceType.DockerNetwork => SanitizeDockerName(name),
            ResourceType.AzureQueue => SanitizeAzureQueueName(name),
            ResourceType.BlobContainer => SanitizeBlobContainerName(name),
            ResourceType.AzureTable => SanitizeAzureTableName(name),
            _ => name.ToLowerInvariant(),
        };
    }

    // Docker: [a-zA-Z0-9][a-zA-Z0-9_.-], max 128
    private static string SanitizeDockerName(string name)
    {
        var sanitized = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_.-]", "-");
        if (sanitized.Length > 0 && !char.IsLetterOrDigit(sanitized[0]))
            sanitized = "s" + sanitized;
        return sanitized.Length > 128 ? sanitized[..128] : sanitized;
    }

    // Azure Queue: lowercase, alphanumeric + hyphens, 3-63 chars, no consecutive hyphens
    private static string SanitizeAzureQueueName(string name)
    {
        var sanitized = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]", "-");
        sanitized = Regex.Replace(sanitized, @"-{2,}", "-");
        sanitized = sanitized.Trim('-');
        if (sanitized.Length < 3) sanitized = sanitized.PadRight(3, 'x');
        return sanitized.Length > 63 ? sanitized[..63] : sanitized;
    }

    // Blob container: lowercase, alphanumeric + hyphens, 3-63 chars
    private static string SanitizeBlobContainerName(string name)
        => SanitizeAzureQueueName(name); // same rules

    // Azure Table: alphanumeric only, 3-63 chars, must start with letter
    private static string SanitizeAzureTableName(string name)
    {
        var sanitized = Regex.Replace(name, @"[^a-zA-Z0-9]", "");
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            sanitized = "t" + sanitized;
        if (sanitized.Length < 3) sanitized = sanitized.PadRight(3, 'x');
        return sanitized.Length > 63 ? sanitized[..63] : sanitized;
    }
}

/// <summary>
/// Target resource types for name sanitization.
/// </summary>
public enum ResourceType
{
    DockerContainer,
    DockerNetwork,
    AzureQueue,
    BlobContainer,
    AzureTable,
}
