using System;
using System.Collections.Generic;
using System.IO;
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
        var (certPath, keyPath) = cert.WriteTempFiles();
        var nginxConf = GenerateNginxConfig();

        // Write nginx config to temp file
        var confPath = Path.Combine(Path.GetDirectoryName(certPath)!, "nginx.conf");
        await File.WriteAllTextAsync(confPath, nginxConf);

        var aliases = new List<string>(GetNetworkAliases());

        var builder = new ContainerBuilder()
            .WithImage("nginx:alpine")
            .WithNetwork(network)
            .WithNetworkAliases(aliases.ToArray())
            .WithResourceMapping(certPath, "/etc/nginx/certs/cert.pem")
            .WithResourceMapping(keyPath, "/etc/nginx/certs/key.pem")
            .WithResourceMapping(confPath, "/etc/nginx/nginx.conf")
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

    private string GenerateNginxConfig()
    {
        var suffix = _options.EndpointSuffix;
        var sb = new StringBuilder();

        sb.AppendLine("worker_processes 1;");
        sb.AppendLine("events { worker_connections 128; }");
        sb.AppendLine();
        sb.AppendLine("http {");

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

        sb.AppendLine("}");

        return sb.ToString();
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
        sb.AppendLine($"            proxy_pass https://azurite:{port};");
        sb.AppendLine($"            proxy_ssl_verify off;");
        sb.AppendLine($"            proxy_set_header Host $host;");
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
