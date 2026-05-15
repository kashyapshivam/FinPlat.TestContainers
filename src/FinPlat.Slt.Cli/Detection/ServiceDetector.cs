using System.Text.Json;
using System.Text.RegularExpressions;
using FinPlat.Slt.Cli.Models;

namespace FinPlat.Slt.Cli.Detection;

/// <summary>
/// Auto-detects service configuration from repo structure.
/// Scans for projects, workers, queues, API dependencies, and config files.
/// </summary>
public static class ServiceDetector
{
    /// <summary>
    /// Detects service configuration from the given repo root.
    /// Returns a populated SltManifest with best-guess values.
    /// </summary>
    public static async Task<SltManifest> DetectAsync(string repoRoot)
    {
        var manifest = new SltManifest();

        // 1. Find solution file
        var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length == 0)
            slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.AllDirectories);

        // 2. Find Worker/Service projects
        var workerProject = await FindWorkerProjectAsync(repoRoot);
        if (workerProject != null)
        {
            manifest.ServiceProject = Path.GetRelativePath(repoRoot, workerProject);
            manifest.ServiceName = Path.GetFileNameWithoutExtension(workerProject)
                .Replace(".Worker", "").Replace(".Service", "");
            manifest.Docker.PublishProject = manifest.ServiceProject;
        }

        // 3. Detect workers and queues from config
        var (workers, apis) = await DetectWorkersAndApisAsync(repoRoot, workerProject);
        manifest.Workers = workers;
        manifest.ExternalApis = apis;

        // 4. Detect test project
        var testProject = FindTestProject(repoRoot);
        if (testProject != null)
        {
            manifest.Tests.ProjectPath = Path.GetRelativePath(repoRoot, testProject);
            manifest.Tests.Namespace = Path.GetFileNameWithoutExtension(testProject)
                .Replace(".csproj", "");
        }
        else
        {
            var servicePascal = ToPascalCase(manifest.ServiceName);
            manifest.Tests.ProjectPath = $"tests/{servicePascal}.SltTests/{servicePascal}.SltTests.csproj";
            manifest.Tests.Namespace = $"{servicePascal}.SltTests";
        }

        // 5. Detect infrastructure needs
        manifest.Infrastructure.UseTokenAuth = true; // default for FinPlat services
        manifest.Infrastructure.UseHttpsProxy = true;

        // Detect blob containers and tables from config
        var (blobs, tables) = DetectStorageRequirements(repoRoot);
        manifest.Infrastructure.BlobContainers = blobs;
        manifest.Infrastructure.Tables = tables;

        return manifest;
    }

    private static async Task<string?> FindWorkerProjectAsync(string repoRoot)
    {
        // Look for Worker projects by name pattern
        var projects = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("node_modules"))
            .Where(p => !p.Contains(Path.Combine("bin", "")))
            .Where(p => !p.Contains(Path.Combine("obj", "")))
            .ToList();

        // Priority 1: Project with "Worker" in name
        var workerProj = projects.FirstOrDefault(p =>
            Path.GetFileName(p).Contains("Worker", StringComparison.OrdinalIgnoreCase));
        if (workerProj != null) return workerProj;

        // Priority 2: Project with "Service" in name
        var serviceProj = projects.FirstOrDefault(p =>
            Path.GetFileName(p).Contains("Service", StringComparison.OrdinalIgnoreCase));
        if (serviceProj != null) return serviceProj;

        // Priority 3: Project in src/ directory
        var srcProj = projects.FirstOrDefault(p =>
            p.Contains(Path.Combine("src", ""), StringComparison.OrdinalIgnoreCase));
        return srcProj ?? projects.FirstOrDefault();
    }

    private static async Task<(List<WorkerConfig>, List<ApiDependency>)> DetectWorkersAndApisAsync(
        string repoRoot, string? workerProject)
    {
        var workers = new List<WorkerConfig>();
        var apis = new List<ApiDependency>();

        // Search for config files
        var configFiles = new List<string>();
        configFiles.AddRange(Directory.GetFiles(repoRoot, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj")));
        configFiles.AddRange(Directory.GetFiles(repoRoot, "config*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules")));

        foreach (var configFile in configFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(configFile);
                var doc = JsonDocument.Parse(json);

                // Detect queue workers from AzureQueueWorkerConfiguration
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

                // Detect API dependencies from URI config keys
                DetectApiDependencies(doc.RootElement, apis);
            }
            catch { /* skip unparseable files */ }
        }

        return (workers, apis);
    }

    private static void DetectApiDependencies(JsonElement element, List<ApiDependency> apis)
    {
        var uriPattern = new Regex(@"^(https?://|http://)", RegexOptions.IgnoreCase);
        var knownUriKeys = new[] { "CollectorUri", "DisplayCatalogUri", "TaxUri", "BillingGroupUri",
            "FIServiceUri", "QuoteServiceUri", "KeyVaultUri" };

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String &&
                prop.Name.EndsWith("Uri", StringComparison.OrdinalIgnoreCase))
            {
                var key = prop.Name;
                if (!apis.Any(a => a.ConfigKey == key))
                {
                    apis.Add(new ApiDependency
                    {
                        ConfigKey = key,
                        BasePath = $"/{key.Replace("Uri", "").ToLowerInvariant()}",
                        DefaultResponse = "[]"
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
        return Directory.GetFiles(repoRoot, "*SltTest*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? Directory.GetFiles(repoRoot, "*IntegrationTest*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();
    }

    private static (List<string> Blobs, List<string> Tables) DetectStorageRequirements(string repoRoot)
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
                var json = File.ReadAllText(configFile);
                var doc = JsonDocument.Parse(json);

                // Look for table configurations
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
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
                }
            }
            catch { }
        }

        return (blobs, tables);
    }

    private static string DeriveQueueName(string workerName)
    {
        // "BillingPurchaseLineOrganizationQueueWorkerV1" → "billingpurchaselineorganization"
        var name = workerName
            .Replace("QueueWorkerV1", "", StringComparison.OrdinalIgnoreCase)
            .Replace("QueueWorker", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Worker", "", StringComparison.OrdinalIgnoreCase);
        return name.ToLowerInvariant();
    }

    private static string ToPascalCase(string name)
    {
        return string.Concat(
            name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
