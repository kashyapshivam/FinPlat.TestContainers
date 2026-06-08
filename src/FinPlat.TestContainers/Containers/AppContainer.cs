using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Isolation;

namespace FinPlat.TestContainers.Containers;

/// <summary>
/// Manages an application container built from a Dockerfile or pulled from an image.
/// Injects environment variables for Azurite and WireMock connectivity.
/// </summary>
public class ManagedAppContainer : IAsyncDisposable
{
    private IContainer? _container;
    private IFutureDockerImage? _image;
    private IFutureDockerImage? _wrapperImage;

    private readonly string _name;
    private readonly string? _dockerfilePath;
    private readonly string _contextPath;
    private readonly string? _imageName;
    private readonly List<int> _exposedPorts;
    private readonly DebuggerSupportOptions? _debuggerSupport;
    private readonly InContainerDebugOptions? _inContainerDebug;
    private readonly string? _instanceId;

    /// <summary>
    /// Populated after <see cref="StartAsync"/> when the container was started for in-container
    /// debugging. <c>null</c> otherwise.
    /// </summary>
    public ContainerDebugInfo? DebugInfo { get; private set; }

    /// <summary>
    /// Creates a new managed application container.
    /// </summary>
    /// <param name="name">Container name and network alias.</param>
    /// <param name="dockerfilePath">Path to the Dockerfile, or null if using an image.</param>
    /// <param name="contextPath">Docker build context path.</param>
    /// <param name="imageName">Pre-built image name, or null if building from Dockerfile.</param>
    /// <param name="exposedPorts">Ports to expose from the container.</param>
    /// <param name="debuggerSupport">Optional vsdbg install options that layer a debug-enabled image on top of the user image.</param>
    /// <param name="inContainerDebug">Optional attach-time options (stable container name, ptrace, labels).</param>
    /// <param name="instanceId">Test-environment instance ID used for resource labels.</param>
    public ManagedAppContainer(
        string name,
        string? dockerfilePath,
        string contextPath,
        string? imageName,
        List<int> exposedPorts,
        DebuggerSupportOptions? debuggerSupport = null,
        InContainerDebugOptions? inContainerDebug = null,
        string? instanceId = null)
    {
        _name = name;
        _dockerfilePath = dockerfilePath;
        _contextPath = contextPath;
        _imageName = imageName;
        _exposedPorts = exposedPorts;
        _debuggerSupport = debuggerSupport;
        _inContainerDebug = inContainerDebug;
        _instanceId = instanceId;
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

        // If debugger support requested, layer vsdbg on top of the resolved image.
        if (_debuggerSupport is not null)
        {
            var (ctxDir, dfRelPath) = DebuggerImageBuilder.WriteWrapperDockerfile(resolvedImage, _debuggerSupport);
            _wrapperImage = new ImageFromDockerfileBuilder()
                .WithDockerfile(dfRelPath)
                .WithDockerfileDirectory(ctxDir)
                .WithName($"finplat-debug-{_name}:{Guid.NewGuid():N}")
                .Build();

            await _wrapperImage.CreateAsync();
            resolvedImage = _wrapperImage.FullName;
        }

        var builder = new ContainerBuilder()
            .WithImage(resolvedImage)
            .WithNetwork(network)
            .WithNetworkAliases(_name);

        // For in-container debugging: deterministic container name + ptrace cap + labels.
        string? explicitContainerName = null;
        if (_inContainerDebug is not null)
        {
            explicitContainerName = _inContainerDebug.ContainerNameOverride
                ?? (_inContainerDebug.UseStableContainerName ? $"finplat-debug-{_name}" : null);

            if (explicitContainerName is not null)
            {
                await PreCleanStaleDebugContainerAsync(explicitContainerName);
                builder = builder.WithName(explicitContainerName);
            }

            // vsdbg needs SYS_PTRACE; some kernels also need seccomp=unconfined for ptrace syscalls.
            builder = builder.WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                p.HostConfig.CapAdd ??= new List<string>();
                if (!p.HostConfig.CapAdd.Contains("SYS_PTRACE"))
                    p.HostConfig.CapAdd.Add("SYS_PTRACE");

                p.HostConfig.SecurityOpt ??= new List<string>();
                if (!p.HostConfig.SecurityOpt.Contains("seccomp=unconfined"))
                    p.HostConfig.SecurityOpt.Add("seccomp=unconfined");
            });

