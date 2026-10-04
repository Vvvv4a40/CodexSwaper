using System.ComponentModel;
using System.Diagnostics;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopNativeTerminationTests
{
    [Fact]
    public void Terminate_StopsOnlyAnOwnedHiddenTestHelperAndAllowsRepeatedRequests()
    {
        using var helper = new HiddenTestHelper();
        using var pinned = DesktopProcessCatalog.Open(helper.Identity, "owned test helper");
        Assert.NotNull(pinned);
        Assert.False(pinned.HasExited);
        pinned.Terminate();
        pinned.Terminate();
        Assert.True(helper.Process.WaitForExit(5000));
        Assert.True(pinned.HasExited);
        Assert.NotNull(pinned.ExitTimeUtc);
        pinned.Terminate();
    }

    [Fact]
    public void Batch_AccessDeniedOnOneHelperDoesNotPreventAnotherOwnedHelperFromExiting()
    {
        using var desktop = new HiddenTestHelper();
        using var deniedHelper = new HiddenTestHelper();
        using var pinnedDesktop = DesktopProcessCatalog.Open(desktop.Identity, "owned desktop fixture");
        using var pinnedDenied = DesktopProcessCatalog.Open(deniedHelper.Identity, "owned denied fixture");
        Assert.NotNull(pinnedDesktop);
        Assert.NotNull(pinnedDenied);
        var failedPids = new List<int>();

        // Inject access denied for our own fixture; do not manipulate ACLs or protected system processes.
        DesktopShutdownBatch.RequestTermination(new[] { deniedHelper.Identity, desktop.Identity },
            new HashSet<int> { desktop.Identity.ProcessId },
            identity =>
            {
                if (identity.ProcessId == deniedHelper.Identity.ProcessId) { throw new Win32Exception(5); }
                pinnedDesktop.Terminate();
            },
            (identity, _) => failedPids.Add(identity.ProcessId));

        Assert.Equal(new[] { deniedHelper.Identity.ProcessId }, failedPids);
        Assert.True(desktop.Process.WaitForExit(5000));
        Assert.True(pinnedDesktop.HasExited);
        Assert.False(pinnedDenied.HasExited); // A request batch is not proof of complete shutdown.
    }

    private sealed class HiddenTestHelper : IDisposable
    {
        public HiddenTestHelper()
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var start = new ProcessStartInfo(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[System.Threading.Thread]::Sleep(60000)");
            Process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated test helper.");
            Identity = new DesktopProcessSnapshot(Process.Id, Environment.ProcessId, Process.SessionId, Process.StartTime.ToUniversalTime());
        }

        public Process Process { get; }
        public DesktopProcessSnapshot Identity { get; }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(); // Only the helper created by this fixture.
                    _ = Process.WaitForExit(5000);
                }
            }
            finally { Process.Dispose(); }
        }
    }
}
