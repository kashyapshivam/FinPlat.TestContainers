using System.Collections.Generic;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Options for marking an application as debugger-attachable via the in-container
/// (<c>docker exec vsdbg</c>) debugging flow.
/// </summary>
public class InContainerDebugOptions
{
    /// <summary>
    /// When true (default), the library pins the container to a deterministic name
    /// (<c>finplat-debug-&lt;appName&gt;</c>) so a developer's saved
    /// <c>launch.json</c> entry keeps working across test runs.
    /// Disable for parallel test execution; you'll need to re-paste the launch.json
    /// each run because the container name will be random.
    /// </summary>
    public bool UseStableContainerName { get; set; } = true;

    /// <summary>
    /// Explicit container name override. Wins over <see cref="UseStableContainerName"/>.
    /// Use this to disambiguate parallel debug sessions (e.g.,
    /// <c>"finplat-debug-fo-worker-shiv"</c>).
    /// </summary>
    public string? ContainerNameOverride { get; set; }

    /// <summary>
    /// Extra source-file map entries to include in the generated launch.json
    /// (in addition to whatever the app-level <see cref="DebuggerSupportOptions.SourceFileMap"/>
    /// declares). Useful when multiple repos contribute source to one image.
    /// </summary>
    public Dictionary<string, string> AdditionalSourceFileMap { get; } = new();
}

/// <summary>
/// Runtime information exposed by <see cref="TestEnvironment.GetContainerDebugInfo"/>
/// so test authors / users know exactly how to attach a debugger to a running
/// application container.
/// </summary>
/// <param name="AppName">The application name as registered in the builder.</param>
/// <param name="ContainerName">
/// The actual Docker container name (deterministic when
/// <see cref="InContainerDebugOptions.UseStableContainerName"/> is on, random otherwise).
/// </param>
/// <param name="VsDbgPath">
/// Absolute path to <c>vsdbg</c> inside the container, normally <c>/vsdbg/vsdbg</c>.
/// </param>
/// <param name="LaunchJsonSnippet">
/// A ready-to-paste, strict-JSON launch.json configuration entry for VS Code /
/// Visual Studio Code coreclr attach.
/// </param>
/// <param name="LaunchJsonFilePath">
/// Filesystem path the launch.json snippet was also written to (for convenience).
/// </param>
public record ContainerDebugInfo(
    string AppName,
    string ContainerName,
    string VsDbgPath,
    string LaunchJsonSnippet,
    string LaunchJsonFilePath);
