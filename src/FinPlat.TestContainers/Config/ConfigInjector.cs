using System.Collections.Generic;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Static helper that generates environment variable overrides for application containers
/// based on their wiring configuration and auth mode.
/// </summary>
public static class ConfigInjector
{
    /// <summary>
    /// Builds a dictionary of environment variables to inject into an application container.
    /// </summary>
    /// <param name="wiring">The wiring configuration declaring what the app depends on.</param>
    /// <param name="azuriteInternalConnectionString">
    /// Azurite connection string using the Docker-internal hostname (container-to-container).
    /// </param>
    /// <param name="mockApiInternalUrls">
    /// Dictionary mapping mock API names to their internal URLs on the Docker network.
    /// </param>
    /// <param name="azuriteOptions">Optional Azurite options for token auth mode.</param>
    /// <returns>A dictionary of environment variable name-value pairs.</returns>
    public static Dictionary<string, string> BuildEnvVars(
        WiringConfig wiring,
        string? azuriteInternalConnectionString,
        Dictionary<string, string> mockApiInternalUrls,
        AzuriteOptions? azuriteOptions = null)
    {
        var envVars = new Dictionary<string, string>();

        bool useTokenAuth = azuriteOptions?.UseTokenAuth == true;

        if (useTokenAuth)
        {
            // Token auth mode: inject Azure Identity env vars + endpoint suffix
            envVars["AZURE_CLIENT_ID"] = azuriteOptions!.ClientId;
            envVars["AZURE_TENANT_ID"] = azuriteOptions.TenantId;
            envVars["AZURE_AUTHORITY_HOST"] = $"https://{azuriteOptions.AuthorityHosts[0]}";
            envVars["AZURE_FEDERATED_TOKEN_FILE"] = "/app/federated-token.txt";

            // Storage endpoint configuration
            envVars["StorageAccountEndpointSuffix"] = azuriteOptions.EndpointSuffix;
            envVars["StorageAccountName"] = azuriteOptions.AccountName;

            // Cert path for app to trust
            envVars["SLT_CERT_PATH"] = "/certs/ca-cert.pem";
        }
        else
        {
            // Simple mode: inject connection string if Azurite is available
            if (azuriteInternalConnectionString is not null &&
                (wiring.Queues.Count > 0 || wiring.BlobContainers.Count > 0 || wiring.Tables.Count > 0))
            {
                envVars["ConnectionStrings__AzureStorage"] = azuriteInternalConnectionString;
            }
        }

        // Inject mock API URLs as environment variables using the configured key names
        foreach (var (mockName, configKey) in wiring.MockApiBindings)
        {
            if (mockApiInternalUrls.TryGetValue(mockName, out var internalUrl))
            {
                envVars[configKey] = internalUrl;
            }
        }

        return envVars;
    }
}
