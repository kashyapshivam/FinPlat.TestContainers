using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Fluent;

/// <summary>
/// Fluent builder for configuring mock API behaviors.
/// Provides a Moq-like API for setting up HTTP mock responses.
/// </summary>
public class MockSetupBuilder
{
    private readonly ManagedWireMockContainer _container;
    private readonly List<MockSetupRule> _rules = new();

    internal MockSetupBuilder(ManagedWireMockContainer container)
    {
        _container = container;
    }

    /// <summary>
    /// Sets up a mock for POST requests to the specified path.
    /// </summary>
    public MockSetupRule OnPost(string path) => CreateRule("POST", path);

    /// <summary>
    /// Sets up a mock for GET requests to the specified path.
    /// </summary>
    public MockSetupRule OnGet(string path) => CreateRule("GET", path);

    /// <summary>
    /// Sets up a mock for PUT requests to the specified path.
    /// </summary>
    public MockSetupRule OnPut(string path) => CreateRule("PUT", path);

    /// <summary>
    /// Sets up a mock for DELETE requests to the specified path.
    /// </summary>
    public MockSetupRule OnDelete(string path) => CreateRule("DELETE", path);

    /// <summary>
    /// Sets up a mock for any HTTP method to the specified path.
    /// </summary>
    public MockSetupRule OnAny(string path) => CreateRule("ANY", path);

    private MockSetupRule CreateRule(string method, string path)
    {
        var rule = new MockSetupRule(method, path, this);
        _rules.Add(rule);
        return rule;
    }

    /// <summary>
    /// Applies all configured rules to the WireMock container.
    /// Called internally after all setup is complete.
    /// </summary>
    internal async Task ApplyAsync()
    {
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            await rule.ApplyToContainerAsync(_container, i + 1);
        }
    }

    /// <summary>
    /// Gets all configured rules (for verification purposes).
    /// </summary>
    internal IReadOnlyList<MockSetupRule> Rules => _rules;
}

/// <summary>
/// Represents a single mock setup rule with fluent configuration.
/// </summary>
public class MockSetupRule
{
    private readonly string _method;
    private readonly string _path;
    private readonly MockSetupBuilder _parent;
    private int _statusCode = 200;
    private string _responseBody = "{}";
    private string? _bodyContains;
    private List<(int statusCode, string body)>? _sequence;
    private bool _isPathPattern;
    private bool _isVerifiable;

    internal MockSetupRule(string method, string path, MockSetupBuilder parent)
    {
        _method = method;
        _path = path;
        _parent = parent;
        _isPathPattern = path.Contains(".*") || path.Contains("{") || path.Contains("[");
    }

    /// <summary>HTTP method for this rule.</summary>
    public string Method => _method;

    /// <summary>URL path for this rule.</summary>
    public string Path => _path;

    /// <summary>Whether this rule is marked verifiable.</summary>
    public bool IsVerifiable => _isVerifiable;

    /// <summary>
    /// Sets the response status code and body.
    /// </summary>
    public MockSetupRule Returns(int statusCode, object? body = null)
    {
        _statusCode = statusCode;
        _responseBody = body is string s ? s : JsonSerializer.Serialize(body ?? new object());
        return this;
    }

    /// <summary>
    /// Sets a condition: this rule only matches when the request body contains the specified string.
    /// </summary>
    public MockSetupRule When(string bodyContains)
    {
        _bodyContains = bodyContains;
        return this;
    }

    /// <summary>
    /// Returns different responses for sequential calls to this endpoint.
    /// Uses WireMock scenario-based sequencing.
    /// </summary>
    public MockSetupRule ReturnsSequence(params (int statusCode, object? body)[] responses)
    {
        _sequence = new List<(int, string)>();
        foreach (var (code, body) in responses)
        {
            var bodyStr = body is string s ? s : JsonSerializer.Serialize(body ?? new object());
            _sequence.Add((code, bodyStr));
        }
        return this;
    }

    /// <summary>
    /// Treats the path as a regex pattern for flexible matching.
    /// </summary>
    public MockSetupRule AsPathPattern()
    {
        _isPathPattern = true;
        return this;
    }

    /// <summary>
    /// Marks this rule as verifiable — VerifyAll() will check it was called.
    /// </summary>
    public MockSetupRule Verifiable()
    {
        _isVerifiable = true;
        return this;
    }

    /// <summary>
    /// Applies this rule to the WireMock container.
    /// </summary>
    internal async Task ApplyToContainerAsync(ManagedWireMockContainer container, int priority)
    {
        if (_sequence != null && _sequence.Count > 0)
        {
            var scenarioName = $"seq-{Guid.NewGuid():N}".Substring(0, 30);
            for (int i = 0; i < _sequence.Count; i++)
            {
                var (code, body) = _sequence[i];
                var stub = new StubDefinition
                {
                    Method = _method,
                    Path = _path,
                    IsPathPattern = _isPathPattern,
                    StatusCode = code,
                    ResponseBody = body,
                    Priority = priority,
                    BodyContains = _bodyContains
                };
                await container.ConfigureScenarioStubAsync(stub, scenarioName,
                    requiredState: i == 0 ? "Started" : $"step-{i}",
                    newState: i < _sequence.Count - 1 ? $"step-{i + 1}" : null);
            }
        }
        else
        {
            var stub = new StubDefinition
            {
                Method = _method,
                Path = _path,
                IsPathPattern = _isPathPattern,
                StatusCode = _statusCode,
                ResponseBody = _responseBody,
                Priority = priority,
                BodyContains = _bodyContains
            };
            await container.ConfigureStubAsync(stub);
        }
    }
}
