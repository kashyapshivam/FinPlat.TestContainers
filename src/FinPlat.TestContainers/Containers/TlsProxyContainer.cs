using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FinPlat.TestContainers.Config;

namespace FinPlat.TestContainers.Containers;

/// <summary>
/// Manages an nginx TLS reverse proxy container that routes virtual-hosted-style HTTPS
/// storage requests to Azurite and auth requests to WireMock.
/// This is an internal component created automatically when token auth is enabled.
/// </summary>
internal class ManagedTlsProxyContainer : IAsyncDisposable
{
    private IContainer? _container;
    private readonly AzuriteOptions _options;
    private readonly string _wireMockAlias;

    internal ManagedTlsProxyContainer(AzuriteOptions options, string wireMockAlias)
    {
        _options = options;
        _wireMockAlias = wireMockAlias;
    }

    /// <summary>
    /// Gets all Docker network aliases this proxy needs to route storage and auth traffic.
    /// </summary>
    internal IEnumerable<string> GetNetworkAliases()
    {
        var suffix = _options.EndpointSuffix;
        var account = _options.AccountName;

        yield return $"{account}.blob.{suffix}";
        yield return $"{account}.queue.{suffix}";
        yield return $"{account}.table.{suffix}";
        yield return $"{account}.dfs.{suffix}";

        foreach (var host in _options.AuthorityHosts)
        {
            yield return host;
        }
    }

    /// <summary>
    /// Starts the nginx proxy container with generated config and certificates.
    /// </summary>
    internal async Task StartAsync(INetwork network, CertificateMaterial cert)
    {
        var nginxConf = GenerateNginxConfig();

        var aliases = new List<string>(GetNetworkAliases());

        var builder = new ContainerBuilder()
            .WithImage("nginx:alpine")
            .WithNetwork(network)
            .WithNetworkAliases(aliases.ToArray())
            .WithResourceMapping(Encoding.UTF8.GetBytes(cert.CertPem), "/etc/nginx/certs/cert.pem")
            .WithResourceMapping(Encoding.UTF8.GetBytes(cert.KeyPem), "/etc/nginx/certs/key.pem")
            .WithResourceMapping(Encoding.UTF8.GetBytes(nginxConf), "/etc/nginx/nginx.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(443));

        _container = builder.Build();
        await _container.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    internal async Task<string> GetLogsAsync()
    {
        if (_container is null) return string.Empty;
        var (stdout, stderr) = await _container.GetLogsAsync();
        return $"{stdout}\n{stderr}";
    }

    private string GenerateNginxConfig()
    {
        var suffix = _options.EndpointSuffix;
        var sb = new StringBuilder();

        sb.AppendLine("worker_processes 1;");
        sb.AppendLine("error_log /dev/stderr info;");
        sb.AppendLine("events { worker_connections 128; }");
        sb.AppendLine();
        sb.AppendLine("http {");
        sb.AppendLine("    access_log /dev/stderr;");

        // Auth endpoint(s) - route to WireMock
        foreach (var host in _options.AuthorityHosts)
        {
            sb.AppendLine($"    server {{");
            sb.AppendLine($"        listen 443 ssl;");
            sb.AppendLine($"        server_name {host};");
            sb.AppendLine();
            sb.AppendLine($"        ssl_certificate /etc/nginx/certs/cert.pem;");
            sb.AppendLine($"        ssl_certificate_key /etc/nginx/certs/key.pem;");
            sb.AppendLine();
            sb.AppendLine($"        location / {{");
            sb.AppendLine($"            proxy_pass http://{_wireMockAlias}:8080;");
            sb.AppendLine($"            proxy_set_header Host $host;");
            sb.AppendLine($"            proxy_set_header X-Forwarded-Proto https;");
            sb.AppendLine($"        }}");
            sb.AppendLine($"    }}");
            sb.AppendLine();
        }

        // Blob service - route to Azurite port 10000
        AppendStorageServer(sb, "blob", 10000);

        // Queue service - route to Azurite port 10001
        AppendStorageServer(sb, "queue", 10001);

        // Table service - route to Azurite port 10002
        AppendStorageServer(sb, "table", 10002);

        // DFS (Data Lake) service - Azurite doesn't support DFS APIs,
        // so we return mock success responses directly from nginx.
        AppendDfsMockServer(sb);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private void AppendDfsMockServer(StringBuilder sb)
    {
        var suffix = _options.EndpointSuffix;

        sb.AppendLine($"    server {{");
        sb.AppendLine($"        listen 443 ssl;");
        sb.AppendLine($"        server_name ~^(?<account>.+)\\.dfs\\.{EscapeForRegex(suffix)}$;");
        sb.AppendLine();
        sb.AppendLine($"        ssl_certificate /etc/nginx/certs/cert.pem;");
        sb.AppendLine($"        ssl_certificate_key /etc/nginx/certs/key.pem;");
        sb.AppendLine($"        client_max_body_size 256m;");
        sb.AppendLine();
        // DFS CreatePath (PUT ?resource=file) → 201 Created
        sb.AppendLine($"        location / {{");
        sb.AppendLine($"            if ($request_method = PUT) {{");
        sb.AppendLine($"                return 201;");
        sb.AppendLine($"            }}");
        // DFS Append (PATCH ?action=append) → 202, Flush (PATCH ?action=flush) → 200
        sb.AppendLine($"            if ($arg_action = append) {{");
        sb.AppendLine($"                return 202;");
        sb.AppendLine($"            }}");
        sb.AppendLine($"            if ($arg_action = flush) {{");
        sb.AppendLine($"                return 200;");
        sb.AppendLine($"            }}");
        // DFS GetProperties (HEAD) → 200 OK
        sb.AppendLine($"            if ($request_method = HEAD) {{");
        sb.AppendLine($"                return 200;");
        sb.AppendLine($"            }}");
        sb.AppendLine($"            return 200;");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
        sb.AppendLine();
    }

    private void AppendStorageServer(StringBuilder sb, string service, int port)
    {
        var suffix = _options.EndpointSuffix;

        sb.AppendLine($"    server {{");
        sb.AppendLine($"        listen 443 ssl;");
        sb.AppendLine($"        server_name ~^(?<account>.+)\\.{service}\\.{EscapeForRegex(suffix)}$;");
        sb.AppendLine();
        sb.AppendLine($"        ssl_certificate /etc/nginx/certs/cert.pem;");
        sb.AppendLine($"        ssl_certificate_key /etc/nginx/certs/key.pem;");
        sb.AppendLine();
        sb.AppendLine($"        location / {{");
        sb.AppendLine($"            rewrite ^/(.*)$ /$account/$1 break;");
        sb.AppendLine($"            proxy_pass https://azurite:{port};");
        sb.AppendLine($"            proxy_ssl_verify off;");
        sb.AppendLine($"            proxy_set_header Host 127.0.0.1;");
        sb.AppendLine($"            proxy_set_header Authorization $http_authorization;");
        sb.AppendLine($"            proxy_set_header X-Forwarded-Proto https;");

        if (service == "blob")
        {
            sb.AppendLine($"            client_max_body_size 256m;");
            sb.AppendLine($"            proxy_request_buffering off;");
        }

        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
        sb.AppendLine();
    }

    private static string EscapeForRegex(string input)
    {
        return input.Replace(".", "\\.");
    }
}
