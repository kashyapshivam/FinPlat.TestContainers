using System.Collections.Generic;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Configures stub definitions for a WireMock-based mock API container.
/// </summary>
public class MockApiBuilder
{
    internal List<StubDefinition> Stubs { get; } = new();

    /// <summary>
    /// Registers a GET stub that responds with the specified status code and body.
    /// </summary>
    /// <param name="path">URL path to match (e.g., "/api/data").</param>
    /// <param name="statusCode">HTTP response status code.</param>
    /// <param name="body">Response body as a string.</param>
    public MockApiBuilder OnGet(string path, int statusCode = 200, string body = "{}")
    {
        Stubs.Add(new StubDefinition
        {
            Method = "GET",
            Path = path,
            StatusCode = statusCode,
            ResponseBody = body
        });
        return this;
    }

    /// <summary>
    /// Registers a POST stub that responds with the specified status code and body.
    /// </summary>
    /// <param name="path">URL path to match (e.g., "/api/submit").</param>
    /// <param name="statusCode">HTTP response status code.</param>
    /// <param name="body">Response body as a string.</param>
    public MockApiBuilder OnPost(string path, int statusCode = 200, string body = "{}")
    {
        Stubs.Add(new StubDefinition
        {
            Method = "POST",
            Path = path,
            StatusCode = statusCode,
            ResponseBody = body
        });
        return this;
    }

    /// <summary>
    /// Registers a stub that matches any HTTP method for the specified path.
    /// </summary>
    /// <param name="path">URL path to match.</param>
    /// <param name="statusCode">HTTP response status code.</param>
    /// <param name="body">Response body as a string.</param>
    public MockApiBuilder OnAny(string path, int statusCode = 200, string body = "{}")
    {
        Stubs.Add(new StubDefinition
        {
            Method = "ANY",
            Path = path,
            StatusCode = statusCode,
            ResponseBody = body
        });
        return this;
    }
}
