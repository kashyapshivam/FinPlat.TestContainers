using System;
using System.Collections.Generic;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Declarative seed step executed after all containers are healthy.
/// Implementations are sealed records added by <see cref="Builder.SeedBuilder"/>.
/// </summary>
public interface ISeedStep
{
    /// <summary>Friendly name used in error messages / logs. May be null.</summary>
    string? Name { get; }

    /// <summary>Discriminator used by the executor.</summary>
    SeedKind Kind { get; }
}

/// <summary>Kinds of seed steps supported by the library.</summary>
public enum SeedKind
{
    /// <summary>Upsert rows into an Azurite table.</summary>
    Table,
    /// <summary>Upload a single blob into an Azurite blob container.</summary>
    Blob,
    /// <summary>POST a payload to a registered application container's HTTP endpoint.</summary>
    HttpPost,
    /// <summary>Block until an entity / matching rows appear in an Azurite table.</summary>
    WaitForTable,
}

/// <summary>
/// Seeds rows into an Azurite table from a JSON fixture.
/// The fixture must be a JSON array of objects. Each object must contain
/// "PartitionKey" and "RowKey" string properties; other properties become
/// table entity columns. Use a "@type" suffix (e.g. "Created@type": "DateTime")
/// to coerce a value to a non-string Edm type.
/// </summary>
public sealed record TableSeed(
    string TableName,
    string FixturePath,
    string? Name = null) : ISeedStep
{
    /// <inheritdoc />
    public SeedKind Kind => SeedKind.Table;
}

/// <summary>
/// Uploads a single blob into an Azurite blob container from a file on disk.
/// Overwrites any existing blob with the same name.
/// </summary>
public sealed record BlobSeed(
    string ContainerName,
    string BlobName,
    string FixturePath,
    string? Name = null) : ISeedStep
{
    /// <inheritdoc />
    public SeedKind Kind => SeedKind.Blob;
}

/// <summary>
/// POSTs a payload to a registered application container. Useful for ingesting
/// events through a real distributed pipeline (e.g. Collector.FD ingest API).
/// Exactly one of <see cref="InlineBody"/> or <see cref="FixturePath"/> must be set.
/// </summary>
public sealed record HttpSeed(
    string TargetApp,
    string Path,
    byte[]? InlineBody,
    string? FixturePath,
    string ContentType,
    IReadOnlyDictionary<string, string>? RequestHeaders,
    int RetryAttempts,
    TimeSpan RetryDelay,
    SeedReadinessCheck? Readiness,
    string? Name) : ISeedStep
{
    /// <inheritdoc />
    public SeedKind Kind => SeedKind.HttpPost;
}

/// <summary>
/// Optional readiness probe for an <see cref="HttpSeed"/>. The executor polls
/// the given URL until it returns the expected status code (or attempts run out)
/// before sending the seed payload.
/// </summary>
public sealed record SeedReadinessCheck(
    string TargetApp,
    string Path,
    int ExpectedStatusCode,
    int MaxAttempts,
    TimeSpan PollDelay);

/// <summary>
/// Polls an Azurite table until a target row (or rows matching a filter) appear.
/// This is the durability bridge between an <see cref="HttpSeed"/> POST and a downstream
/// reader: a 2xx response does not prove the receiver flushed to storage, so use a
/// <see cref="TableWaitSeed"/> as the next step to make sure the row is queryable
/// before the test starts asserting on downstream behavior.
/// Exactly one of (<see cref="PartitionKey"/>+<see cref="RowKey"/>) or <see cref="Filter"/> must be set.
/// </summary>
public sealed record TableWaitSeed(
    string TableName,
    string? PartitionKey,
    string? RowKey,
    string? Filter,
    int MinMatchingRows,
    TimeSpan Timeout,
    TimeSpan PollDelay,
    string? Name) : ISeedStep
{
    /// <inheritdoc />
    public SeedKind Kind => SeedKind.WaitForTable;
}

/// <summary>
/// Aggregated seed configuration. Steps are executed in declaration order.
/// </summary>
public class SeedConfig
{
    /// <summary>Ordered list of seed steps. Executed sequentially in declaration order.</summary>
    public List<ISeedStep> Steps { get; } = new();

    /// <summary>
    /// Root directory used to resolve relative fixture paths. Defaults to
    /// <see cref="AppContext.BaseDirectory"/> (the test output folder).
    /// </summary>
    public string FixtureRoot { get; set; } = AppContext.BaseDirectory;
}
