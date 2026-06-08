using System.CommandLine;
using FinPlat.Slt.Cli.Detection;
using FinPlat.Slt.Cli.Generation;
using FinPlat.Slt.Cli.Models;

namespace FinPlat.Slt.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var cmd = new Command("init", "Detect service configuration and scaffold SLT tests (zero-config)");

        var interactiveOpt = new Option<bool>("--interactive", "Prompt for each decision");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview what would be generated without writing files");
        var overwriteOpt = new Option<bool>("--overwrite", "Overwrite existing files");
        var pathOpt = new Option<string>("--path", () => ".", "Path to the repo root");

        cmd.AddOption(interactiveOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddOption(overwriteOpt);
        cmd.AddOption(pathOpt);

        cmd.SetHandler(async (bool interactive, bool dryRun, bool overwrite, string path) =>
        {
            var repoRoot = Path.GetFullPath(path);
            Console.WriteLine($"🔍 Scanning {repoRoot}...\n");

            // Phase 1: Detection
            var manifest = await ServiceDetector.DetectAsync(repoRoot);

            // Print detection summary
            PrintDetectionSummary(manifest);

            if (interactive)
            {
                manifest = await ConfirmInteractively(manifest);
            }

            // Phase 2: Generation
            Console.WriteLine("\n📁 Generating SLT files...\n");

            var options = new GenerationOptions
            {
                DryRun = dryRun,
                Overwrite = overwrite,
                Interactive = interactive
            };

            var result = await SltGenerator.GenerateAsync(manifest, repoRoot, options);

            // Print result
            PrintGenerationResult(result, repoRoot, dryRun);

        }, interactiveOpt, dryRunOpt, overwriteOpt, pathOpt);

        return cmd;
    }

    private static void PrintDetectionSummary(SltManifest manifest)
    {
        Console.WriteLine("┌─────────────────────────────────────────┐");
        Console.WriteLine("│         Detected Configuration          │");
        Console.WriteLine("├─────────────────────────────────────────┤");
        Console.WriteLine($"│ Service:   {manifest.ServiceName,-28}│");
        Console.WriteLine($"│ Project:   {manifest.ServiceProject,-28}│");
        Console.WriteLine("├─────────────────────────────────────────┤");

        if (manifest.Workers.Count > 0)
        {
            Console.WriteLine("│ Workers:                                │");
            foreach (var w in manifest.Workers)
            {
                Console.WriteLine($"│   • {w.Name,-35}│");
                Console.WriteLine($"│     queue: {w.QueueName,-28}│");
            }
        }

        if (manifest.ExternalApis.Count > 0)
        {
            Console.WriteLine("├─────────────────────────────────────────┤");
            Console.WriteLine("│ External APIs (will be mocked):         │");
            foreach (var a in manifest.ExternalApis)
            {
                Console.WriteLine($"│   • {a.ConfigKey,-35}│");
            }
        }

        if (manifest.Infrastructure.Tables.Count > 0)
        {
            Console.WriteLine("├─────────────────────────────────────────┤");
            Console.WriteLine("│ Storage:                                │");
            foreach (var t in manifest.Infrastructure.Tables)
            {
                Console.WriteLine($"│   table: {t,-30}│");
            }
        }

        Console.WriteLine("└─────────────────────────────────────────┘");
    }

    private static void PrintGenerationResult(GenerationResult result, string repoRoot, bool dryRun)
    {
        var verb = dryRun ? "Would generate" : "Generated";

        var files = dryRun ? result.Planned : result.Generated;
        if (files.Count > 0)
        {
            Console.WriteLine($"✅ {verb}:");
            foreach (var f in files)
            {
                Console.WriteLine($"   {Path.GetRelativePath(repoRoot, f)}");
            }
        }

        if (result.Skipped.Count > 0)
        {
            Console.WriteLine($"\n⏭️  Skipped (already exist):");
            foreach (var f in result.Skipped)
            {
                Console.WriteLine($"   {Path.GetRelativePath(repoRoot, f)}");
            }
        }

        Console.WriteLine($"\n🚀 Next steps:");
        Console.WriteLine("   1. Review .slt/slt.json and customize if needed");
        Console.WriteLine("   2. Update Scenarios/ with real test fixtures");
        Console.WriteLine("   3. Run: dotnet-slt run");
    }

    private static Task<SltManifest> ConfirmInteractively(SltManifest manifest)
    {
        // In interactive mode, we'd prompt for confirmation of each detected value
        // For now, just return the auto-detected manifest
        Console.Write("\nProceed with detected configuration? [Y/n] ");
        var response = Console.ReadLine()?.Trim().ToLower();
        if (response == "n" || response == "no")
        {
            Console.WriteLine("Aborted. Edit .slt/slt.json manually and re-run.");
            Environment.Exit(0);
        }
        return Task.FromResult(manifest);
    }
}
