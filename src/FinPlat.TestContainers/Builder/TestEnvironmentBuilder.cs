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
    /// 5. Starts application containers with injected environment variables.
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

            // 5. Build and start application containers
            var mockApiInternalUrls = new Dictionary<string, string>();
            foreach (var (name, container) in wireMockContainers)
            {
                mockApiInternalUrls[name] = container.InternalUrl;
            }

            foreach (var (appName, appBuilder) in _applications)
            {
                // Build environment variables from wiring + explicit env vars
                var envVars = new Dictionary<string, string>(appBuilder.EnvironmentVariables);

                if (_wirings.TryGetValue(appName, out var wiringBuilder))
                {
                    var injectedVars = ConfigInjector.BuildEnvVars(
                        wiringBuilder.Config,
                        azurite?.InternalConnectionString,
                        mockApiInternalUrls,
                        _azuriteOptions);

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
}
