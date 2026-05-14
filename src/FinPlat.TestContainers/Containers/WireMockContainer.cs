using System;
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

    /// <summary>HTTP status code to return.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Response body to return.</summary>
    public string ResponseBody { get; set; } = "{}";
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
            url = path
        });

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{Url}/__admin/requests/count", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("count").GetInt32();
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
        object request;
        if (stub.Method == "ANY")
        {
            request = new { urlPath = stub.Path };
        }
        else
        {
            request = new { method = stub.Method, urlPath = stub.Path };
        }

        var mapping = new
        {
            request,
            response = new
            {
                status = stub.StatusCode,
                body = stub.ResponseBody,
                headers = new { Content_Type = "application/json" }
            }
        };

        return JsonSerializer.Serialize(mapping, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
