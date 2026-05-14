using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FinPlat.TestContainers.Assertions;

/// <summary>
/// Fluent assertion builder for container log output.
/// Supports literal text matching, regex patterns, error detection,
/// and ordered sequence verification.
/// </summary>
public class LogAssertionBuilder
{
    private readonly string _logs;
    private readonly string[] _lines;
    private readonly string _appName;
    private readonly List<string> _failures = new();

    internal LogAssertionBuilder(string logs, string appName)
    {
        _logs = logs;
        _appName = appName;
        _lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Asserts that at least one log line contains the specified text (case-insensitive).
    /// </summary>
    public LogAssertionBuilder ContainsLine(string text)
    {
        if (!_lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase)))
            _failures.Add($"Expected log to contain '{text}' but it was not found.");
        return this;
    }

    /// <summary>
    /// Asserts that at least one log line matches the specified regex pattern.
    /// </summary>
    public LogAssertionBuilder ContainsLineMatching(string regexPattern)
    {
        if (!_lines.Any(l => Regex.IsMatch(l, regexPattern, RegexOptions.IgnoreCase)))
            _failures.Add($"Expected log to match pattern '{regexPattern}' but no match found.");
        return this;
    }

    /// <summary>
    /// Asserts that no log line contains the specified text (case-insensitive).
    /// </summary>
    public LogAssertionBuilder DoesNotContain(string text)
    {
        var matchingLine = _lines.FirstOrDefault(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (matchingLine is not null)
            _failures.Add($"Expected log NOT to contain '{text}', but found: {matchingLine.Trim()}");
        return this;
    }

    /// <summary>
    /// Asserts that no log line matches the specified regex pattern.
    /// </summary>
    public LogAssertionBuilder DoesNotContainMatching(string regexPattern)
    {
        var matchingLine = _lines.FirstOrDefault(l => Regex.IsMatch(l, regexPattern, RegexOptions.IgnoreCase));
        if (matchingLine is not null)
            _failures.Add($"Expected log NOT to match '{regexPattern}', but found: {matchingLine.Trim()}");
        return this;
    }

    /// <summary>
    /// Asserts that no log lines contain common error indicators.
    /// Detects: "ERROR", "FATAL", unhandled exceptions, and stack traces.
    /// Ignores lines that contain known false-positive patterns (e.g., "no error").
    /// </summary>
    /// <param name="additionalIgnorePatterns">
    /// Extra regex patterns to ignore (lines matching these won't trigger a failure).
    /// </param>
    public LogAssertionBuilder HasNoErrors(params string[] additionalIgnorePatterns)
    {
        var errorPatterns = new[]
        {
            @"\bERROR\b",
            @"\bFATAL\b",
            @"\bUnhandled\s+[Ee]xception\b",
            @"\bSystem\.\w+Exception\b",
        };

        var ignorePatterns = new List<string>(DefaultIgnorePatterns);
        ignorePatterns.AddRange(additionalIgnorePatterns);

        foreach (var line in _lines)
        {
            // Skip lines matching ignore patterns
            if (ignorePatterns.Any(p => Regex.IsMatch(line, p, RegexOptions.IgnoreCase)))
                continue;

            foreach (var errorPattern in errorPatterns)
            {
                if (Regex.IsMatch(line, errorPattern, RegexOptions.IgnoreCase))
                {
                    _failures.Add($"Found error in log: {line.Trim()}");
                    break;
                }
            }
        }
        return this;
    }

    /// <summary>
    /// Asserts that the specified texts appear in order in the log output.
    /// Useful for verifying startup sequences or processing order.
    /// </summary>
    public LogAssertionBuilder HasSequence(params string[] orderedTexts)
    {
        int lastIndex = -1;
        foreach (var text in orderedTexts)
        {
            int foundIndex = -1;
            for (int i = lastIndex + 1; i < _lines.Length; i++)
            {
                if (_lines[i].Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex < 0)
            {
                var after = lastIndex >= 0 ? $" (after '{orderedTexts[Array.IndexOf(orderedTexts, text) - 1]}')" : "";
                _failures.Add($"Expected '{text}' in log sequence{after}, but it was not found.");
                return this;
            }
            lastIndex = foundIndex;
        }
        return this;
    }

    /// <summary>
    /// Asserts a condition on the total number of log lines.
    /// </summary>
    public LogAssertionBuilder LineCount(Func<int, bool> predicate, string? description = null)
    {
        if (!predicate(_lines.Length))
            _failures.Add(
                description ?? $"Log line count {_lines.Length} did not satisfy the predicate.");
        return this;
    }

    /// <summary>
    /// Parses JSON-structured log lines and returns them for further assertion.
    /// Lines that are not valid JSON are skipped.
    /// </summary>
    public List<StructuredLogEntry> ParseStructuredLogs()
    {
        var entries = new List<StructuredLogEntry>();
        foreach (var line in _lines)
        {
            try
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('{')) continue;

                var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                entries.Add(new StructuredLogEntry
                {
                    Level = TryGetString(root, "level", "Level", "LogLevel", "Severity") ?? "Unknown",
                    Message = TryGetString(root, "message", "Message", "msg", "Msg") ?? "",
                    Timestamp = TryGetTimestamp(root),
                    RawJson = trimmed,
                    Properties = root,
                });
            }
            catch (JsonException)
            {
                // Not a JSON line — skip
            }
        }
        return entries;
    }

    /// <summary>
    /// Asserts that no structured log entries are at or above the specified level.
    /// Requires JSON-structured logs with a "level" or "Level" property.
    /// </summary>
    public LogAssertionBuilder HasNoStructuredLogsAtOrAbove(LogSeverity minLevel)
    {
        var entries = ParseStructuredLogs();
        foreach (var entry in entries)
        {
            var severity = ParseSeverity(entry.Level);
            if (severity >= minLevel)
            {
                _failures.Add($"Found {entry.Level} log: {entry.Message}");
            }
        }
        return this;
    }

    /// <summary>
    /// Throws an exception if any assertions failed.
    /// Call this at the end of the assertion chain.
    /// </summary>
    public void Verify()
    {
        if (_failures.Count > 0)
        {
            throw new LogAssertionException(
                $"Log assertions failed for '{_appName}' ({_failures.Count} failure(s)):\n" +
                string.Join("\n", _failures.Select(f => $"  • {f}")));
        }
    }

    private static readonly string[] DefaultIgnorePatterns =
    {
        @"\bno\s+error\b",
        @"\berror\s*count\s*[:=]\s*0\b",
        @"\berrors?\s*:\s*0\b",
        @"\b0\s+errors?\b",
        @"\bwithout\s+error\b",
        @"\berror\s+handling\b",
    };

    private static string? TryGetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root)
    {
        var names = new[] { "timestamp", "Timestamp", "@t", "time", "Time" };
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(prop.GetString(), out var ts))
            {
                return ts;
            }
        }
        return null;
    }

    private static LogSeverity ParseSeverity(string level) => level.ToUpperInvariant() switch
    {
        "TRACE" or "VERBOSE" => LogSeverity.Trace,
        "DEBUG" or "DBG" => LogSeverity.Debug,
        "INFO" or "INFORMATION" or "INF" => LogSeverity.Information,
        "WARN" or "WARNING" or "WRN" => LogSeverity.Warning,
        "ERROR" or "ERR" => LogSeverity.Error,
        "FATAL" or "CRITICAL" or "FTL" => LogSeverity.Fatal,
        _ => LogSeverity.Information,
    };
}

