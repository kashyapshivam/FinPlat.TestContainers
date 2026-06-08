using System.Text.Json;
using FinPlat.TestContainers.Config;
using FinPlat.TestContainers.Containers;

namespace FinPlat.TestContainers.Tests;

[TestClass]
public sealed class DebuggerImageBuilderTests
{
    [TestMethod]
    public void BuildWrapperDockerfile_DebianBase_IncludesAptInstallAndVsDbg()
    {
        var content = DebuggerImageBuilder.BuildWrapperDockerfileContent(
            "myapp:abc123",
            new DebuggerSupportOptions());

        StringAssert.Contains(content, "FROM myapp:abc123");
        StringAssert.Contains(content, "USER root");
        StringAssert.Contains(content, "apt-get");
        StringAssert.Contains(content, "/vsdbg/vsdbg");
        StringAssert.Contains(content, "getvsdbgsh");
        StringAssert.Contains(content, "test -x /vsdbg/vsdbg");
    }

    [TestMethod]
    public void BuildWrapperDockerfile_AlpineBase_UsesApk()
    {
        var content = DebuggerImageBuilder.BuildWrapperDockerfileContent(
            "alpine-app:1.0",
            new DebuggerSupportOptions { BaseImageType = DebuggerBaseImageType.Alpine });

        StringAssert.Contains(content, "apk add");
        StringAssert.Contains(content, "/vsdbg/vsdbg");
    }

    [TestMethod]
    public void BuildWrapperDockerfile_RestoreUser_EmitsTrailingUserDirective()
    {
        var content = DebuggerImageBuilder.BuildWrapperDockerfileContent(
            "myapp:abc",
            new DebuggerSupportOptions { RestoreUser = "app" });

        var trailing = content.TrimEnd().Split('\n')[^1].Trim();
        Assert.AreEqual("USER app", trailing);
    }

    [TestMethod]
    public void BuildWrapperDockerfile_NoRestoreUser_StaysRoot()
    {
        var content = DebuggerImageBuilder.BuildWrapperDockerfileContent(
            "myapp:abc",
            new DebuggerSupportOptions());

        // No trailing USER directive when RestoreUser isn't set.
        Assert.DoesNotEndWith("USER app", content.TrimEnd());
    }

    [TestMethod]
    [DataRow("vs2022")]
    [DataRow("latest")]
    [DataRow("17.10.20509.1")]
    public void BuildWrapperDockerfile_ValidVersions_AreAccepted(string version)
    {
        var content = DebuggerImageBuilder.BuildWrapperDockerfileContent(
            "myapp:abc",
            new DebuggerSupportOptions { VsDbgVersion = version });

        StringAssert.Contains(content, $"-v {version}");
    }

    [TestMethod]
    [DataRow("vs2022; rm -rf /")]
    [DataRow("vs2022 && echo bad")]
    [DataRow("vs2022 $(whoami)")]
    [DataRow("vs2022`whoami`")]
    public void BuildWrapperDockerfile_InjectionVersions_AreRejected(string evilVersion)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            DebuggerImageBuilder.BuildWrapperDockerfileContent(
                "myapp:abc",
                new DebuggerSupportOptions { VsDbgVersion = evilVersion }));
    }
}
