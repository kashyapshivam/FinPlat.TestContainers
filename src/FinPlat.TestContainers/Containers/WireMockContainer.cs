using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace FinPlat.TestContainers.Containers;

/// <summary>
/// Defines a WireMock stub mapping.
/// </summary>
public class StubDefinition
{
    /// <summary>HTTP method to match (GET, POST, or ANY).</summary>
    public string Method { get; set; } = "ANY";

    /// <summary>URL path to match.</summary>
    public string Path { get; set; } = "/";

    /// <summary>When true, Path is treated as a regex pattern (urlPathPattern).</summary>
    public bool IsPathPattern { get; set; }

    /// <summary>HTTP status code to return.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Response body to return.</summary>
    public string ResponseBody { get; set; } = "{}";

    /// <summary>Priority for stub matching (lower = higher priority). Default is 0 (highest).</summary>
    public int? Priority { get; set; }

    /// <summary>When set, the stub only matches requests whose body contains this substring.</summary>
    public string? BodyContains { get; set; }
}

/// <summary>
/// Manages a WireMock container for HTTP API mocking during integration tests.
/// Configures stubs via the WireMock Admin REST API.
/// </summary>
public class ManagedWireMockContainer : IAsyncDisposable
{
    private IContainer? _container;
    private readonly HttpClient _httpClient = new();
    private const int WireMockPort = 8080;

    /// <summary>
    /// Gets the container name used as the Docker network alias.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the external URL for accessing the WireMock API from the test host.
    /// </summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the internal URL for app containers on the same Docker network.
    /// </summary>
    public string InternalUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Creates a new managed WireMock container with the specified name.
    /// </summary>
    /// <param name="name">The name/alias for this mock API container.</param>
    public ManagedWireMockContainer(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Starts the WireMock container and attaches it to the specified Docker network.
    /// </summary>
    /// <param name="network">The shared Docker network for container-to-container communication.</param>
    public async Task StartAsync(INetwork network)
    {
        var networkAlias = $"mock-{Name}";

        _container = new ContainerBuilder()
            .WithImage("wiremock/wiremock:latest")
            .WithNetwork(network)
            .WithNetworkAliases(networkAlias)
            .WithPortBinding(WireMockPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(WireMockPort))
            .Build();

        await _container.StartAsync();

        var mappedPort = _container.GetMappedPublicPort(WireMockPort);
        var host = _container.Hostname;

        Url = $"http://{host}:{mappedPort}";
        InternalUrl = $"http://{networkAlias}:{WireMockPort}";
    }

    /// <summary>
    /// Configures a stub mapping in the running WireMock container via the Admin API.
    /// </summary>
    /// <param name="stub">The stub definition to register.</param>
    public async Task ConfigureStubAsync(StubDefinition stub)
    {
        var mapping = BuildMappingJson(stub);
        var content = new StringContent(mapping, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{Url}/__admin/mappings", content);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets the number of times a specific path has been called.
    /// </summary>
    /// <param name="path">The URL path to check (e.g., "/api/data").</param>
    /// <returns>The number of matched requests.</returns>
    public async Task<int> GetCallCountAsync(string path)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            urlPathPattern = $".*{EscapeRegex(path)}.*"
        });

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{Url}/__admin/requests/count", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>
    /// Gets all captured requests that matched the specified path.
    /// Returns full request details including method, URL, body, and headers.
    /// </summary>
    /// <param name="path">The URL path to find requests for.</param>
    /// <returns>Array of captured requests with body and headers.</returns>
    public async Task<CapturedRequest[]> GetRequestsAsync(string path)
    {
        var requestBody = JsonSerializer.Serialize(new { urlPathPattern = $".*{EscapeRegex(path)}.*" });
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{Url}/__admin/requests/find", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);

        var requests = doc.RootElement.GetProperty("requests");
        var result = new System.Collections.Generic.List<CapturedRequest>();

        foreach (var req in requests.EnumerateArray())
        {
            var captured = new CapturedRequest
            {
                Method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "",
                Url = req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Body = req.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""
            };

            if (req.TryGetProperty("headers", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                {
                    // WireMock returns headers as { "Name": { "values": ["v1"] } } or as strings
                    if (header.Value.ValueKind == JsonValueKind.Object &&
                        header.Value.TryGetProperty("values", out var vals) &&
                        vals.GetArrayLength() > 0)
                    {
                        captured.Headers[header.Name] = vals[0].GetString() ?? "";
                    }
                    else if (header.Value.ValueKind == JsonValueKind.String)
                    {
                        captured.Headers[header.Name] = header.Value.GetString() ?? "";
                    }
                }
            }

            result.Add(captured);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Clears the WireMock request journal so subsequent queries only see new requests.
    /// Call this between tests to isolate request assertions.
    /// </summary>
    public async Task ResetRequestLogAsync()
    {
        var response = await _httpClient.DeleteAsync($"{Url}/__admin/requests");
        response.EnsureSuccessStatusCode();
    }

    private static string EscapeRegex(string input)
    {
        return System.Text.RegularExpressions.Regex.Escape(input);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    private static string BuildMappingJson(StubDefinition stub)
    {
        // Build the request matcher as a dictionary for flexibility
        var request = new Dictionary<string, object>();

        if (stub.IsPathPattern)
        {
            request["urlPathPattern"] = stub.Path;
        }
        else
        {
            request["urlPath"] = stub.Path;
        }

        if (stub.Method != "ANY")
        {
            request["method"] = stub.Method;
        }

        if (!string.IsNullOrEmpty(stub.BodyContains))
        {
            request["bodyPatterns"] = new[] { new { contains = stub.BodyContains } };
        }

        var response = new
        {
            status = stub.StatusCode,
            body = stub.ResponseBody,
            headers = new { Content_Type = "application/json" }
        };

        string json;
        if (stub.Priority.HasValue)
        {
            var mapping = new { priority = stub.Priority.Value, request, response };
            json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        else
        {
            var mapping = new { request, response };
            json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        return json;
    }
}
