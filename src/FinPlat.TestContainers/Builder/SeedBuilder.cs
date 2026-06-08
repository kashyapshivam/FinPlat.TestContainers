using System;
using System.Collections.Generic;
using System.Text;
using FinPlat.TestContainers.Config;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Fluent builder for declarative seed steps executed after the environment is healthy.
/// Use <see cref="TestEnvironmentBuilder.Seed"/> to attach a seed to a build, or
/// <see cref="TestEnvironment.SeedAsync"/> to run a seed at any time after build.
/// </summary>
/// <remarks>
/// Steps run in declaration order. Each step is wrapped with a descriptive label
/// when it fails, so prefer giving each step a short <c>name</c> for easier diagnosis.
/// </remarks>
public class SeedBuilder
{
    internal SeedConfig Config { get; } = new();

    /// <summary>
    /// Sets the root directory used to resolve relative fixture paths. Defaults to
    /// the test output directory (AppContext.BaseDirectory).
    /// </summary>
    public SeedBuilder WithFixtureRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Fixture root must be a non-empty path.", nameof(path));
        Config.FixtureRoot = path;
        return this;
    }

    /// <summary>
    /// Seeds an Azurite table from a JSON fixture. The fixture must be a JSON array
    /// of objects, each containing string "PartitionKey" and "RowKey" properties.
    /// </summary>
    /// <param name="tableName">The Azurite table to seed (should be pre-created via wiring).</param>
    /// <param name="fixturePath">Path to the JSON fixture (relative to fixture root, or absolute).</param>
    /// <param name="name">Optional friendly label used in error messages.</param>
    public SeedBuilder Table(string tableName, string fixturePath, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name must be provided.", nameof(tableName));
        if (string.IsNullOrWhiteSpace(fixturePath))
            throw new ArgumentException("Fixture path must be provided.", nameof(fixturePath));
        Config.Steps.Add(new TableSeed(tableName, fixturePath, name));
        return this;
    }

    /// <summary>
    /// Uploads a single blob into an Azurite container from a file on disk.
    /// Overwrites any existing blob with the same name.
    /// </summary>
    public SeedBuilder Blob(string containerName, string blobName, string fixturePath, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name must be provided.", nameof(containerName));
        if (string.IsNullOrWhiteSpace(blobName))
            throw new ArgumentException("Blob name must be provided.", nameof(blobName));
        if (string.IsNullOrWhiteSpace(fixturePath))
            throw new ArgumentException("Fixture path must be provided.", nameof(fixturePath));
        Config.Steps.Add(new BlobSeed(containerName, blobName, fixturePath, name));
        return this;
    }

    /// <summary>
    /// POSTs an inline JSON payload to a registered application container.
    /// </summary>
    /// <param name="targetApp">Name of the application (must match AddApplication).</param>
    /// <param name="path">URL path (e.g. "/v1/events/bigcatproduct-v3"). Leading slash optional.</param>
    /// <param name="jsonBody">JSON request body.</param>
    /// <param name="name">Optional friendly label.</param>
    public SeedBuilder HttpPost(string targetApp, string path, string jsonBody, string? name = null)
        => HttpPost(targetApp, path, jsonBody, "application/json", _ => { }, name);

    /// <summary>
    /// POSTs an inline payload to a registered application container with full configuration.
    /// </summary>
    public SeedBuilder HttpPost(
        string targetApp,
        string path,
        string body,
        string contentType,
        Action<HttpSeedOptions> configure,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(targetApp))
            throw new ArgumentException("Target app must be provided.", nameof(targetApp));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be provided.", nameof(path));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        var opts = new HttpSeedOptions { ContentType = contentType };
        configure(opts);

        Config.Steps.Add(new HttpSeed(
            TargetApp: targetApp,
            Path: NormalizePath(path),
            InlineBody: Encoding.UTF8.GetBytes(body),
            FixturePath: null,
            ContentType: opts.ContentType,
            RequestHeaders: opts.Headers.Count == 0 ? null : new Dictionary<string, string>(opts.Headers),
            RetryAttempts: opts.RetryAttempts,
            RetryDelay: opts.RetryDelay,
            Readiness: opts.Readiness,
            Name: name ?? opts.Name));
        return this;
    }

    /// <summary>
    /// POSTs a payload read from disk to a registered application container.
    /// The file is read at execution time (not at builder time), so dynamically
    /// generated fixtures are supported.
    /// </summary>
    /// <param name="targetApp">Name of the application (must match AddApplication).</param>
    /// <param name="path">URL path. Leading slash optional.</param>
    /// <param name="fixturePath">Path to the fixture file (relative to fixture root, or absolute).</param>
    /// <param name="name">Optional friendly label.</param>
    public SeedBuilder HttpPostFile(string targetApp, string path, string fixturePath, string? name = null)
        => HttpPostFile(targetApp, path, fixturePath, _ => { }, name);

    /// <summary>
    /// POSTs a payload read from disk with full configuration (headers, retries, readiness).
    /// </summary>
    public SeedBuilder HttpPostFile(
        string targetApp,
        string path,
        string fixturePath,
        Action<HttpSeedOptions> configure,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(targetApp))
            throw new ArgumentException("Target app must be provided.", nameof(targetApp));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be provided.", nameof(path));
        if (string.IsNullOrWhiteSpace(fixturePath))
            throw new ArgumentException("Fixture path must be provided.", nameof(fixturePath));

        var opts = new HttpSeedOptions();
        configure(opts);

        Config.Steps.Add(new HttpSeed(
            TargetApp: targetApp,
            Path: NormalizePath(path),
            InlineBody: null,
            FixturePath: fixturePath,
            ContentType: opts.ContentType,
            RequestHeaders: opts.Headers.Count == 0 ? null : new Dictionary<string, string>(opts.Headers),
            RetryAttempts: opts.RetryAttempts,
            RetryDelay: opts.RetryDelay,
            Readiness: opts.Readiness,
            Name: name ?? opts.Name));
        return this;
    }

    /// <summary>
    /// Blocks until a specific row (PartitionKey + RowKey) appears in an Azurite table.
    /// Use this immediately after an <see cref="HttpPost"/> to assert the receiver
    /// actually flushed to storage. A 2xx POST does NOT prove persistence.
    /// </summary>
    public SeedBuilder WaitForTableRow(
        string tableName,
        string partitionKey,
        string rowKey,
        TimeSpan? timeout = null,
        TimeSpan? pollDelay = null,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name must be provided.", nameof(tableName));
        if (string.IsNullOrWhiteSpace(partitionKey))
            throw new ArgumentException("PartitionKey must be provided.", nameof(partitionKey));
        if (string.IsNullOrWhiteSpace(rowKey))
            throw new ArgumentException("RowKey must be provided.", nameof(rowKey));
        Config.Steps.Add(new TableWaitSeed(
            TableName: tableName,
            PartitionKey: partitionKey,
            RowKey: rowKey,
            Filter: null,
            MinMatchingRows: 1,
            Timeout: timeout ?? TimeSpan.FromSeconds(30),
            PollDelay: pollDelay ?? TimeSpan.FromMilliseconds(250),
            Name: name));
        return this;
    }

    /// <summary>
    /// Blocks until an OData filter query against an Azurite table returns at least
    /// <paramref name="minMatchingRows"/> rows. Use this when you don't know the
    /// exact (PartitionKey, RowKey) ahead of time (e.g. Collector hashes the keys).
    /// Example filter: <c>"BillingGroupId eq 'bg-001'"</c>.
    /// </summary>
    public SeedBuilder WaitForTableQuery(
        string tableName,
        string filter,
        int minMatchingRows = 1,
        TimeSpan? timeout = null,
        TimeSpan? pollDelay = null,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name must be provided.", nameof(tableName));
        if (string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException("Filter must be provided.", nameof(filter));
        if (minMatchingRows < 1)
            throw new ArgumentOutOfRangeException(nameof(minMatchingRows), "Must be >= 1.");
        Config.Steps.Add(new TableWaitSeed(
            TableName: tableName,
            PartitionKey: null,
            RowKey: null,
            Filter: filter,
            MinMatchingRows: minMatchingRows,
            Timeout: timeout ?? TimeSpan.FromSeconds(30),
            PollDelay: pollDelay ?? TimeSpan.FromMilliseconds(250),
            Name: name));
        return this;
    }

    private static string NormalizePath(string path)
        => path.StartsWith('/') ? path : "/" + path;
}

