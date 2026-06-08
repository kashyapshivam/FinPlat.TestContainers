using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Internal executor that runs a <see cref="SeedConfig"/> against a running test environment.
/// Used both by <see cref="TestEnvironmentBuilder.BuildAsync"/> (post-startup) and
/// <see cref="TestEnvironment.SeedAsync"/> (per-test).
/// </summary>
internal static class SeedExecutor
{
    public static async Task ExecuteAsync(
        SeedConfig config,
        ManagedAzuriteContainer? azurite,
        IReadOnlyDictionary<string, ManagedAppContainer> appContainers,
        IReadOnlyDictionary<string, int> appHttpPorts,
        bool bypassSsl,
        TextWriter logger)
    {
        for (int i = 0; i < config.Steps.Count; i++)
        {
            var step = config.Steps[i];
            var label = $"seed[{i}] {step.Kind}{(step.Name is null ? "" : $" '{step.Name}'")}";
            logger.WriteLine($"[Seed] -> {label}");

            try
            {
                switch (step)
                {
                    case TableSeed table:
                        await ExecuteTableSeedAsync(table, azurite, config.FixtureRoot, bypassSsl);
                        break;
                    case BlobSeed blob:
                        await ExecuteBlobSeedAsync(blob, azurite, config.FixtureRoot, bypassSsl);
                        break;
                    case HttpSeed http:
                        await ExecuteHttpSeedAsync(http, appContainers, appHttpPorts, config.FixtureRoot, logger);
                        break;
                    case TableWaitSeed wait:
                        await ExecuteTableWaitAsync(wait, azurite, appContainers, bypassSsl, logger);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown seed step type: {step.GetType().Name}");
                }
            }
            catch (Exception ex) when (ex is not SeedExecutionException)
            {
                throw new SeedExecutionException(
                    $"Seed step failed: {label}. See inner exception for details.", ex);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table seed
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ExecuteTableSeedAsync(
        TableSeed step,
        ManagedAzuriteContainer? azurite,
        string fixtureRoot,
        bool bypassSsl)
    {
        if (azurite is null)
            throw new InvalidOperationException(
                $"TableSeed '{step.TableName}' requires Azurite, but AddAzurite() was not called.");

        var fixturePath = ResolveFixturePath(fixtureRoot, step.FixturePath);
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException($"Table fixture not found: {fixturePath}", fixturePath);

        var options = new TableClientOptions();
        if (bypassSsl) ConfigureSslBypass(options);
        var serviceClient = new TableServiceClient(azurite.ConnectionString, options);
        // Ensure table exists (idempotent) — supports seeding into ad-hoc tables not declared in wiring.
        await serviceClient.CreateTableIfNotExistsAsync(step.TableName);
        var client = new TableClient(azurite.ConnectionString, step.TableName, options);

        await using var stream = File.OpenRead(fixturePath);
        using var doc = await JsonDocument.ParseAsync(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"Table fixture must be a JSON array of objects (was {doc.RootElement.ValueKind}): {fixturePath}");

        int rowIndex = 0;
        foreach (var rowElement in doc.RootElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"Row {rowIndex} in {fixturePath} is not an object (was {rowElement.ValueKind}).");

            var entity = BuildTableEntity(rowElement, fixturePath, rowIndex);
            await client.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            rowIndex++;
        }
    }

    private static TableEntity BuildTableEntity(JsonElement row, string fixturePath, int rowIndex)
    {
        // First pass: collect @type hints
        var typeHints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in row.EnumerateObject())
        {
            if (prop.Name.EndsWith("@type", StringComparison.Ordinal))
            {
                var baseName = prop.Name[..^"@type".Length];
                typeHints[baseName] = prop.Value.GetString()
                    ?? throw new InvalidDataException(
                        $"Row {rowIndex} in {fixturePath}: @type hint for '{baseName}' is not a string.");
            }
        }

        string? partitionKey = null;
        string? rowKey = null;
        var properties = new Dictionary<string, object>();

        foreach (var prop in row.EnumerateObject())
        {
            if (prop.Name.EndsWith("@type", StringComparison.Ordinal))
                continue;

            if (prop.Name == "PartitionKey")
            {
                partitionKey = prop.Value.GetString();
                continue;
            }
            if (prop.Name == "RowKey")
            {
                rowKey = prop.Value.GetString();
                continue;
            }

            typeHints.TryGetValue(prop.Name, out var typeHint);
            var value = CoerceJsonValue(prop.Value, typeHint, fixturePath, rowIndex, prop.Name);
            if (value is not null)
                properties[prop.Name] = value;
        }

        if (string.IsNullOrEmpty(partitionKey) || string.IsNullOrEmpty(rowKey))
            throw new InvalidDataException(
                $"Row {rowIndex} in {fixturePath} is missing PartitionKey or RowKey.");

        var entity = new TableEntity(partitionKey, rowKey);
        foreach (var (k, v) in properties)
            entity[k] = v;
        return entity;
    }

    private static object? CoerceJsonValue(
        JsonElement value, string? typeHint, string fixturePath, int rowIndex, string propertyName)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;

        if (typeHint is not null)
        {
            try
            {
                return typeHint switch
                {
                    "DateTime" or "Edm.DateTime" => ParseDateTimeOffset(value.GetString()!),
                    "Guid" or "Edm.Guid" => Guid.Parse(value.GetString()!),
                    "Long" or "Int64" or "Edm.Int64" => value.ValueKind == JsonValueKind.Number
                        ? value.GetInt64()
                        : long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    "Int" or "Int32" or "Edm.Int32" => value.ValueKind == JsonValueKind.Number
                        ? value.GetInt32()
                        : int.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    "Double" or "Edm.Double" => value.ValueKind == JsonValueKind.Number
                        ? value.GetDouble()
                        : double.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    "Bool" or "Boolean" or "Edm.Boolean" => value.GetBoolean(),
                    "Binary" or "Edm.Binary" => Convert.FromBase64String(value.GetString()!),
                    "String" or "Edm.String" => value.GetString()!,
                    _ => throw new InvalidDataException(
                        $"Unknown @type '{typeHint}' for property '{propertyName}' at row {rowIndex} of {fixturePath}.")
                };
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException(
                    $"Could not coerce property '{propertyName}' to @type '{typeHint}' " +
                    $"at row {rowIndex} of {fixturePath}: {ex.Message}", ex);
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number => value.GetDouble(),
            _ => throw new InvalidDataException(
                $"Property '{propertyName}' at row {rowIndex} of {fixturePath} has unsupported JSON kind " +
                $"{value.ValueKind}. Use a @type hint or flatten the value.")
        };
    }

    private static DateTimeOffset ParseDateTimeOffset(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    // ─────────────────────────────────────────────────────────────────────────
    // Blob seed
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ExecuteBlobSeedAsync(
        BlobSeed step,
        ManagedAzuriteContainer? azurite,
        string fixtureRoot,
        bool bypassSsl)
    {
        if (azurite is null)
            throw new InvalidOperationException(
                $"BlobSeed '{step.ContainerName}/{step.BlobName}' requires Azurite, but AddAzurite() was not called.");

        var fixturePath = ResolveFixturePath(fixtureRoot, step.FixturePath);
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException($"Blob fixture not found: {fixturePath}", fixturePath);

        var options = new BlobClientOptions();
        if (bypassSsl) ConfigureSslBypass(options);
        var container = new BlobContainerClient(azurite.ConnectionString, step.ContainerName, options);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient(step.BlobName);

        await using var stream = File.OpenRead(fixturePath);
        await blob.UploadAsync(stream, overwrite: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTTP seed
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ExecuteHttpSeedAsync(
        HttpSeed step,
        IReadOnlyDictionary<string, ManagedAppContainer> appContainers,
        IReadOnlyDictionary<string, int> appHttpPorts,
        string fixtureRoot,
        TextWriter logger)
    {
        if (!appContainers.TryGetValue(step.TargetApp, out var targetContainer))
            throw new InvalidOperationException(
                $"HttpSeed target app '{step.TargetApp}' was not registered via AddApplication().");
        if (!appHttpPorts.TryGetValue(step.TargetApp, out var internalPort) || internalPort <= 0)
            throw new InvalidOperationException(
                $"HttpSeed target app '{step.TargetApp}' has no internal port. " +
                "Call WithInternalPort(...) or WithPort(...) on its ApplicationBuilder.");

        var hostPort = targetContainer.GetMappedPort(internalPort);
        var baseUrl = $"http://localhost:{hostPort}";
        var url = $"{baseUrl}{step.Path}";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Readiness probe
        if (step.Readiness is { } readiness)
        {
            await WaitForReadinessAsync(readiness, appContainers, appHttpPorts, httpClient, logger);
        }

        // Resolve body
        byte[] bodyBytes;
        if (step.InlineBody is not null)
        {
            bodyBytes = step.InlineBody;
        }
        else if (step.FixturePath is not null)
        {
            var fixturePath = ResolveFixturePath(fixtureRoot, step.FixturePath);
            if (!File.Exists(fixturePath))
                throw new FileNotFoundException($"HTTP fixture not found: {fixturePath}", fixturePath);
            bodyBytes = await File.ReadAllBytesAsync(fixturePath);
        }
        else
        {
            throw new InvalidOperationException(
                "HttpSeed has neither InlineBody nor FixturePath — this should be impossible. Builder bug?");
        }

        // POST with retry
        int attempts = Math.Max(1, step.RetryAttempts + 1);
        Exception? lastError = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(bodyBytes)
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(step.ContentType);

                if (step.RequestHeaders is not null)
                {
                    foreach (var (name, value) in step.RequestHeaders)
                    {
                        // Headers like Authorization belong on the request, not the content
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                using var response = await httpClient.SendAsync(request);
                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                {
                    logger.WriteLine($"[Seed]    POST {url} -> {(int)response.StatusCode}");
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                lastError = new HttpRequestException(
                    $"POST {url} returned {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    $"Body (first 500 chars): {Truncate(responseBody, 500)}");

                // Only retry on 5xx
                if ((int)response.StatusCode < 500)
                    break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            if (attempt < attempts)
            {
                logger.WriteLine($"[Seed]    POST {url} attempt {attempt}/{attempts} failed: {lastError?.Message}. Retrying in {step.RetryDelay.TotalMilliseconds}ms.");
                await Task.Delay(step.RetryDelay);
            }
        }

        // Pull target container logs for diagnostics
        string targetLogs = "";
        try { targetLogs = await targetContainer.GetLogsAsync(); }
        catch { /* best effort */ }

        throw new SeedExecutionException(
            $"HttpSeed POST {url} failed after {attempts} attempt(s). " +
            $"Last error: {lastError?.Message}\n" +
            $"--- {step.TargetApp} container logs (tail) ---\n{Tail(targetLogs, 3000)}",
            lastError);
    }

    private static async Task WaitForReadinessAsync(
        SeedReadinessCheck readiness,
        IReadOnlyDictionary<string, ManagedAppContainer> appContainers,
        IReadOnlyDictionary<string, int> appHttpPorts,
        HttpClient httpClient,
        TextWriter logger)
    {
        if (!appContainers.TryGetValue(readiness.TargetApp, out var container))
            throw new InvalidOperationException(
                $"Readiness probe target app '{readiness.TargetApp}' was not registered.");
        if (!appHttpPorts.TryGetValue(readiness.TargetApp, out var internalPort) || internalPort <= 0)
            throw new InvalidOperationException(
                $"Readiness probe target app '{readiness.TargetApp}' has no internal port.");

        var hostPort = container.GetMappedPort(internalPort);
        var url = $"http://localhost:{hostPort}{readiness.Path}";

        int lastStatus = 0;
        string? lastBody = null;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= readiness.MaxAttempts; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(url);
                lastStatus = (int)response.StatusCode;
                if (lastStatus == readiness.ExpectedStatusCode)
                {
                    logger.WriteLine($"[Seed]    readiness {url} -> {readiness.ExpectedStatusCode} (attempt {attempt})");
                    return;
                }
                try { lastBody = await response.Content.ReadAsStringAsync(); } catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            await Task.Delay(readiness.PollDelay);
        }

        string targetLogs = "";
        try { targetLogs = await container.GetLogsAsync(); } catch { /* best effort */ }

        var diag = lastException is not null
            ? $"last exception: {lastException.GetType().Name}: {lastException.Message}"
            : $"last status: {lastStatus}; body (first 300): {Truncate(lastBody ?? "", 300)}";

        throw new TimeoutException(
            $"Readiness probe for {url} did not return {readiness.ExpectedStatusCode} " +
            $"within {readiness.MaxAttempts} attempts (poll {readiness.PollDelay.TotalMilliseconds}ms). " +
            $"{diag}\n--- {readiness.TargetApp} container logs (tail) ---\n{Tail(targetLogs, 2000)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table wait
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ExecuteTableWaitAsync(
        TableWaitSeed step,
        ManagedAzuriteContainer? azurite,
        IReadOnlyDictionary<string, ManagedAppContainer> appContainers,
        bool bypassSsl,
        TextWriter logger)
    {
        if (azurite is null)
            throw new InvalidOperationException(
                $"TableWait '{step.TableName}' requires Azurite, but AddAzurite() was not called.");

        var options = new TableClientOptions();
        if (bypassSsl) ConfigureSslBypass(options);
        var client = new TableClient(azurite.ConnectionString, step.TableName, options);

        var deadline = DateTime.UtcNow + step.Timeout;
        Exception? lastException = null;
        int lastMatchCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (step.PartitionKey is not null && step.RowKey is not null)
                {
                    // Point lookup
                    try
                    {
                        await client.GetEntityAsync<TableEntity>(step.PartitionKey, step.RowKey);
                        logger.WriteLine(
                            $"[Seed]    table row {step.TableName}[{step.PartitionKey}/{step.RowKey}] found.");
                        return;
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        lastMatchCount = 0;
                    }
                }
                else if (step.Filter is not null)
                {
                    // Filter query — count matching rows
                    int matchCount = 0;
                    await foreach (var _ in client.QueryAsync<TableEntity>(step.Filter, maxPerPage: step.MinMatchingRows))
                    {
                        matchCount++;
                        if (matchCount >= step.MinMatchingRows) break;
                    }
                    lastMatchCount = matchCount;
                    if (matchCount >= step.MinMatchingRows)
                    {
                        logger.WriteLine(
                            $"[Seed]    table {step.TableName} filter='{step.Filter}' matched {matchCount} row(s).");
                        return;
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "TableWaitSeed requires either (PartitionKey + RowKey) or Filter — builder bug?");
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404 && ex.Message.Contains("TableNotFound", StringComparison.OrdinalIgnoreCase))
            {
                // Table doesn't exist yet — keep polling
                lastException = ex;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            await Task.Delay(step.PollDelay);
        }

        var what = step.PartitionKey is not null
            ? $"row [{step.PartitionKey}/{step.RowKey}]"
            : $"filter '{step.Filter}' (need {step.MinMatchingRows}, last seen {lastMatchCount})";

        // Diagnostic dump on timeout: list actual rows in the table so we can see
        // what PartitionKey/RowKey/EventType the producer actually wrote.
        try
        {
            logger.WriteLine($"[Seed]    >>> TableWait TIMEOUT diagnostic dump of '{step.TableName}' (first 50 rows):");
            int dumped = 0;
            await foreach (var row in client.QueryAsync<TableEntity>(filter: (string?)null, maxPerPage: 50))
            {
                if (dumped >= 50) break;
                var keys = string.Join(",", row.Keys.Where(k => k != "PartitionKey" && k != "RowKey" && k != "Timestamp" && k != "odata.etag").Take(8));
                logger.WriteLine($"[Seed]      PK='{row.PartitionKey}' RK='{Truncate(row.RowKey, 80)}' cols=[{keys}]");
                dumped++;
            }
            if (dumped == 0)
            {
                logger.WriteLine($"[Seed]      (table '{step.TableName}' is empty or doesn't exist)");
            }
            else
            {
                logger.WriteLine($"[Seed]    <<< end dump ({dumped} row(s))");
            }
        }
        catch (Exception dumpEx)
        {
            logger.WriteLine($"[Seed]    diagnostic dump failed: {dumpEx.GetType().Name}: {dumpEx.Message}");
        }

        // Diagnostic dump: list ALL tables (besides the target one)
        try
        {
            var tableServiceClient = new TableServiceClient(azurite.ConnectionString, options);
            logger.WriteLine($"[Seed]    >>> All tables in Azurite:");
            int t = 0;
            await foreach (var tbl in tableServiceClient.QueryAsync())
            {
                t++;
                int rowCount = 0;
                try
                {
                    var tc = new TableClient(azurite.ConnectionString, tbl.Name, options);
                    await foreach (var _ in tc.QueryAsync<TableEntity>(filter: (string?)null, maxPerPage: 5))
                    {
                        if (++rowCount >= 5) break;
                    }
                }
                catch { /* best effort */ }
                logger.WriteLine($"[Seed]      table '{tbl.Name}' (>=  {rowCount} row(s))");
            }
            if (t == 0) logger.WriteLine($"[Seed]      (no tables)");
        }
        catch (Exception ex) { logger.WriteLine($"[Seed]    table list failed: {ex.Message}"); }

        // Diagnostic dump: list all queues and their message counts (use peek)
        try
        {
            var queueOptions = new QueueClientOptions();
            if (bypassSsl) ConfigureSslBypass(queueOptions);
            var queueServiceClient = new QueueServiceClient(azurite.ConnectionString, queueOptions);
            logger.WriteLine($"[Seed]    >>> All queues in Azurite (with approximate message count):");
            int q = 0;
            await foreach (var qi in queueServiceClient.GetQueuesAsync())
            {
                q++;
                int count = 0;
                try
                {
                    var perQueueOptions = new QueueClientOptions();
                    if (bypassSsl) ConfigureSslBypass(perQueueOptions);
                    var qc = new QueueClient(azurite.ConnectionString, qi.Name, perQueueOptions);
                    var props = await qc.GetPropertiesAsync();
                    count = props.Value.ApproximateMessagesCount;
                }
                catch { /* best effort */ }
                logger.WriteLine($"[Seed]      queue '{qi.Name}' approxCount={count}");
            }
            if (q == 0) logger.WriteLine($"[Seed]      (no queues)");
        }
        catch (Exception ex) { logger.WriteLine($"[Seed]    queue list failed: {ex.Message}"); }

        // Diagnostic dump: list all blob containers and per-container blob count
        try
        {
            var blobOptions = new BlobClientOptions();
            if (bypassSsl) ConfigureSslBypass(blobOptions);
            var blobServiceClient = new BlobServiceClient(azurite.ConnectionString, blobOptions);
            logger.WriteLine($"[Seed]    >>> All blob containers in Azurite:");
            int c = 0;
            await foreach (var bc in blobServiceClient.GetBlobContainersAsync())
            {
                c++;
                int blobCount = 0;
                string firstBlob = "";
                try
                {
                    var cc = blobServiceClient.GetBlobContainerClient(bc.Name);
                    await foreach (var blob in cc.GetBlobsAsync())
                    {
                        if (blobCount == 0) firstBlob = blob.Name;
                        if (++blobCount >= 20) break;
                    }
                }
                catch { /* best effort */ }
                logger.WriteLine($"[Seed]      container '{bc.Name}' blobs>={blobCount} firstBlob='{Truncate(firstBlob, 120)}'");
            }
            if (c == 0) logger.WriteLine($"[Seed]      (no containers)");
        }
        catch (Exception ex) { logger.WriteLine($"[Seed]    container list failed: {ex.Message}"); }

        // Diagnostic dump: tail logs from every app container so we can see
        // why orch didn't write the expected CollectorIndex row. Also write
        // FULL logs to disk so we can grep for billing events.
        foreach (var (appName, appContainer) in appContainers)
        {
            try
            {
                var logs = await appContainer.GetLogsAsync();
                var logDir = Path.Combine(Path.GetTempPath(), "slt-logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, $"{appName}.log");
                await File.WriteAllTextAsync(logPath, logs);
                logger.WriteLine($"[Seed]    >>> {appName} logs ({logs.Length} chars) -> {logPath} (tail 4000):");
                logger.WriteLine(Tail(logs, 4000));
                logger.WriteLine($"[Seed]    <<< end {appName} logs");
            }
            catch (Exception ex) { logger.WriteLine($"[Seed]    {appName} log dump failed: {ex.Message}"); }
        }

        throw new TimeoutException(
            $"TableWait timed out after {step.Timeout.TotalSeconds:F1}s: " +
            $"{step.TableName} {what} did not appear. " +
            (lastException is not null ? $"Last error: {lastException.GetType().Name}: {lastException.Message}" : ""));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string ResolveFixturePath(string fixtureRoot, string fixturePath)
        => Path.IsPathRooted(fixturePath) ? fixturePath : Path.Combine(fixtureRoot, fixturePath);

    private static void ConfigureSslBypass(Azure.Core.ClientOptions options)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        options.Transport = new Azure.Core.Pipeline.HttpClientTransport(handler);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    private static string Tail(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : "..." + s[^max..];
}

/// <summary>
/// Thrown when a seed step fails. Wraps the original exception with step context.
/// </summary>
public class SeedExecutionException : Exception
{
    /// <summary>Initializes a new <see cref="SeedExecutionException"/>.</summary>
    public SeedExecutionException(string message, Exception? inner = null) : base(message, inner) { }
}
