using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopProcessCatalogTests
{
    [Fact]
    public void ProcessEntry_HasWindowsX64ToolhelpLayout()
    {
        Assert.Equal(568, Marshal.SizeOf<ProcessEntry>());
    }
    [Fact]
    public void Capture_ReadsCurrentTestProcessAndFiltersOtherSessions()
    {
        using Process current = Process.GetCurrentProcess();
        var snapshot = DesktopProcessCatalog.Capture(current.SessionId);
        Assert.All(snapshot, item => Assert.Equal(current.SessionId, item.Identity.SessionId));
        var captured = Assert.Single(snapshot, item => item.Identity.ProcessId == current.Id);
        Assert.Equal(current.StartTime.ToUniversalTime(), captured.Identity.StartTimeUtc);
        Assert.Equal(current.MainModule?.FileName, DesktopProcessCatalog.ExecutablePath(captured.Identity), ignoreCase: true);
        using var pinned = DesktopProcessCatalog.Open(captured.Identity);
        Assert.NotNull(pinned);
        Assert.False(pinned.HasExited);
    }

    [Fact]
    public void Open_RejectsReusedProcessIdWithDifferentCreationTime()
    {
        using Process current = Process.GetCurrentProcess();
        var staleIdentity = new DesktopProcessSnapshot(current.Id, 0, current.SessionId, current.StartTime.ToUniversalTime().AddSeconds(-1));
        Assert.Null(DesktopProcessCatalog.Open(staleIdentity));
        Assert.Throws<InvalidOperationException>(() => DesktopProcessCatalog.ExecutablePath(staleIdentity));
    }

    [Fact]
    public void Open_FailsClosedWhenSnapshotCreationTimeIsUnknown()
    {
        using Process current = Process.GetCurrentProcess();
        var unknownIdentity = new DesktopProcessSnapshot(current.Id, 0, current.SessionId, null);
        Assert.Throws<InvalidOperationException>(() => DesktopProcessCatalog.Open(unknownIdentity));
    }

    [Theory]
    [InlineData("OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe", true)]
    [InlineData("OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "app", "Codex.exe", true)]
    [InlineData("Other.ChatGPT_1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe", false)]
    [InlineData("OpenAI.Codex_26.930.3930.0_x64__wrongpublisher", "app", "ChatGPT.exe", false)]
    [InlineData("OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "bin", "codex.exe", false)]
    [InlineData("OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "app", "codex-windows-sandbox-service.exe", false)]
    public void BackgroundFallback_RequiresInstalledDesktopPackageAndAppDirectory(string package, string directory, string executable, bool expected)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", package, directory, executable);
        Assert.Equal(expected, DesktopProcessCatalog.IsInstalledDesktopPath(path));
    }

    [Fact]
    public void BackgroundFallback_RejectsSameNamedExecutableOutsideInstalledPackageRoot()
    {
        Assert.False(DesktopProcessCatalog.IsInstalledDesktopPath(Path.Combine(Path.GetTempPath(), "WindowsApps", "OpenAI.Codex_1_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")));
    }
}
