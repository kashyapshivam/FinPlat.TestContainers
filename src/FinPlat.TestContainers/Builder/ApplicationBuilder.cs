using System;
using System.Collections.Generic;

namespace FinPlat.TestContainers.Builder;

/// <summary>
/// Configures how an application container is built and started.
/// Supports building from a Dockerfile or pulling an existing image.
/// </summary>
public class ApplicationBuilder
{
    internal string? DockerfilePath { get; private set; }
    internal string ContextPath { get; private set; } = ".";
    internal string? ImageName { get; private set; }
    internal Dictionary<string, string> EnvironmentVariables { get; } = new();
    internal List<int> ExposedPorts { get; } = new();
    internal int? InternalServicePort { get; private set; }
    internal string? HealthCheckPath { get; private set; }
    internal int? HealthCheckPort { get; private set; }
    internal int HealthCheckTimeoutSeconds { get; private set; } = 60;

    /// <summary>
    /// Builds the application container from a Dockerfile.
    /// </summary>
    /// <param name="dockerfilePath">Path to the Dockerfile relative to context.</param>
    /// <param name="contextPath">Docker build context path. Defaults to current directory.</param>
    public ApplicationBuilder FromDockerfile(string dockerfilePath, string contextPath = ".")
    {
        DockerfilePath = dockerfilePath;
        ContextPath = contextPath;
        ImageName = null;
        return this;
    }

    /// <summary>
    /// Uses a pre-built Docker image for the application container.
    /// </summary>
    /// <param name="imageName">The fully qualified image name (e.g., "myapp:latest").</param>
    public ApplicationBuilder FromImage(string imageName)
    {
        ImageName = imageName;
        DockerfilePath = null;
        return this;
    }

    /// <summary>
    /// Adds an environment variable to the application container.
    /// </summary>
    /// <param name="key">Environment variable name.</param>
    /// <param name="value">Environment variable value.</param>
    public ApplicationBuilder WithEnv(string key, string value)
    {
        EnvironmentVariables[key] = value;
        return this;
    }

    /// <summary>
    /// Exposes a container port for external access.
    /// </summary>
    /// <param name="containerPort">The port inside the container to expose.</param>
    public ApplicationBuilder WithPort(int containerPort)
    {
        ExposedPorts.Add(containerPort);
        return this;
    }

    /// <summary>
    /// Declares the internal service port for app-to-app communication.
    /// Used by <see cref="WiringBuilder.AppUrl"/> to construct the internal URL.
    /// </summary>
    /// <param name="port">The port the application listens on inside the container.</param>
    public ApplicationBuilder WithInternalPort(int port)
    {
        InternalServicePort = port;
        if (!ExposedPorts.Contains(port))
            ExposedPorts.Add(port);
        return this;
    }

    /// <summary>
    /// Declares an HTTP health check endpoint. The builder will wait for a 200 response
    /// from this endpoint before considering the application ready.
    /// </summary>
    /// <param name="path">Health check URL path (e.g., "/v1/ping").</param>
    /// <param name="port">Port to check. Defaults to <see cref="InternalServicePort"/>.</param>
    /// <param name="timeoutSeconds">Maximum time to wait for readiness. Default is 60 seconds.</param>
    public ApplicationBuilder WithHttpHealthCheck(string path, int? port = null, int timeoutSeconds = 60)
    {
        HealthCheckPath = path;
        HealthCheckPort = port;
        HealthCheckTimeoutSeconds = timeoutSeconds;
        return this;
    }
}
