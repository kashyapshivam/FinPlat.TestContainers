namespace FinPlat.TestContainers.Config;

/// <summary>
/// Configuration options for Azurite container behavior.
/// Controls whether to use simple connection string mode or token auth (HTTPS + OAuth) mode.
/// </summary>
public class AzuriteOptions
{
    /// <summary>
    /// When true, Azurite starts with HTTPS + OAuth support and a TLS proxy is provisioned
    /// for virtual-hosted-style URL routing. Apps use Bearer token auth instead of connection strings.
    /// Default: false (simple connection string mode).
    /// </summary>
    public bool UseTokenAuth { get; set; }

    /// <summary>
    /// The DNS suffix for storage endpoints (e.g., "azurite.local").
    /// Used to construct virtual-hosted-style URLs: https://{AccountName}.queue.{EndpointSuffix}/
    /// Only relevant when <see cref="UseTokenAuth"/> is true.
    /// Default: "azurite.local"
    /// </summary>
    public string EndpointSuffix { get; set; } = "azurite.local";

    /// <summary>
    /// The storage account name. Default: "devstoreaccount1" (Azurite's built-in account).
    /// </summary>
    public string AccountName { get; set; } = "devstoreaccount1";

    /// <summary>
    /// Whether to pass --skipApiVersionCheck to Azurite. Required when using newer Azure SDK versions
    /// that send API versions Azurite hasn't been updated to support.
    /// Default: true
    /// </summary>
    public bool SkipApiVersionCheck { get; set; } = true;

    /// <summary>
    /// Authority hosts to intercept via DNS and route to WireMock for token acquisition.
    /// Default: ["login.microsoftonline.com"]
    /// </summary>
    public List<string> AuthorityHosts { get; set; } = new() { "login.microsoftonline.com" };

    /// <summary>
    /// The tenant ID to use in auth stub responses.
    /// Default: "00000000-0000-0000-0000-000000000000"
    /// </summary>
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// The client ID for the fake service principal.
    /// Default: "00000000-0000-0000-0000-000000000000"
    /// </summary>
    public string ClientId { get; set; } = "00000000-0000-0000-0000-000000000000";
}
