using System.Collections.Generic;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Options controlling how vsdbg (the .NET CLI debugger) is layered onto an application
/// container image for in-container debugging via VS Code / Visual Studio attach.
/// </summary>
public class DebuggerSupportOptions
{
    /// <summary>
    /// Version selector passed to <c>getvsdbgsh -v</c>. Defaults to <c>"vs2022"</c>
    /// which the install script resolves to the latest vsdbg compatible with
    /// Visual Studio 2022 / VS Code (a well-pinned, stable channel — not bleeding edge).
    /// Use <c>"latest"</c> for newest, or a specific build number for full reproducibility.
    /// Build-time internet access is required either way.
    /// </summary>
    public string VsDbgVersion { get; set; } = "vs2022";

    /// <summary>
    /// Base image OS family for choosing the install commands.
    /// <see cref="DebuggerBaseImageType.DebianOrUbuntu"/> uses <c>apt-get</c>;
    /// <see cref="DebuggerBaseImageType.Alpine"/> uses <c>apk</c>.
    /// Distroless / scratch images are unsupported.
    /// </summary>
    public DebuggerBaseImageType BaseImageType { get; set; } = DebuggerBaseImageType.DebianOrUbuntu;

    /// <summary>
    /// Source-file mapping that will be emitted in the generated launch.json.
    /// Keys are paths inside the container (as embedded in PDB), values are local paths
    /// on the developer's machine. Defaults to <c>/app -&gt; ${workspaceFolder}</c>;
    /// override per-app when PDBs reference a different container path.
    /// </summary>
    public Dictionary<string, string> SourceFileMap { get; } = new();

    /// <summary>
    /// Optional username to switch back to via <c>USER &lt;name&gt;</c> at the end of the
    /// wrapper Dockerfile. If null (default), the debug image runs as <c>root</c> —
    /// acceptable for local debug images. Set to e.g. <c>"app"</c> when the base image's
    /// runtime depends on a non-root user existing. The user must already exist in the
    /// base image; the wrapper does not create it.
    /// </summary>
    public string? RestoreUser { get; set; }
}

/// <summary>
/// Base image OS family used to pick the right install commands when adding vsdbg.
/// </summary>
public enum DebuggerBaseImageType
{
    /// <summary>Debian or Ubuntu based image. Uses <c>apt-get install</c>.</summary>
    DebianOrUbuntu,

    /// <summary>Alpine Linux based image. Uses <c>apk add</c>.</summary>
    Alpine,
}
