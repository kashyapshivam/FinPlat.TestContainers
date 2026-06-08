using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace FinPlat.TestContainers.Reporting;

/// <summary>
/// Parses TRX (Visual Studio Test Results) files and generates structured reports.
/// Handles XML namespace variations across different TRX versions.
/// </summary>
public static class TestResultParser
{
    /// <summary>
    /// Parses a TRX file and returns a structured test report.
    /// </summary>
    /// <param name="trxFilePath">Path to the .trx file.</param>
    public static TestReport ParseTrx(string trxFilePath)
    {
        if (!File.Exists(trxFilePath))
            throw new FileNotFoundException($"TRX file not found: {trxFilePath}", trxFilePath);

        var doc = XDocument.Load(trxFilePath);
        var root = doc.Root!;
        var ns = root.GetDefaultNamespace();

        var report = new TestReport { SourceFile = trxFilePath };

        // Parse test run times
        var times = root.Element(ns + "Times");
        if (times is not null)
        {
            if (DateTime.TryParse(times.Attribute("start")?.Value, out var start))
                report.StartTime = start;
            if (DateTime.TryParse(times.Attribute("finish")?.Value, out var finish))
                report.EndTime = finish;
            if (report.StartTime.HasValue && report.EndTime.HasValue)
                report.Duration = report.EndTime.Value - report.StartTime.Value;
        }

        // Parse result summary
        var resultSummary = root.Element(ns + "ResultSummary");
        if (resultSummary is not null)
        {
            report.Outcome = resultSummary.Attribute("outcome")?.Value ?? "Unknown";
            var counters = resultSummary.Element(ns + "Counters");
            if (counters is not null)
            {
                report.Total = ParseInt(counters, "total");
                report.Passed = ParseInt(counters, "passed");
                report.Failed = ParseInt(counters, "failed");
                report.Skipped = ParseInt(counters, "notExecuted") + ParseInt(counters, "disconnected")
                    + ParseInt(counters, "aborted") + ParseInt(counters, "timeout");
            }
        }

        // Parse individual test results
        var results = root.Element(ns + "Results");
        if (results is not null)
        {
            foreach (var unitResult in results.Elements(ns + "UnitTestResult"))
            {
                var testCase = new TestCaseResult
                {
                    TestName = unitResult.Attribute("testName")?.Value ?? "Unknown",
                    Outcome = unitResult.Attribute("outcome")?.Value ?? "Unknown",
                    Duration = ParseDuration(unitResult.Attribute("duration")?.Value),
                    ComputerName = unitResult.Attribute("computerName")?.Value,
                };

                // Parse error info
                var output = unitResult.Element(ns + "Output");
                if (output is not null)
                {
                    var errorInfo = output.Element(ns + "ErrorInfo");
                    if (errorInfo is not null)
                    {
                        testCase.ErrorMessage = errorInfo.Element(ns + "Message")?.Value;
                        testCase.StackTrace = errorInfo.Element(ns + "StackTrace")?.Value;
                    }
                    testCase.StdOut = output.Element(ns + "StdOut")?.Value;
                }

                report.TestCases.Add(testCase);
            }
        }

        return report;
    }

    /// <summary>
    /// Parses multiple TRX files and merges into a single report.
    /// </summary>
    public static TestReport ParseMultipleTrx(IEnumerable<string> trxFilePaths)
    {
        var merged = new TestReport();
        foreach (var path in trxFilePaths)
        {
            var report = ParseTrx(path);
            merged.Total += report.Total;
            merged.Passed += report.Passed;
            merged.Failed += report.Failed;
            merged.Skipped += report.Skipped;
            merged.TestCases.AddRange(report.TestCases);

            if (report.Duration.HasValue)
            {
                merged.Duration = (merged.Duration ?? TimeSpan.Zero) + report.Duration.Value;
            }
        }
        merged.Outcome = merged.Failed > 0 ? "Failed" : "Passed";
        return merged;
    }

    /// <summary>
    /// Finds all TRX files in a directory (recursive).
    /// </summary>
    public static IEnumerable<string> FindTrxFiles(string directory)
    {
        return Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
    }

    private static int ParseInt(XElement element, string attributeName)
    {
        return int.TryParse(element.Attribute(attributeName)?.Value, out var value) ? value : 0;
    }

    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }
}

/// <summary>
/// Aggregated test report with summary statistics and per-test details.
/// </summary>
public class TestReport
{
    public string? SourceFile { get; set; }
    public string Outcome { get; set; } = "Unknown";
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public TimeSpan? Duration { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public List<TestCaseResult> TestCases { get; set; } = new();

    /// <summary>Pass rate as a percentage (0-100).</summary>
    public double PassRate => Total > 0 ? (double)Passed / Total * 100 : 0;

    /// <summary>Failed test cases only.</summary>
    public IEnumerable<TestCaseResult> Failures => TestCases.Where(t => t.Outcome == "Failed");

    /// <summary>Slowest test cases.</summary>
    public IEnumerable<TestCaseResult> SlowestTests(int count = 5)
        => TestCases.Where(t => t.Duration.HasValue).OrderByDescending(t => t.Duration).Take(count);

    /// <summary>Generates a markdown summary table.</summary>
    public string ToMarkdownSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## SLT Test Report");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| **Outcome** | {(Outcome == "Passed" ? "✅ Passed" : "❌ Failed")} |");
        sb.AppendLine($"| Total | {Total} |");
        sb.AppendLine($"| Passed | {Passed} |");
        sb.AppendLine($"| Failed | {Failed} |");
        sb.AppendLine($"| Skipped | {Skipped} |");
        sb.AppendLine($"| Pass Rate | {PassRate:F1}% |");
        if (Duration.HasValue)
            sb.AppendLine($"| Duration | {Duration.Value.TotalSeconds:F1}s |");
        sb.AppendLine();

        // Failed tests detail
        var failures = Failures.ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("### ❌ Failed Tests");
            sb.AppendLine();
            foreach (var f in failures)
            {
                sb.AppendLine($"- **{f.TestName}**");
                if (f.ErrorMessage is not null)
                    sb.AppendLine($"  > {f.ErrorMessage.Split('\n')[0]}");
            }
            sb.AppendLine();
        }

        // Slowest tests
        var slowest = SlowestTests(5).ToList();
        if (slowest.Count > 0)
        {
            sb.AppendLine("### ⏱️ Slowest Tests");
            sb.AppendLine();
            sb.AppendLine("| Test | Duration |");
            sb.AppendLine("|------|----------|");
            foreach (var t in slowest)
                sb.AppendLine($"| {t.TestName} | {t.Duration!.Value.TotalSeconds:F2}s |");
        }

        return sb.ToString();
    }

    /// <summary>Generates a JSON summary.</summary>
    public string ToJsonSummary()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            outcome = Outcome,
            total = Total,
            passed = Passed,
            failed = Failed,
            skipped = Skipped,
            passRate = Math.Round(PassRate, 1),
            durationSeconds = Duration?.TotalSeconds,
            failures = Failures.Select(f => new { name = f.TestName, error = f.ErrorMessage }).ToList(),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>Result of a single test case.</summary>
public class TestCaseResult
{
    public string TestName { get; set; } = "";
    public string Outcome { get; set; } = "Unknown";
    public TimeSpan? Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? StdOut { get; set; }
    public string? ComputerName { get; set; }
}
