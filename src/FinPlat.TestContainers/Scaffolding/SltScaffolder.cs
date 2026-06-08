using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FinPlat.TestContainers.Scaffolding;

/// <summary>
/// Generates SLT scaffolding files for a new service.
/// Creates Dockerfile.slt, config.docker.json, and a test class boilerplate.
/// Designed as a library API that can be wrapped by a CLI tool later.
/// </summary>
public static class SltScaffolder
{
    /// <summary>
    /// Generates all scaffolding files for an SLT test suite.
    /// </summary>
    /// <param name="options">Configuration options for the scaffolding.</param>
    /// <returns>List of generated file paths.</returns>
    public static async Task<ScaffoldResult> GenerateAsync(ScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ServiceName))
            throw new ArgumentException("ServiceName is required.", nameof(options));

        var result = new ScaffoldResult();
        var servicePascal = ToPascalCase(options.ServiceName);

        // Create directories
        var sltDir = Path.Combine(options.OutputDirectory, "docker");
        var testDir = options.TestProjectDirectory;

        if (options.CreateDirectories)
        {
            Directory.CreateDirectory(sltDir);
            if (testDir is not null) Directory.CreateDirectory(testDir);
        }

        // Generate Dockerfile.slt
        var dockerfilePath = Path.Combine(sltDir, "Dockerfile.slt");
        await WriteIfAllowedAsync(dockerfilePath, GenerateDockerfile(options), options, result);

        // Generate config.docker.json
        var configPath = Path.Combine(sltDir, "config.docker.json");
        await WriteIfAllowedAsync(configPath, GenerateConfig(options), options, result);

        // Generate test class
        if (testDir is not null)
        {
            var testPath = Path.Combine(testDir, $"{servicePascal}SltTests.cs");
            await WriteIfAllowedAsync(testPath, GenerateTestClass(options, servicePascal), options, result);
        }

        // Generate scenario file
        var scenarioDir = Path.Combine(options.OutputDirectory, "Scenarios", servicePascal);
        if (options.CreateDirectories) Directory.CreateDirectory(scenarioDir);

        var scenarioPath = Path.Combine(scenarioDir, "happy-path.json");
        await WriteIfAllowedAsync(scenarioPath, GenerateScenarioFile(), options, result);

        return result;
    }

    /// <summary>
    /// Returns what files would be generated without writing them (dry run).
    /// </summary>
    public static ScaffoldResult Preview(ScaffoldOptions options)
    {
        var result = new ScaffoldResult();
        var servicePascal = ToPascalCase(options.ServiceName);
        var sltDir = Path.Combine(options.OutputDirectory, "docker");

        result.PlannedFiles.Add(Path.Combine(sltDir, "Dockerfile.slt"));
        result.PlannedFiles.Add(Path.Combine(sltDir, "config.docker.json"));

        if (options.TestProjectDirectory is not null)
            result.PlannedFiles.Add(Path.Combine(options.TestProjectDirectory, $"{servicePascal}SltTests.cs"));

        result.PlannedFiles.Add(Path.Combine(options.OutputDirectory, "Scenarios", servicePascal, "happy-path.json"));

        foreach (var f in result.PlannedFiles)
        {
            if (File.Exists(f))
                result.SkippedFiles.Add(f);
        }

        return result;
    }

    private static string GenerateDockerfile(ScaffoldOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base");
        sb.AppendLine("WORKDIR /app");
        sb.AppendLine($"EXPOSE {options.ServicePort}");
        sb.AppendLine();
        sb.AppendLine("# Copy pre-published output");
        sb.AppendLine("COPY publish/ /app/");
        sb.AppendLine();
        sb.AppendLine("# Install CA certificate for Azurite HTTPS (token auth)");
        sb.AppendLine("COPY docker/ca.crt /usr/local/share/ca-certificates/azurite-ca.crt");
        sb.AppendLine("RUN update-ca-certificates");
        sb.AppendLine();
        sb.AppendLine($"ENV ASPNETCORE_URLS=http://+:{options.ServicePort}");
        sb.AppendLine("ENV ASPNETCORE_ENVIRONMENT=Development");
        sb.AppendLine();
        sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{options.ServiceName}.dll\"]");
        return sb.ToString();
    }

    private static string GenerateConfig(ScaffoldOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"Environment\": \"docker\",");

        if (options.QueueNames.Count > 0)
        {
            sb.AppendLine("  \"AzureQueueWorkerConfiguration\": [");
            for (int i = 0; i < options.QueueNames.Count; i++)
            {
                var comma = i < options.QueueNames.Count - 1 ? "," : "";
                sb.AppendLine("    {");
                sb.AppendLine($"      \"Name\": \"{ToPascalCase(options.QueueNames[i])}Worker\",");
                sb.AppendLine($"      \"QueueName\": \"{options.QueueNames[i]}\",");
                sb.AppendLine("      \"Enabled\": true");
                sb.AppendLine($"    }}{comma}");
            }
            sb.AppendLine("  ],");
        }

        sb.AppendLine("  \"Logging\": {");
        sb.AppendLine("    \"LogLevel\": {");
        sb.AppendLine("      \"Default\": \"Information\"");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTestClass(ScaffoldOptions options, string servicePascal)
    {
        var queues = string.Join("\n", options.QueueNames.Select(q =>
            $"            wire.Queue(\"{q}\").UseTokenAuth();"));

        var mocks = string.Join("\n", options.MockApis.Select(m =>
            $"            wire.MockApi(\"services\", \"{m}\");"));

        var wirings = queues;
        if (!string.IsNullOrEmpty(mocks))
            wirings += "\n" + mocks;

        var ns = options.TestNamespace ?? $"{servicePascal}.SltTests";
        var firstQueue = options.QueueNames.Count > 0 ? options.QueueNames[0] : "my-queue";

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using FinPlat.TestContainers;");
        sb.AppendLine("using FinPlat.TestContainers.Builder;");
        sb.AppendLine("using FinPlat.TestContainers.Fixtures;");
        sb.AppendLine("using Microsoft.VisualStudio.TestTools.UnitTesting;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[TestClass]");
        sb.AppendLine($"public class {servicePascal}SltTests");
        sb.AppendLine("{");
        sb.AppendLine("    private static TestEnvironment _env = null!;");
        sb.AppendLine();
        sb.AppendLine("    [ClassInitialize]");
        sb.AppendLine("    public static async Task ClassInit(TestContext context)");
        sb.AppendLine("    {");
        sb.AppendLine("        var sltContext = GetSltContext();");
        sb.AppendLine();
        sb.AppendLine("        _env = await new TestEnvironmentBuilder()");
        sb.AppendLine("            .AddAzurite(opts => opts.UseTokenAuth = true)");
        sb.AppendLine("            .AddMockApi(\"services\", mock =>");
        sb.AppendLine("            {");
        sb.AppendLine("                // TODO: Configure mock API stubs");
        sb.AppendLine("            })");
        sb.AppendLine($"            .AddApplication(\"{options.ServiceName}\", app =>");
        sb.AppendLine("            {");
        sb.AppendLine("                app.FromDockerfile(\"docker/Dockerfile.slt\", contextPath: sltContext);");
        sb.AppendLine($"                app.WithInternalPort({options.ServicePort});");
        sb.AppendLine($"                app.WithHttpHealthCheck(\"{options.HealthCheckPath}\", timeoutSeconds: 90);");
        sb.AppendLine("            })");
        sb.AppendLine($"            .Wire(\"{options.ServiceName}\", wire =>");
        sb.AppendLine("            {");
        if (!string.IsNullOrWhiteSpace(wirings))
            sb.AppendLine(wirings);
        sb.AppendLine("            })");
        sb.AppendLine("            .BuildAsync();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [ClassCleanup]");
        sb.AppendLine("    public static async Task ClassCleanup()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_env is not null)");
        sb.AppendLine("            await _env.DisposeAsync();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [TestMethod]");
        sb.AppendLine("    public async Task HappyPath_ProcessesSuccessfully()");
        sb.AppendLine("    {");
        sb.AppendLine("        // Arrange — load fixture and send to queue");
        sb.AppendLine($"        // var message = FixtureFile.LoadBase64(");
        sb.AppendLine($"        //     Path.Combine(GetSltContext(), \"Scenarios/{servicePascal}/happy-path.json\"));");
        sb.AppendLine($"        // await _env.Queue(\"{firstQueue}\").SendAsync(message);");
        sb.AppendLine();
        sb.AppendLine("        // Act — wait for processing");
        sb.AppendLine("        // await Task.Delay(5000);");
        sb.AppendLine();
        sb.AppendLine("        // Assert");
        sb.AppendLine("        // await _env.MockApi(\"services\").AssertCalledAsync(\"/expected-endpoint\");");
        sb.AppendLine("        Assert.IsTrue(true, \"TODO: Implement happy path assertion\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [TestMethod]");
        sb.AppendLine("    public async Task WorkerRemainsHealthy_AfterProcessing()");
        sb.AppendLine("    {");
        sb.AppendLine($"        var logs = await _env.GetLogsAsync(\"{options.ServiceName}\");");
        sb.AppendLine("        Assert.IsFalse(string.IsNullOrEmpty(logs), \"Worker should have produced log output\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static string GetSltContext()");
        sb.AppendLine("    {");
        sb.AppendLine("        var dir = new DirectoryInfo(AppContext.BaseDirectory);");
        sb.AppendLine("        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, \".slt\")))");
        sb.AppendLine("            dir = dir.Parent;");
        sb.AppendLine("        return Path.Combine(dir!.FullName, \".slt\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateScenarioFile()
    {
        return """
            {
              "_comment": "Replace this with your actual test message payload",
              "id": "test-id-001",
              "type": "TestEvent",
              "data": {
                "orderId": "ORD-12345",
                "status": "Active"
              }
            }
            """;
    }

    private static async Task WriteIfAllowedAsync(
        string path, string content, ScaffoldOptions options, ScaffoldResult result)
    {
        if (options.DryRun)
        {
            result.PlannedFiles.Add(path);
            return;
        }

        if (File.Exists(path) && !options.OverwriteExisting)
        {
            result.SkippedFiles.Add(path);
            return;
        }

        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        result.GeneratedFiles.Add(path);
    }

    private static string ToPascalCase(string name)
    {
        return string.Concat(
            name.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

/// <summary>Configuration for SLT scaffolding generation.</summary>
public class ScaffoldOptions
{
    /// <summary>Service name (e.g., "order-service"). Used for Docker, config, and class naming.</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Root output directory for SLT files (e.g., ".slt/").</summary>
    public string OutputDirectory { get; set; } = ".slt";

    /// <summary>Test project directory (e.g., "tests/SltTests/"). Null to skip test class generation.</summary>
    public string? TestProjectDirectory { get; set; }

    /// <summary>Namespace for the generated test class.</summary>
    public string? TestNamespace { get; set; }

    /// <summary>Queue names the service reads from.</summary>
    public List<string> QueueNames { get; set; } = new();

    /// <summary>External API endpoint names to mock (injected as env vars).</summary>
    public List<string> MockApis { get; set; } = new();

    /// <summary>Port the service listens on inside the container.</summary>
    public int ServicePort { get; set; } = 8080;

    /// <summary>Health check endpoint path.</summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>If true, overwrite existing files. Default: false (skip existing).</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>If true, only report what would be generated. Default: false.</summary>
    public bool DryRun { get; set; }

    /// <summary>If true, create output directories automatically. Default: true.</summary>
    public bool CreateDirectories { get; set; } = true;
}

/// <summary>Result of a scaffolding operation.</summary>
public class ScaffoldResult
{
    /// <summary>Files that were successfully generated.</summary>
    public List<string> GeneratedFiles { get; } = new();

    /// <summary>Files that were skipped (already exist and OverwriteExisting is false).</summary>
    public List<string> SkippedFiles { get; } = new();

    /// <summary>Files that would be generated (dry run mode).</summary>
    public List<string> PlannedFiles { get; } = new();
}
