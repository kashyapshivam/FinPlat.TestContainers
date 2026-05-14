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
}
