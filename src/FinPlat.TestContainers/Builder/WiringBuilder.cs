using System.Collections.Generic;
using FinPlat.TestContainers.Config;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Declares the infrastructure dependencies (queues, blobs, tables, mock APIs) that an
/// application container needs. The builder translates these into environment variables
/// injected at container start time.
/// </summary>
public class WiringBuilder
{
    internal WiringConfig Config { get; } = new();

    /// <summary>
    /// Declares that the application requires access to an Azure Storage queue.
    /// The queue will be pre-created in Azurite before the app starts.
    /// </summary>
    /// <param name="queueName">Name of the queue to create.</param>
    public WiringBuilder Queue(string queueName)
    {
        Config.Queues.Add(queueName);
        return this;
    }

    /// <summary>
    /// Declares that the application requires access to an Azure Blob Storage container.
    /// The container will be pre-created in Azurite before the app starts.
    /// </summary>
    /// <param name="containerName">Name of the blob container to create.</param>
    public WiringBuilder Blob(string containerName)
    {
        Config.BlobContainers.Add(containerName);
        return this;
    }

    /// <summary>
    /// Declares that the application requires access to an Azure Table Storage table.
    /// The table will be pre-created in Azurite before the app starts.
    /// </summary>
    /// <param name="tableName">Name of the table to create.</param>
    public WiringBuilder Table(string tableName)
    {
        Config.Tables.Add(tableName);
        return this;
    }

    /// <summary>
    /// Declares that the application depends on a mock API. The mock API's internal URL
    /// will be injected as an environment variable with the specified config key.
    /// </summary>
    /// <param name="mockName">Name of the mock API (must match a name registered via AddMockApi).</param>
    /// <param name="asConfigKey">Environment variable name to use (e.g., "CollectorUri").</param>
    public WiringBuilder MockApi(string mockName, string asConfigKey)
    {
        Config.MockApiBindings[mockName] = asConfigKey;
        return this;
    }

    /// <summary>
    /// Signals that this application uses token-based authentication for storage access.
    /// When set, the ConfigInjector will emit Azure Identity env vars and endpoint suffix
    /// instead of a connection string.
    /// </summary>
    public WiringBuilder UseTokenAuth()
    {
        Config.UseTokenAuth = true;
        return this;
    }

    /// <summary>
    /// Injects an Azurite connection string into the application container.
    /// Use this for apps that need simple connection-string access even when
    /// the global Azurite is in token auth mode.
    /// </summary>
    /// <param name="envVarName">
    /// Environment variable name. Defaults to "ConnectionStrings__AzureStorage".
    /// </param>
    public WiringBuilder StorageConnectionString(string envVarName = "ConnectionStrings__AzureStorage")
    {
        Config.InjectConnectionString = true;
        Config.ConnectionStringEnvVar = envVarName;
        return this;
    }

    /// <summary>
    /// Injects another application's internal URL as an environment variable.
    /// Creates an implicit startup dependency: this app waits for the target app's readiness.
    /// </summary>
    /// <param name="targetAppName">Name of the target application (must match AddApplication name).</param>
    /// <param name="asEnvVar">Environment variable name to inject the URL into.</param>
    /// <param name="port">Internal port of the target application. Defaults to the app's declared port.</param>
    /// <param name="scheme">URL scheme. Defaults to "http".</param>
    public WiringBuilder AppUrl(string targetAppName, string asEnvVar, int? port = null, string scheme = "http")
    {
        Config.AppUrlBindings[targetAppName] = (asEnvVar, port, scheme);
        return this;
    }
}
