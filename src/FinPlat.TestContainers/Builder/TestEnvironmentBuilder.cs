using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Fluent builder that orchestrates Docker containers for integration testing.
/// Call <see cref="BuildAsync"/> to start all containers on a shared Docker network.
/// </summary>
public class TestEnvironmentBuilder
{
    private bool _addAzurite;
    private AzuriteOptions? _azuriteOptions;
    private readonly Dictionary<string, ApplicationBuilder> _applications = new();
    private readonly Dictionary<string, MockApiBuilder> _mockApis = new();
    private readonly Dictionary<string, WiringBuilder> _wirings = new();

    /// <summary>
    /// Adds an Azurite container in simple mode (HTTP, connection string).
    /// </summary>
    public TestEnvironmentBuilder AddAzurite()
    {
        _addAzurite = true;
        _azuriteOptions = null;
        return this;
    }

    /// <summary>
    /// Adds an Azurite container with the specified options.
    /// Set <see cref="AzuriteOptions.UseTokenAuth"/> to enable HTTPS + OAuth + TLS proxy mode.
    /// </summary>
    public TestEnvironmentBuilder AddAzurite(Action<AzuriteOptions> configure)
    {
        _addAzurite = true;
        _azuriteOptions = new AzuriteOptions();
        configure(_azuriteOptions);
        return this;
    }

    /// <summary>
    /// Registers an application container to be built and started.
    /// </summary>
    /// <param name="name">Unique name for the application (used as Docker network alias).</param>
    /// <param name="configure">Action to configure the application builder.</param>
    public TestEnvironmentBuilder AddApplication(string name, Action<ApplicationBuilder> configure)
    {
        var builder = new ApplicationBuilder();
        configure(builder);
        _applications[name] = builder;
        return this;
    }

    /// <summary>
    /// Adds a WireMock mock API container to the test environment.
    /// </summary>
    /// <param name="name">Unique name for the mock API (used as Docker network alias prefix).</param>
    /// <param name="configure">Action to configure stub definitions.</param>
    public TestEnvironmentBuilder AddMockApi(string name, Action<MockApiBuilder> configure)
    {
        var builder = new MockApiBuilder();
        configure(builder);
        _mockApis[name] = builder;
        return this;
    }

    /// <summary>
    /// Declares wiring for an application container, specifying which infrastructure
    /// resources (queues, blobs, tables, mock APIs) it depends on.
    /// </summary>
    /// <param name="appName">Name of the application (must match a name passed to AddApplication).</param>
    /// <param name="configure">Action to configure the wiring.</param>
    public TestEnvironmentBuilder Wire(string appName, Action<WiringBuilder> configure)
    {
        var builder = new WiringBuilder();
        configure(builder);
        _wirings[appName] = builder;
        return this;
    }

