using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FinPlat.TestContainers.CI;

/// <summary>
/// Analyzes git diffs to determine which SLT tests should run based on changed files.
/// Uses a configurable file-to-test mapping and merge-base for accurate diff calculation.
/// </summary>
public class TestSelector
{
    private readonly List<TestMapping> _mappings = new();
    private string _baseBranch = "origin/main";
    private string _repoRoot = ".";

    /// <summary>
    /// Sets the base branch for diff calculation. Default: "origin/main".
    /// </summary>
    public TestSelector WithBaseBranch(string branch)
    {
        _baseBranch = branch;
        return this;
    }

    /// <summary>
    /// Sets the repository root directory. Default: current directory.
    /// </summary>
    public TestSelector WithRepoRoot(string path)
    {
        _repoRoot = path;
        return this;
    }

    /// <summary>
    /// Maps a file glob pattern to one or more test classes.
    /// When files matching the pattern change, the specified tests will run.
    /// </summary>
    /// <param name="globPattern">
    /// Glob pattern to match changed files (e.g., "src/Workers/BigCat/**", "*.csproj").
    /// </param>
    /// <param name="testClasses">Fully qualified test class names to run when pattern matches.</param>
    public TestSelector MapFiles(string globPattern, params string[] testClasses)
    {
        _mappings.Add(new TestMapping(globPattern, testClasses));
        return this;
    }

    /// <summary>
    /// Maps a directory to test classes. Shorthand for MapFiles("directory/**", ...).
    /// </summary>
    public TestSelector MapDirectory(string directory, params string[] testClasses)
    {
        var normalizedDir = directory.TrimEnd('/', '\\');
        return MapFiles($"{normalizedDir}/**", testClasses);
    }

    /// <summary>
    /// Analyzes the git diff and returns the list of affected test classes.
    /// Uses merge-base for accurate diff calculation (handles shallow clones).
    /// </summary>
    /// <returns>Distinct list of test class names that should run.</returns>
    public async Task<TestSelectionResult> SelectAsync()
    {
        var result = new TestSelectionResult();

        // Get changed files using merge-base
        var changedFiles = await GetChangedFilesAsync();
        result.ChangedFiles.AddRange(changedFiles);

        if (changedFiles.Count == 0)
        {
            result.Reason = "No changed files detected.";
            return result;
        }

        // Match changed files to test mappings
        var matchedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedMappings = new List<string>();

        foreach (var file in changedFiles)
        {
            // If a test file itself changed, always include it
            if (IsTestFile(file))
            {
                var testClassName = ExtractTestClassName(file);
                if (testClassName is not null)
                {
                    matchedTests.Add(testClassName);
                    matchedMappings.Add($"Test file changed: {file} → {testClassName}");
                }
            }

            // Check mappings
            foreach (var mapping in _mappings)
            {
                if (GlobMatch(file, mapping.GlobPattern))
                {
                    foreach (var testClass in mapping.TestClasses)
                    {
                        matchedTests.Add(testClass);
                        matchedMappings.Add($"Mapping: {mapping.GlobPattern} matched {file} → {testClass}");
                    }
                }
            }
        }

        result.SelectedTests.AddRange(matchedTests);
        result.MatchDetails.AddRange(matchedMappings);

        if (matchedTests.Count == 0)
        {
            result.Reason = "No mappings matched changed files. Consider running all tests.";
            result.RunAll = true;
        }
        else
        {
            result.Reason = $"Selected {matchedTests.Count} test class(es) based on {changedFiles.Count} changed file(s).";
        }

        return result;
    }

    /// <summary>
    /// Generates a --filter argument for dotnet test.
    /// </summary>
    public static string ToDotnetTestFilter(IEnumerable<string> testClasses)
    {
        var classes = testClasses.ToList();
        if (classes.Count == 0) return "";

        return string.Join("|", classes.Select(c => $"FullyQualifiedName~{c}"));
    }

    private async Task<List<string>> GetChangedFilesAsync()
    {
        // Try merge-base first (most accurate)
        var mergeBase = await RunGitAsync($"merge-base HEAD {_baseBranch}");
        if (!string.IsNullOrWhiteSpace(mergeBase))
        {
            var diffOutput = await RunGitAsync($"diff --name-only {mergeBase.Trim()} HEAD");
            var files = ParseFileList(diffOutput);
            if (files.Count > 0) return files;
        }

        // Fallback for shallow clones: diff against base branch directly
        var fallbackOutput = await RunGitAsync($"diff --name-only {_baseBranch}...HEAD");
        var fallbackFiles = ParseFileList(fallbackOutput);
        if (fallbackFiles.Count > 0) return fallbackFiles;

        // Last resort: uncommitted changes
        var statusOutput = await RunGitAsync("diff --name-only HEAD");
        return ParseFileList(statusOutput);
    }

    private async Task<string> RunGitAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    private static List<string> ParseFileList(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim().Replace('\\', '/'))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();
    }

    private static bool IsTestFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("SltTests.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTestClassName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    /// <summary>
    /// Simple glob matching supporting *, **, and ? wildcards.
    /// </summary>
    internal static bool GlobMatch(string path, string pattern)
    {
        // Normalize separators
        path = path.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        // Convert glob to regex
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", "§DOUBLESTAR§")
            .Replace(@"\*", "[^/]*")
            .Replace("§DOUBLESTAR§", ".*")
            .Replace(@"\?", "[^/]") + "$";

        return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
    }

    private record TestMapping(string GlobPattern, string[] TestClasses);
}

/// <summary>Result of a test selection analysis.</summary>
public class TestSelectionResult
{
    /// <summary>Files that changed in the git diff.</summary>
    public List<string> ChangedFiles { get; } = new();

    /// <summary>Test classes selected for execution.</summary>
    public List<string> SelectedTests { get; } = new();

    /// <summary>Details of which mappings matched which files.</summary>
    public List<string> MatchDetails { get; } = new();

    /// <summary>Human-readable summary of the selection.</summary>
    public string Reason { get; set; } = "";

    /// <summary>If true, no mappings matched — caller should run all tests as a safety fallback.</summary>
    public bool RunAll { get; set; }

    /// <summary>Generates the --filter argument for dotnet test.</summary>
    public string ToDotnetTestFilter() => TestSelector.ToDotnetTestFilter(SelectedTests);
}
