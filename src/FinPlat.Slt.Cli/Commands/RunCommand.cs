using System.CommandLine;
using FinPlat.Slt.Cli.Models;

namespace FinPlat.Slt.Cli.Commands;

public static class RunCommand
{
    public static Command Create()
    {
        var cmd = new Command("run", "Build the Docker image and run SLT tests");

        var filterOpt = new Option<string?>("--filter", "MSTest filter expression (e.g. TestCategory=SLT)");
        var noBuildOpt = new Option<bool>("--no-build", "Skip Docker image rebuild");
        var verboseOpt = new Option<bool>("--verbose", "Show detailed output");
        var pathOpt = new Option<string>("--path", () => ".", "Path to the repo root");

        cmd.AddOption(filterOpt);
        cmd.AddOption(noBuildOpt);
        cmd.AddOption(verboseOpt);
        cmd.AddOption(pathOpt);

        cmd.SetHandler(async (string? filter, bool noBuild, bool verbose, string path) =>
        {
            var repoRoot = Path.GetFullPath(path);
            var manifestPath = Path.Combine(repoRoot, ".slt", "slt.json");

            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine("❌ No .slt/slt.json found. Run: dotnet-slt init");
                Environment.Exit(1);
                return;
            }

            var manifest = SltManifest.LoadFromFile(manifestPath);

            // Step 1: Build Docker image
            if (!noBuild)
            {
                Console.WriteLine("🐳 Building Docker image...\n");
                var dockerResult = await RunProcessAsync(
                    "docker", $"build -t {manifest.ServiceName}-slt -f .slt/docker/Dockerfile.slt .",
                    repoRoot, verbose);

                if (dockerResult != 0)
                {
                    Console.Error.WriteLine("❌ Docker build failed");
                    Environment.Exit(1);
                    return;
                }
                Console.WriteLine("  ✅ Docker image built\n");
            }

            // Step 2: Run tests
            Console.WriteLine("🧪 Running SLT tests...\n");

            var testArgs = $"test \"{manifest.Tests.ProjectPath}\"";
            if (!string.IsNullOrEmpty(filter))
                testArgs += $" --filter \"{filter}\"";
            testArgs += " --logger \"console;verbosity=normal\"";
            if (verbose)
                testArgs += " --verbosity detailed";

            var testResult = await RunProcessAsync("dotnet", testArgs, repoRoot, true);

            Console.WriteLine();
            if (testResult == 0)
                Console.WriteLine("✅ All SLT tests passed!");
            else
            {
                Console.WriteLine("❌ Some SLT tests failed.");
                Environment.Exit(testResult);
            }

        }, filterOpt, noBuildOpt, verboseOpt, pathOpt);

        return cmd;
    }

    private static async Task<int> RunProcessAsync(string command, string args, string workDir, bool showOutput)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(command, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception($"Failed to start {command}");

        var outTask = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (showOutput) Console.WriteLine($"  {line}");
            }
        });

        var errTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync() is { } line)
            {
                if (showOutput) Console.Error.WriteLine($"  {line}");
            }
        });

        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