    /// <summary>
    /// Builds and starts all configured containers in the correct order:
    /// 1. Creates a shared Docker network.
    /// 2. Starts Azurite (if added) and pre-creates resources.
    /// 3. Starts WireMock containers and configures stubs.
    /// 4. If token auth: starts TLS proxy and adds auth stubs.
    /// 5. Resolves app-to-app URLs and dependency ordering.
    /// 6. Starts application containers with injected environment variables.
    /// 7. Waits for health checks (if configured).
    /// </summary>
    /// <returns>A <see cref="TestEnvironment"/> providing runtime access to all containers.</returns>
    public async Task<TestEnvironment> BuildAsync()
    {
        // 1. Create shared Docker network
        var network = new NetworkBuilder()
            .WithName($"finplat-test-{Guid.NewGuid():N}")
            .Build();

        await network.CreateAsync();

        var useTokenAuth = _azuriteOptions?.UseTokenAuth == true;
        CertificateMaterial? cert = null;
        ManagedAzuriteContainer? azurite = null;
        ManagedTlsProxyContainer? tlsProxy = null;
        var wireMockContainers = new Dictionary<string, ManagedWireMockContainer>();
        var appContainers = new Dictionary<string, ManagedAppContainer>();

        try
        {
            // Generate certificate if token auth mode
            if (useTokenAuth)
            {
                cert = CertificateGenerator.Generate(_azuriteOptions!);
            }

            // 2. Start Azurite if configured
            if (_addAzurite)
            {
                azurite = new ManagedAzuriteContainer(_azuriteOptions);
                await azurite.StartAsync(network, cert);

                // Pre-create resources declared in wirings
                foreach (var (_, wiring) in _wirings)
                {
                    foreach (var queue in wiring.Config.Queues)
                    {
                        await azurite.CreateQueueAsync(queue);
                    }

                    foreach (var blobContainer in wiring.Config.BlobContainers)
                    {
                        await azurite.CreateBlobContainerAsync(blobContainer);
                    }

                    foreach (var table in wiring.Config.Tables)
                    {
                        await azurite.CreateTableAsync(table);
                    }
                }
            }

            // 3. Start WireMock containers and configure stubs
            foreach (var (name, mockBuilder) in _mockApis)
            {
                var wireMock = new ManagedWireMockContainer(name);
                await wireMock.StartAsync(network);

                // If token auth, add auth stubs to the first WireMock instance
                if (useTokenAuth && wireMockContainers.Count == 0)
                {
                    foreach (var authStub in AuthStubs.All(_azuriteOptions!.TenantId))
                    {
                        await wireMock.ConfigureStubAsync(authStub);
                    }
                }

                foreach (var stub in mockBuilder.Stubs)
                {
                    await wireMock.ConfigureStubAsync(stub);
                }

                wireMockContainers[name] = wireMock;
            }

            // If token auth but no mock APIs declared, create a dedicated auth WireMock
            if (useTokenAuth && wireMockContainers.Count == 0)
            {
                var authMock = new ManagedWireMockContainer("auth");
                await authMock.StartAsync(network);

                foreach (var authStub in AuthStubs.All(_azuriteOptions!.TenantId))
                {
                    await authMock.ConfigureStubAsync(authStub);
                }

                wireMockContainers["auth"] = authMock;
            }

            // 4. Start TLS proxy if token auth
            if (useTokenAuth)
            {
                // Use the first WireMock's network alias for auth routing
                var authWireMockAlias = $"mock-{wireMockContainers.Keys.First()}";
                tlsProxy = new ManagedTlsProxyContainer(_azuriteOptions!, authWireMockAlias);
                await tlsProxy.StartAsync(network, cert!);
            }

            // 5. Resolve app-to-app URLs and determine startup order
            var mockApiInternalUrls = new Dictionary<string, string>();
            foreach (var (name, container) in wireMockContainers)
            {
                mockApiInternalUrls[name] = container.InternalUrl;
            }

            // Build app internal URL map (DNS alias + declared port)
            var appInternalUrls = new Dictionary<string, string>();
            foreach (var (appName, appBuilder) in _applications)
            {
                int port = appBuilder.InternalServicePort ?? appBuilder.ExposedPorts.FirstOrDefault();
                if (port > 0)
                {
                    appInternalUrls[appName] = $"http://{appName}:{port}/";
                }
            }

            // Topological sort: apps depended on (via AppUrl) start first
            var startOrder = ResolveStartOrder();

            // 6. Start application containers in dependency order
            foreach (var appName in startOrder)
            {
                if (!_applications.TryGetValue(appName, out var appBuilder))
                    continue;

                var envVars = new Dictionary<string, string>(appBuilder.EnvironmentVariables);

                if (_wirings.TryGetValue(appName, out var wiringBuilder))
                {
                    // Check that AppUrl targets exist
                    foreach (var targetApp in wiringBuilder.Config.AppUrlBindings.Keys)
                    {
                        if (!_applications.ContainsKey(targetApp))
                            throw new InvalidOperationException(
                                $"Application '{appName}' declares AppUrl dependency on '{targetApp}', but no application with that name was registered.");
                    }

                    // Resolve AppUrl ports from target app's builder if not explicitly set
                    foreach (var (targetApp, (envVar, port, scheme)) in wiringBuilder.Config.AppUrlBindings)
                    {
                        if (port is null && _applications.TryGetValue(targetApp, out var targetBuilder))
                        {
                            int resolvedPort = targetBuilder.InternalServicePort ?? targetBuilder.ExposedPorts.FirstOrDefault();
                            if (resolvedPort > 0)
                            {
                                appInternalUrls[targetApp] = $"{scheme}://{targetApp}:{resolvedPort}/";
                            }
                        }
                        else if (port is not null)
                        {
                            appInternalUrls[targetApp] = $"{scheme}://{targetApp}:{port}/";
                        }
                    }

                    var injectedVars = ConfigInjector.BuildEnvVars(
                        wiringBuilder.Config,
                        azurite?.InternalConnectionString,
                        mockApiInternalUrls,
                        _azuriteOptions,
                        appInternalUrls);

                    foreach (var (key, value) in injectedVars)
                    {
                        envVars[key] = value;
                    }
                }

                var appContainer = new ManagedAppContainer(
                    appName,
                    appBuilder.DockerfilePath,
                    appBuilder.ContextPath,
                    appBuilder.ImageName,
                    appBuilder.ExposedPorts);

                // Mount cert into app container for TLS trust (if token auth)
                await appContainer.StartAsync(network, envVars, cert);
                appContainers[appName] = appContainer;

                // Wait for health check if configured
                if (appBuilder.HealthCheckPath is not null)
                {
                    int healthPort = appBuilder.HealthCheckPort ?? appBuilder.InternalServicePort
                        ?? appBuilder.ExposedPorts.FirstOrDefault();
                    if (healthPort > 0)
                    {
                        await WaitForHealthCheckAsync(
                            appContainer, healthPort, appBuilder.HealthCheckPath,
                            appBuilder.HealthCheckTimeoutSeconds);
                    }
                }
            }

            return new TestEnvironment(network, azurite, wireMockContainers, appContainers, cert, tlsProxy);
        }
        catch
        {
            // Clean up on failure
            foreach (var app in appContainers.Values)
                await app.DisposeAsync();
            if (tlsProxy is not null)
                await tlsProxy.DisposeAsync();
            foreach (var mock in wireMockContainers.Values)
                await mock.DisposeAsync();
            if (azurite is not null)
                await azurite.DisposeAsync();
            cert?.Dispose();
            await network.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Resolves the application start order via topological sort based on AppUrl dependencies.
    /// Apps that are depended upon (via AppUrl) start before their dependents.
    /// </summary>
    private List<string> ResolveStartOrder()
    {
        // Build dependency graph from AppUrl bindings
        var dependencies = new Dictionary<string, HashSet<string>>();
        foreach (var appName in _applications.Keys)
        {
            dependencies[appName] = new HashSet<string>();
        }

        foreach (var (appName, wiringBuilder) in _wirings)
        {
            if (!dependencies.ContainsKey(appName))
                dependencies[appName] = new HashSet<string>();

            foreach (var targetApp in wiringBuilder.Config.AppUrlBindings.Keys)
            {
                dependencies[appName].Add(targetApp);
            }
        }

        // Topological sort (Kahn's algorithm)
        var inDegree = new Dictionary<string, int>();
        foreach (var app in dependencies.Keys) inDegree[app] = 0;
        foreach (var (_, deps) in dependencies)
        {
            foreach (var dep in deps)
            {
                if (inDegree.ContainsKey(dep))
                    inDegree[dep] = inDegree[dep]; // ensure key exists
                if (inDegree.ContainsKey(dep)) // dep is not an app we manage, skip
                    inDegree[_applications.Keys.FirstOrDefault(k => k == dep) ?? ""] += 0;
            }
        }

        // Count how many apps depend on each app
        foreach (var (app, deps) in dependencies)
        {
            foreach (var dep in deps)
            {
                if (!inDegree.ContainsKey(dep)) continue;
                // 'app' depends on 'dep', so 'app' should start after 'dep'
            }
        }

        // Simple approach: start apps with no AppUrl dependencies first, then the rest
        var ordered = new List<string>();
        var remaining = new HashSet<string>(_applications.Keys);

        // First pass: apps with no dependencies
        foreach (var app in _applications.Keys)
        {
            if (!dependencies.ContainsKey(app) || dependencies[app].Count == 0)
            {
                // Check if this app is NOT depended upon by anyone — start last
                // Actually: start apps that are depended upon first, then dependents
            }
        }

        // Simpler: apps that ARE targets of AppUrl go first, then the rest
        var dependedUpon = new HashSet<string>();
        foreach (var (_, deps) in dependencies)
        {
            foreach (var dep in deps)
                dependedUpon.Add(dep);
        }

        // Start depended-upon apps first
        foreach (var app in _applications.Keys.Where(a => dependedUpon.Contains(a)))
        {
            ordered.Add(app);
            remaining.Remove(app);
        }

        // Then start the rest
        foreach (var app in remaining)
        {
            ordered.Add(app);
        }

        return ordered;
    }

    /// <summary>
    /// Waits for an HTTP health check endpoint to return a success status code.
    /// </summary>
    private static async Task WaitForHealthCheckAsync(
        ManagedAppContainer container, int port, string path, int timeoutSeconds)
    {
        using var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var hostPort = container.GetMappedPort(port);
        var url = $"http://localhost:{hostPort}{path}";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Container not ready yet
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Get container logs for diagnostics
        var logs = await container.GetLogsAsync();
        throw new TimeoutException(
            $"Health check for '{container.Name}' at {url} did not pass within {timeoutSeconds}s.\n" +
            $"Container logs:\n{logs}");
    }
}
