using System.Collections.Generic;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Provides pre-built WireMock stub definitions for Azure AD/MSAL token authentication flows.
/// These stubs satisfy WorkloadIdentityCredential and ClientAssertionCredential token acquisition.
/// </summary>
public static class AuthStubs
{
    private const string FakeAccessToken =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJodHRwczovL3N0b3JhZ2UuYXp1cmUuY29tIiwiaXNzIjoiaHR0cHM6Ly9zdHMud2luZG93cy5uZXQvMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAwLyIsImlhdCI6MTcwMDAwMDAwMCwiZXhwIjo5OTk5OTk5OTk5LCJzdWIiOiJ0ZXN0LXN1YmplY3QifQ.fake-signature";

    /// <summary>
    /// Creates a stub for the OAuth2 token endpoint (POST /{tenant}/oauth2/v2.0/token).
    /// Returns a valid-looking access token response.
    /// </summary>
    public static StubDefinition TokenEndpoint(string tenantId = "00000000-0000-0000-0000-000000000000")
    {
        var responseBody = $$"""
        {
            "token_type": "Bearer",
            "expires_in": 86400,
            "ext_expires_in": 86400,
            "access_token": "{{FakeAccessToken}}"
        }
        """;

        return new StubDefinition
        {
            Method = "POST",
            Path = $"/{tenantId}/oauth2/v2.0/token",
            StatusCode = 200,
            ResponseBody = responseBody
        };
    }

    /// <summary>
    /// Creates a stub for the OpenID Connect discovery endpoint.
    /// </summary>
    public static StubDefinition OpenIdConfig(string tenantId = "00000000-0000-0000-0000-000000000000")
    {
        var responseBody = $$"""
        {
            "token_endpoint": "https://login.microsoftonline.com/{{tenantId}}/oauth2/v2.0/token",
            "authorization_endpoint": "https://login.microsoftonline.com/{{tenantId}}/oauth2/v2.0/authorize",
            "issuer": "https://sts.windows.net/{{tenantId}}/",
            "jwks_uri": "https://login.microsoftonline.com/{{tenantId}}/discovery/v2.0/keys"
        }
        """;

        return new StubDefinition
        {
            Method = "GET",
            Path = $"/{tenantId}/v2.0/.well-known/openid-configuration",
            StatusCode = 200,
            ResponseBody = responseBody
        };
    }

    /// <summary>
    /// Creates a stub for MSAL's instance discovery endpoint.
    /// Returns metadata indicating login.microsoftonline.com is a known authority.
    /// </summary>
    public static StubDefinition InstanceDiscovery()
    {
        var responseBody = """
        {
            "tenant_discovery_endpoint": "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
            "metadata": [
                {
                    "preferred_network": "login.microsoftonline.com",
                    "preferred_cache": "login.microsoftonline.com",
                    "aliases": ["login.microsoftonline.com", "login.windows.net", "sts.windows.net"]
                }
            ]
        }
        """;

        return new StubDefinition
        {
            Method = "GET",
            Path = "/common/discovery/instance",
            StatusCode = 200,
            ResponseBody = responseBody
        };
    }

    /// <summary>
    /// Returns all auth stubs needed for a complete token auth flow.
    /// </summary>
    public static IEnumerable<StubDefinition> All(string tenantId = "00000000-0000-0000-0000-000000000000")
    {
        yield return TokenEndpoint(tenantId);
        yield return OpenIdConfig(tenantId);
        yield return InstanceDiscovery();
    }
}
