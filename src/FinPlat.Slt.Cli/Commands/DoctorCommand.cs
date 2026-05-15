using System.CommandLine;

namespace FinPlat.Slt.Cli.Commands;

public static class DoctorCommand
{
    public static Command Create()
    {
        var cmd = new Command("doctor", "Verify SLT prerequisites (Docker, .NET SDK, etc.)");

        cmd.SetHandler(async () =>
        {
            Console.WriteLine("🩺 Checking SLT prerequisites...\n");

            var allGood = true;

            // Check Docker
            allGood &= await CheckCommandAsync("docker", "--version", "Docker");

            // Check Docker running
            allGood &= await CheckCommandAsync("docker", "info --format '{{.ServerVersion}}'", "Docker daemon");

            // Check .NET SDK
            allGood &= await CheckCommandAsync("dotnet", "--version", ".NET SDK");

            // Check if .slt directory exists
            var sltDir = Path.Combine(Directory.GetCurrentDirectory(), ".slt");
            if (Directory.Exists(sltDir))
            {
                Console.WriteLine("  ✅ .slt/ directory found");

                var manifestPath = Path.Combine(sltDir, "slt.json");
                if (File.Exists(manifestPath))
                    Console.WriteLine("  ✅ .slt/slt.json manifest found");
                else
                {
                    Console.WriteLine("  ❌ .slt/slt.json not found — run: dotnet-slt init");
                    allGood = false;
                }
            }
            else
            {
                Console.WriteLine("  ⚠️  .slt/ directory not found — run: dotnet-slt init");
                allGood = false;
            }

            Console.WriteLine();
            Console.WriteLine(allGood
                ? "✅ All prerequisites met. Ready to run SLT tests!"
                : "❌ Some prerequisites are missing. Fix the issues above and re-run.");
        });

        return cmd;
    }

    private static async Task<bool> CheckCommandAsync(string command, string args, string label)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine($"  ❌ {label}: not found");
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                Console.WriteLine($"  ✅ {label}: {output.Trim().Split('\n')[0]}");
                return true;
            }
            else
            {
                Console.WriteLine($"  ❌ {label}: failed (exit code {proc.ExitCode})");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {label}: {ex.Message}");
            return false;
        }
    }
}