/// <summary>
/// Optional configuration for an <see cref="HttpSeed"/> step.
/// </summary>
public class HttpSeedOptions
{
    /// <summary>Request Content-Type header. Defaults to "application/json".</summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>Custom request headers (in addition to Content-Type).</summary>
    public Dictionary<string, string> Headers { get; } = new();

    /// <summary>How many times to retry on 5xx / network error. Defaults to 0
    /// (no retry) to avoid duplicate ingestion if a request times out after the
    /// server has already processed it. Combine with <see cref="WaitUntilHttpGet"/>
    /// to handle startup races without retries.</summary>
    public int RetryAttempts { get; set; } = 0;

    /// <summary>Delay between retries. Defaults to 500ms.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Optional readiness probe (e.g. wait for /ping to return 200 before posting).</summary>
    public SeedReadinessCheck? Readiness { get; private set; }

    /// <summary>Optional friendly label set via the options bag (used if the outer name is null).</summary>
    public string? Name { get; set; }

    /// <summary>Adds a custom request header.</summary>
    public HttpSeedOptions WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    /// <summary>Configures retry behavior on transient HTTP failures.</summary>
    public HttpSeedOptions WithRetries(int attempts, TimeSpan delay)
    {
        if (attempts < 0) throw new ArgumentOutOfRangeException(nameof(attempts), "Must be >= 0.");
        RetryAttempts = attempts;
        RetryDelay = delay;
        return this;
    }

    /// <summary>
    /// Polls the given app's HTTP GET endpoint until it returns the expected status
    /// (default 200) before sending the seed payload. Use this to make sure the
    /// target ingest API is fully bound to its port, especially for slow-starting services.
    /// </summary>
    public HttpSeedOptions WaitUntilHttpGet(
        string targetApp,
        string path,
        int expectedStatus = 200,
        int maxAttempts = 30,
        int pollDelayMs = 250)
    {
        if (string.IsNullOrWhiteSpace(targetApp))
            throw new ArgumentException("Target app must be provided.", nameof(targetApp));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be provided.", nameof(path));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        Readiness = new SeedReadinessCheck(targetApp, path, expectedStatus, maxAttempts, TimeSpan.FromMilliseconds(pollDelayMs));
        return this;
    }
}
