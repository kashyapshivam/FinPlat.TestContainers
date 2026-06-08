using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinPlat.TestContainers.Fixtures;

/// <summary>
/// Utilities for loading and transforming fixture files (JSON scenarios).
/// Supports variable substitution in JSON templates using {{variable}} syntax.
/// </summary>
public static class FixtureFile
{
    /// <summary>
    /// Loads a JSON file and returns its content as a string.
    /// </summary>
    /// <param name="path">Path to the JSON file.</param>
    public static string LoadJson(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixture file not found: {path}", path);

        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// Loads a JSON file, applies variable substitution, and returns the result.
    /// Variables use the {{variableName}} syntax.
    /// </summary>
    /// <param name="path">Path to the JSON template file.</param>
    /// <param name="variables">Dictionary of variable names to values.</param>
    public static string LoadJson(string path, IDictionary<string, string> variables)
    {
        var json = LoadJson(path);
        return ReplaceVariables(json, variables);
    }

    /// <summary>
    /// Loads a JSON file and returns it as a base64-encoded string.
    /// Useful for queue messages that expect base64 encoding.
    /// </summary>
    public static string LoadBase64(string path)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(LoadJson(path)));

    /// <summary>
    /// Loads a JSON template file, applies variable substitution, and returns base64.
    /// </summary>
    public static string LoadBase64(string path, IDictionary<string, string> variables)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(LoadJson(path, variables)));

    /// <summary>
    /// Deserializes a JSON fixture file into a typed object.
    /// </summary>
    public static T Load<T>(string path, JsonSerializerOptions? options = null)
    {
        var json = LoadJson(path);
        return JsonSerializer.Deserialize<T>(json, options ?? DefaultOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture file '{path}' to {typeof(T).Name}.");
    }

    /// <summary>
    /// Deserializes a JSON template file with variable substitution into a typed object.
    /// </summary>
    public static T Load<T>(string path, IDictionary<string, string> variables, JsonSerializerOptions? options = null)
    {
        var json = LoadJson(path, variables);
        return JsonSerializer.Deserialize<T>(json, options ?? DefaultOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture file '{path}' to {typeof(T).Name}.");
    }

    /// <summary>
    /// Replaces {{variableName}} placeholders in a string with values from the dictionary.
    /// Throws if any placeholder has no matching variable.
    /// </summary>
    public static string ReplaceVariables(string template, IDictionary<string, string> variables)
    {
        return Regex.Replace(template, @"\{\{(\w+)\}\}", match =>
        {
            var varName = match.Groups[1].Value;
            if (variables.TryGetValue(varName, out var value))
                return value;

            throw new InvalidOperationException(
                $"Variable '{{{{{varName}}}}}' in template has no value in the variables dictionary.");
        });
    }

    /// <summary>
    /// Scans a template string and returns all {{variableName}} placeholders found.
    /// Useful for validation before substitution.
    /// </summary>
    public static IReadOnlyList<string> GetVariables(string template)
    {
        var names = new List<string>();
        foreach (Match match in Regex.Matches(template, @"\{\{(\w+)\}\}"))
        {
            var name = match.Groups[1].Value;
            if (!names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