/// <summary>Represents a parsed structured (JSON) log entry.</summary>
public class StructuredLogEntry
{
    public string Level { get; set; } = "Unknown";
    public string Message { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public string RawJson { get; set; } = "";
    public JsonElement Properties { get; set; }
}

/// <summary>Log severity levels for structured log assertions.</summary>
public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
}

/// <summary>Exception thrown when log assertions fail.</summary>
public class LogAssertionException : Exception
{
    public LogAssertionException(string message) : base(message) { }
}

/// <summary>
/// Extension methods for log assertions on <see cref="TestEnvironment"/>.
/// </summary>
public static class LogAssertionExtensions
{
    /// <summary>
    /// Creates a log assertion builder for the specified application's container logs.
    /// </summary>
    public static async Task<LogAssertionBuilder> AssertLogsAsync(this TestEnvironment env, string appName)
    {
        var logs = await env.GetLogsAsync(appName);
        return new LogAssertionBuilder(logs, appName);
    }

    /// <summary>
    /// Polls container logs until a line containing the specified text appears.
    /// Useful for waiting for startup completion or processing events.
    /// </summary>
    public static async Task WaitForLogLineAsync(
        this TestEnvironment env,
        string appName,
        string text,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            var logs = await env.GetLogsAsync(appName);
            if (logs.Contains(text, StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Log line containing '{text}' not found for '{appName}' within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s.");
    }
}
