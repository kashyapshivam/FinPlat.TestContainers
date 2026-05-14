using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using FinPlat.TestContainers.Config;

namespace FinPlat.TestContainers.Containers;

/// <summary>
/// Manages an application container built from a Dockerfile or pulled from an image.
/// Injects environment variables for Azurite and WireMock connectivity.
/// </summary>
public class ManagedAppContainer : IAsyncDisposable
{
    private IContainer? _container;
    private IFutureDockerImage? _image;

    private readonly string _name;
    private readonly string? _dockerfilePath;
    private readonly string _contextPath;
    private readonly string? _imageName;
    private readonly List<int> _exposedPorts;

    /// <summary>
    /// Creates a new managed application container.
    /// </summary>
    /// <param name="name">Container name and network alias.</param>
    /// <param name="dockerfilePath">Path to the Dockerfile, or null if using an image.</param>
    /// <param name="contextPath">Docker build context path.</param>
    /// <param name="imageName">Pre-built image name, or null if building from Dockerfile.</param>
    /// <param name="exposedPorts">Ports to expose from the container.</param>
    public ManagedAppContainer(
        string name,
        string? dockerfilePath,
        string contextPath,
        string? imageName,
        List<int> exposedPorts)
    {
        _name = name;
        _dockerfilePath = dockerfilePath;
        _contextPath = contextPath;
        _imageName = imageName;
        _exposedPorts = exposedPorts;
    }

    /// <summary>
    /// Starts the application container with the given environment variables on the specified network.
    /// </summary>
    /// <param name="network">The shared Docker network.</param>
    /// <param name="envVars">Environment variables to inject into the container.</param>
    /// <param name="cert">Optional certificate to mount for TLS trust (token auth mode).</param>
    public async Task StartAsync(INetwork network, Dictionary<string, string> envVars, CertificateMaterial? cert = null)
    {
        string resolvedImage;

        if (_dockerfilePath is not null)
        {
            _image = new ImageFromDockerfileBuilder()
                .WithDockerfile(_dockerfilePath)
                .WithDockerfileDirectory(_contextPath)
                .WithName($"finplat-test-{_name}:{Guid.NewGuid():N}")
                .Build();

            await _image.CreateAsync();
            resolvedImage = _image.FullName;
        }
        else if (_imageName is not null)
        {
            resolvedImage = _imageName;
        }
        else
        {
            throw new InvalidOperationException(
                $"Application '{_name}' must have either a Dockerfile or image configured.");
        }

        var builder = new ContainerBuilder()
            .WithImage(resolvedImage)
            .WithNetwork(network)
            .WithNetworkAliases(_name);

        foreach (var (key, value) in envVars)
        {
            builder = builder.WithEnvironment(key, value);
        }

        foreach (var port in _exposedPorts)
        {
            builder = builder.WithPortBinding(port, true);
        }

        // Mount certificate for TLS trust if provided
        if (cert is not null)
        {
            var (certPath, _) = cert.WriteTempFiles();
            builder = builder.WithResourceMapping(certPath, "/certs/ca-cert.pem");
        }

        if (_exposedPorts.Count > 0)
        {
            builder = builder.WithWaitStrategy(
                Wait.ForUnixContainer().UntilPortIsAvailable(_exposedPorts[0]));
        }

        _container = builder.Build();
        await _container.StartAsync();
    }

    /// <summary>
    /// Retrieves the stdout/stderr logs from the application container.
    /// </summary>
    /// <returns>The container logs as a string.</returns>
    public async Task<string> GetLogsAsync()
    {
        if (_container is null)
            return string.Empty;

        var (stdout, stderr) = await _container.GetLogsAsync();
        return $"{stdout}\n{stderr}";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }

        if (_image is not null)
        {
            await _image.DisposeAsync();
            _image = null;
        }
    }
}
