using System.Text.Json;
using System.Text.RegularExpressions;
using FinPlat.Slt.Cli.Models;

namespace FinPlat.Slt.Cli.Detection;

/// <summary>
/// Auto-detects service configuration from repo structure.
/// Populates all manifest fields so the generator needs zero domain knowledge.
/// </summary>
public static class ServiceDetector
{
    // Known false-positive URI keys that aren't real external API dependencies
    private static readonly HashSet<string> IgnoredUriKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Uri", "SwaggerUri", "ClusterUri", "BaseUri", "AzureAdInstance",
        "KeyVaultUri" // KeyVault handled separately
    };

    public static async Task<SltManifest> DetectAsync(string repoRoot)
    {
        var manifest = new SltManifest();

        // 1. Find service/worker project
        var workerProject = await FindWorkerProjectAsync(repoRoot);
        if (workerProject != null)
        {
            manifest.ServiceProject = Path.GetRelativePath(repoRoot, workerProject);
            manifest.ServiceName = Path.GetFileNameWithoutExtension(workerProject)
                .Replace(".Worker", "").Replace(".Service", "").Replace(".WorkerService", "");
            manifest.Docker.PublishProject = manifest.ServiceProject;
        }

        // 2. Detect Docker settings from existing Dockerfile/config
        DetectDockerSettings(repoRoot, manifest);

        // 3. Detect workers and APIs from config files
        var (workers, apis) = await DetectWorkersAndApisAsync(repoRoot);
        manifest.Workers = workers;
        manifest.ExternalApis = apis;

        // 4. Detect test project
        var testProject = FindTestProject(repoRoot);
        if (testProject != null)
        {
            manifest.Tests.ProjectPath = Path.GetRelativePath(repoRoot, testProject);
            manifest.Tests.Namespace = DetectNamespace(testProject)
                ?? $"CFS.{manifest.ServiceName}.SltTests";
        }
        else
        {
            manifest.Tests.ProjectPath = $"tests/{manifest.ServiceName}.SltTests/{manifest.ServiceName}.SltTests.csproj";
            manifest.Tests.Namespace = $"CFS.{manifest.ServiceName}.SltTests";
        }

        // 5. Detect infrastructure
        var (blobs, tables) = DetectStorageRequirements(repoRoot);
        manifest.Infrastructure.BlobContainers = blobs;
        manifest.Infrastructure.Tables = tables;
        manifest.Infrastructure.UseTokenAuth = true;
        manifest.Infrastructure.UseHttpsProxy = true;
        manifest.Infrastructure.ProxyAuthEndpoint = true;

        // 6. Detect message envelope format
        DetectMessageEnvelope(repoRoot, manifest);

        return manifest;
    }

    private static void DetectDockerSettings(string repoRoot, SltManifest manifest)
    {
        // Check if there's an existing Dockerfile with clues about base image / entry DLL
        var existingDockerfile = Path.Combine(repoRoot, ".slt", "docker", "Dockerfile.slt");
        if (File.Exists(existingDockerfile))
        {
            var content = File.ReadAllText(existingDockerfile);

            // Detect base image
            var fromMatch = Regex.Match(content, @"FROM\s+(mcr\.microsoft\.com/dotnet/aspnet:\S+)");
            if (fromMatch.Success)
                manifest.Docker.BaseImage = fromMatch.Groups[1].Value;

            // Detect entry DLL
            var entryMatch = Regex.Match(content, @"ENTRYPOINT\s+\[""dotnet"",\s*""(.+\.dll)""\]");
            if (entryMatch.Success)
                manifest.Docker.EntryDll = entryMatch.Groups[1].Value;

            // Detect config target path
            var configMatch = Regex.Match(content, @"COPY\s+docker/config\.docker\.json\s+(.+)");
            if (configMatch.Success)
                manifest.Docker.ConfigTargetPath = configMatch.Groups[1].Value.Trim();

            // Detect cert install pattern (Azure Linux vs Debian)
            if (content.Contains("/etc/pki/ca-trust"))
            {
                manifest.Docker.CertInstall = new CertInstallConfig
                {
                    CertDestination = "/etc/pki/ca-trust/source/anchors/azurite.crt",
                    UpdateCommand = "update-ca-trust"
                };
            }

            manifest.Docker.Dockerfile = "docker/Dockerfile.slt"; // Use existing
        }
        else
        {
            // Default: derive entry DLL from project name
            var projectName = Path.GetFileNameWithoutExtension(manifest.ServiceProject);
            manifest.Docker.EntryDll = $"{projectName}.dll";
        }

        // Detect workload identity usage
        var configFiles = Directory.GetFiles(repoRoot, "config*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules"));
        foreach (var cf in configFiles)
        {
            try
            {
                var json = File.ReadAllText(cf);
                if (json.Contains("UseWorkloadIdentity"))
                {
                    manifest.Docker.UseWorkloadIdentity = true;
                    break;
                }
            }
            catch { }
        }
    }

    private static async Task<string?> FindWorkerProjectAsync(string repoRoot)
    {
        var projects = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("node_modules") && !p.Contains("bin") && !p.Contains("obj"))
            .ToList();

        // Priority: Worker > Service > src/
        return projects.FirstOrDefault(p => Path.GetFileName(p).Contains("Worker", StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => Path.GetFileName(p).Contains("Service", StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.Contains(Path.Combine("src", ""), StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault();
    }

    private static async Task<(List<WorkerConfig>, List<ApiDependency>)> DetectWorkersAndApisAsync(string repoRoot)
    {
        var workers = new List<WorkerConfig>();
        var apis = new List<ApiDependency>();

        var configFiles = Directory.GetFiles(repoRoot, "*.json", SearchOption.AllDirectories)
            .Where(f => (f.Contains("appsettings", StringComparison.OrdinalIgnoreCase)
                      || f.Contains("config", StringComparison.OrdinalIgnoreCase))
                      && !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules")
                      && !f.Contains("package.json") && !f.Contains("tsconfig"))
            .ToList();

        foreach (var configFile in configFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(configFile);
                var doc = JsonDocument.Parse(json);

                // Detect queue workers
                if (doc.RootElement.TryGetProperty("AzureQueueWorkerConfiguration", out var queueConfig))
                {
                    foreach (var item in queueConfig.EnumerateArray())
                    {
                        var name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(name) && !workers.Any(w => w.Name == name))
                        {
                            workers.Add(new WorkerConfig
                            {
                                Name = name,
                                QueueName = DeriveQueueName(name),
                                Type = "queue"
                            });
                        }
                    }
                }

                // Detect API dependencies (URI keys)
                DetectApiDependencies(doc.RootElement, apis);
            }
            catch { }
        }

        return (workers, apis);
    }

    private static void DetectApiDependencies(JsonElement element, List<ApiDependency> apis)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String &&
                prop.Name.EndsWith("Uri", StringComparison.OrdinalIgnoreCase) &&
                !IgnoredUriKeys.Contains(prop.Name))
            {
                if (!apis.Any(a => a.ConfigKey == prop.Name))
                {
                    var basePath = "/" + prop.Name.Replace("Uri", "").ToLowerInvariant();
                    apis.Add(new ApiDependency
                    {
                        ConfigKey = prop.Name,
                        BasePath = basePath,
                        Method = "ANY",
                        DefaultResponse = "[]",
                        DefaultStatusCode = 200
                    });
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                DetectApiDependencies(prop.Value, apis);
            }
        }
    }

    private static string? FindTestProject(string repoRoot)
    {
        return Directory.GetFiles(repoRoot, "*SltTest*.csproj", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.GetFiles(repoRoot, "*IntegrationTest*.csproj", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? DetectNamespace(string csprojPath)
    {
        try
        {
            var content = File.ReadAllText(csprojPath);
            var match = Regex.Match(content, @"<RootNamespace>(.+?)</RootNamespace>");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static (List<string>, List<string>) DetectStorageRequirements(string repoRoot)
    {
        var blobs = new List<string>();
        var tables = new List<string>();

        var configFiles = Directory.GetFiles(repoRoot, "*.json", SearchOption.AllDirectories)
            .Where(f => f.Contains("config", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains("bin") && !f.Contains("obj"));

        foreach (var configFile in configFiles)
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(configFile));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    // Tables
                    if (prop.Name.Contains("TableIndex") && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.TryGetProperty("TableName", out var tn))
                            {
                                var tableName = tn.GetString();
                                if (tableName != null && !tables.Contains(tableName))
                                    tables.Add(tableName);
                            }
                        }
                    }

                    // Blobs — detect from container references in code/config
                    if (prop.Name.Contains("Container", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var blobName = prop.Value.GetString();
                        if (blobName != null && !blobs.Contains(blobName))
                            blobs.Add(blobName);
                    }
                }
            }
            catch { }
        }

        return (blobs, tables);
    }

    private static void DetectMessageEnvelope(string repoRoot, SltManifest manifest)
    {
        // Look for EventEntity patterns in source code
        var csFiles = Directory.GetFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj"))
            .Take(500); // limit scan

        foreach (var file in csFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (content.Contains("EventEntityV1") || content.Contains("EventEntity"))
                {
                    manifest.MessageEnvelope.Format = "base64-json";
                    manifest.MessageEnvelope.EventType = "OrderCreation";
                    return;
                }
            }
            catch { }
        }

        // Default: raw JSON (simplest assumption)
        manifest.MessageEnvelope.Format = "raw-json";
    }

    private static string DeriveQueueName(string workerName)
    {
        return workerName
            .Replace("QueueWorkerV1", "", StringComparison.OrdinalIgnoreCase)
            .Replace("QueueWorkerV2", "", StringComparison.OrdinalIgnoreCase)
            .Replace("QueueWorkerV8", "", StringComparison.OrdinalIgnoreCase)
            .Replace("QueueWorker", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Worker", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
    }
}
