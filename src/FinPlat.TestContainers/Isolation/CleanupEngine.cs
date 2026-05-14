using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FinPlat.TestContainers.Isolation;

/// <summary>
/// Discovers and removes orphaned Docker resources from failed SLT test runs.
/// Identifies resources by Docker labels (not just name prefix) to avoid
/// accidentally removing resources from active test runs.
/// </summary>
public class CleanupEngine
{
    /// <summary>
    /// Removes orphaned SLT containers and networks older than the specified age.
    /// Uses Docker labels (<see cref="ResourceIsolation.Labels"/>) to identify SLT resources.
    /// </summary>
    /// <param name="maxAge">Only remove resources older than this duration.</param>
    /// <returns>Summary of removed resources.</returns>
    public static async Task<CleanupResult> CleanupOrphanedAsync(TimeSpan maxAge)
    {
        var result = new CleanupResult();
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        // Find and remove orphaned containers
        var containers = await ListLabeledResourcesAsync("container", ResourceIsolation.Labels.Managed);
        foreach (var container in containers)
        {
            if (TryParseCreatedAt(container, out var createdAt) && createdAt < cutoff)
            {
                var id = container.GetProperty("ID").GetString()!;
                await RunDockerAsync($"rm -f {id}");
                result.ContainersRemoved++;
                result.RemovedResources.Add($"container:{id}");
            }
        }

        // Find and remove orphaned networks
        var networks = await ListLabeledResourcesAsync("network", ResourceIsolation.Labels.Managed);
        foreach (var network in networks)
        {
            if (TryParseCreatedAt(network, out var createdAt) && createdAt < cutoff)
            {
                var id = network.GetProperty("ID").GetString()!;
                await RunDockerAsync($"network rm {id}");
                result.NetworksRemoved++;
                result.RemovedResources.Add($"network:{id}");
            }
        }

        return result;
    }

    /// <summary>
    /// Lists all SLT-managed Docker resources (containers and networks) with their labels.
    /// Does not remove anything — useful for diagnostics.
    /// </summary>
    public static async Task<List<ManagedResource>> ListManagedResourcesAsync()
    {
        var resources = new List<ManagedResource>();

        var containers = await ListLabeledResourcesAsync("container", ResourceIsolation.Labels.Managed);
        foreach (var c in containers)
        {
            resources.Add(new ManagedResource
            {
                Type = "container",
                Id = c.GetProperty("ID").GetString() ?? "",
                Name = c.TryGetProperty("Names", out var names) ? names.GetString() ?? "" : "",
                InstanceId = GetLabel(c, ResourceIsolation.Labels.InstanceId),
                Role = GetLabel(c, ResourceIsolation.Labels.Role),
                CreatedAt = GetLabel(c, ResourceIsolation.Labels.CreatedAt),
            });
        }

        var networks = await ListLabeledResourcesAsync("network", ResourceIsolation.Labels.Managed);
        foreach (var n in networks)
        {
            resources.Add(new ManagedResource
            {
                Type = "network",
                Id = n.GetProperty("ID").GetString() ?? "",
                Name = n.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                InstanceId = GetLabel(n, ResourceIsolation.Labels.InstanceId),
                Role = GetLabel(n, ResourceIsolation.Labels.Role),
                CreatedAt = GetLabel(n, ResourceIsolation.Labels.CreatedAt),
            });
        }

        return resources;
    }

    private static async Task<List<JsonElement>> ListLabeledResourcesAsync(string resourceType, string labelKey)
    {
        var args = resourceType switch
        {
            "container" => $"ps -a --filter label={labelKey}=true --format \"{{{{json .}}}}\"",
            "network" => $"network ls --filter label={labelKey}=true --format \"{{{{json .}}}}\"",
            _ => throw new ArgumentException($"Unknown resource type: {resourceType}")
        };

        var output = await RunDockerAsync(args);
        var results = new List<JsonElement>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                results.Add(JsonDocument.Parse(line.Trim()).RootElement.Clone());
            }
            catch (JsonException)
            {
                // Skip lines that aren't valid JSON
            }
        }
        return results;
    }

    private static bool TryParseCreatedAt(JsonElement element, out DateTimeOffset createdAt)
    {
        createdAt = default;
        var label = GetLabel(element, ResourceIsolation.Labels.CreatedAt);
        return label is not null && DateTimeOffset.TryParse(label, out createdAt);
    }

    private static string? GetLabel(JsonElement element, string labelKey)
    {
        if (element.TryGetProperty("Labels", out var labels))
        {
            var labelsStr = labels.GetString() ?? "";
            // Docker format outputs labels as "key=value,key=value"
            foreach (var pair in labelsStr.Split(','))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim() == labelKey)
                    return parts[1].Trim();
            }
        }
        return null;
    }

    private static async Task<string> RunDockerAsync(string args)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}

/// <summary>Result of a cleanup operation.</summary>
public class CleanupResult
{
    public int ContainersRemoved { get; set; }
    public int NetworksRemoved { get; set; }
    public List<string> RemovedResources { get; } = new();
    public int TotalRemoved => ContainersRemoved + NetworksRemoved;
}

/// <summary>A Docker resource managed by the SLT framework.</summary>
public class ManagedResource
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InstanceId { get; set; }
    public string? Role { get; set; }
    public string? CreatedAt { get; set; }
}