            // Tag for cleanup discovery.
            if (!string.IsNullOrWhiteSpace(_instanceId))
            {
                foreach (var (k, v) in ResourceIsolation.Labels.ForResource(_instanceId, "debug-app"))
                {
                    builder = builder.WithLabel(k, v);
                }
                builder = builder.WithLabel("finplat.slt.debug-app-name", _name);
            }
        }

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
            builder = builder.WithResourceMapping(
                Encoding.UTF8.GetBytes(cert.CertPem), "/certs/ca-cert.pem");
        }

        if (_exposedPorts.Count > 0)
        {
            builder = builder.WithWaitStrategy(
                Wait.ForUnixContainer().UntilPortIsAvailable(_exposedPorts[0]));
        }

        _container = builder.Build();
        await _container.StartAsync();

        // Populate debug info after container is up (so name + launch.json are accurate).
        if (_inContainerDebug is not null && explicitContainerName is not null)
        {
            DebugInfo = BuildDebugInfo(explicitContainerName);
            WriteAndPrintLaunchJson(DebugInfo);
        }
    }

    /// <summary>
    /// The name of this application container (also used as Docker network alias).
    /// </summary>
    public string Name => _name;

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

    /// <summary>
    /// Gets the host-mapped port for a given container port.
    /// </summary>
    /// <param name="containerPort">The internal container port.</param>
    /// <returns>The mapped host port.</returns>
    public ushort GetMappedPort(int containerPort)
    {
        if (_container is null)
            throw new InvalidOperationException($"Container '{_name}' has not been started.");

        return _container.GetMappedPublicPort(containerPort);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }

        if (_wrapperImage is not null)
        {
            await _wrapperImage.DisposeAsync();
            _wrapperImage = null;
        }

        if (_image is not null)
        {
            await _image.DisposeAsync();
            _image = null;
        }
    }

    /// <summary>
    /// Removes any leftover container with the given deterministic debug name so we can re-create it.
    /// Refuses to remove a container that is still running, to avoid clobbering an actively-attached
    /// debug session.
    /// </summary>
    private static async Task PreCleanStaleDebugContainerAsync(string containerName)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"inspect --format \"{{{{.State.Running}}}}\" {containerName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return;
        var stdout = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            // No such container — nothing to clean.
            return;
        }

        if (string.Equals(stdout, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to start debug container '{containerName}': a container with the same name is already running. " +
                "Stop it manually with `docker stop " + containerName + "` (and `docker rm " + containerName + "`) before retrying, " +
                "or call AttachableInDebugger(..., o => o.UseStableContainerName = false) to use random names.");
        }

        var rmPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"rm {containerName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var rmProc = System.Diagnostics.Process.Start(rmPsi);
        if (rmProc is not null) await rmProc.WaitForExitAsync();
    }

    private ContainerDebugInfo BuildDebugInfo(string containerName)
    {
        var sourceMap = new Dictionary<string, string>();
        if (_debuggerSupport is not null)
        {
            foreach (var (k, v) in _debuggerSupport.SourceFileMap) sourceMap[k] = v;
        }
        if (_inContainerDebug is not null)
        {
            foreach (var (k, v) in _inContainerDebug.AdditionalSourceFileMap) sourceMap[k] = v;
        }
        if (sourceMap.Count == 0)
        {
            sourceMap["/app"] = "${workspaceFolder}";
        }

        var sourceMapNode = new JsonObject();
        foreach (var (k, v) in sourceMap) sourceMapNode[k] = v;

        var launchJson = new JsonObject
        {
            ["name"] = $"Attach to {_name} (FinPlat container)",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processId"] = "${command:pickRemoteProcess}",
            ["pipeTransport"] = new JsonObject
            {
                ["pipeProgram"] = "docker",
                ["pipeArgs"] = new JsonArray("exec", "-i", "-u", "root", containerName),
                ["debuggerPath"] = "/vsdbg/vsdbg",
                ["quoteArgs"] = false,
            },
            ["sourceFileMap"] = sourceMapNode,
            ["justMyCode"] = true,
        };

        var serialized = launchJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var launchDir = Path.Combine(Path.GetTempPath(), "finplat-debug");
        Directory.CreateDirectory(launchDir);
        var launchPath = Path.Combine(launchDir, $"{_name}.launch.json");
        File.WriteAllText(launchPath, serialized);

        return new ContainerDebugInfo(
            AppName: _name,
            ContainerName: containerName,
            VsDbgPath: "/vsdbg/vsdbg",
            LaunchJsonSnippet: serialized,
            LaunchJsonFilePath: launchPath);
    }

    private static void WriteAndPrintLaunchJson(ContainerDebugInfo info)
    {
        Console.WriteLine();
        Console.WriteLine("========================================================================");
        Console.WriteLine($"[FinPlat.TestContainers] In-container debugger ready for '{info.AppName}'");
        Console.WriteLine($"  Container name : {info.ContainerName}");
        Console.WriteLine($"  vsdbg path     : {info.VsDbgPath}");
        Console.WriteLine($"  Launch.json    : {info.LaunchJsonFilePath}");
        Console.WriteLine("  Paste this into your VS Code launch.json under \"configurations\":");
        Console.WriteLine("------------------------------------------------------------------------");
        Console.WriteLine(info.LaunchJsonSnippet);
        Console.WriteLine("========================================================================");
        Console.WriteLine();
    }
}
