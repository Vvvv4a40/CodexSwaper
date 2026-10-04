using System.ComponentModel;
using System.Diagnostics;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopShutdownIntegrationTests
{
    [Fact]
    public async Task CloseCodexAsync_HelperMissingFromFirstParentExitSnapshotIsFoundAndTerminatedNext()
    {
        using var fixture = new ShutdownFixture();
        var requests = new List<int>();
        bool omittedAfterDesktopExit = false;
        bool capturedAfterOmission = false;
        var service = fixture.CreateService(process =>
        {
            requests.Add(process.Identity.ProcessId);
            process.Terminate();
        }, rows =>
        {
            // The late helper is unobserved until its parent exits. The first empty
            // snapshot must not end shutdown before the next snapshot finds it.
            if (!fixture.Desktop.HasExited)
            {
                return rows.Where(row => row.Identity.ProcessId != fixture.Helper.Id).ToArray();
            }
            if (!omittedAfterDesktopExit)
            {
                Assert.False(fixture.Helper.HasExited);
                omittedAfterDesktopExit = true;
                return rows.Where(row => row.Identity.ProcessId != fixture.Helper.Id).ToArray();
            }
            if (rows.Any(row => row.Identity.ProcessId == fixture.Helper.Id))
            {
                capturedAfterOmission = true;
            }
            return rows;
        });

        await service.CloseCodexAsync(1, true, CancellationToken.None);
        service.VerifyDesktopStopped();

        Assert.True(omittedAfterDesktopExit);
        Assert.True(capturedAfterOmission);
        Assert.Equal(new int[] { fixture.Desktop.Id, fixture.Helper.Id }, requests.ToArray());
        Assert.True(fixture.Desktop.HasExited);
        Assert.True(fixture.Helper.HasExited);
    }

    [Fact]
    public async Task CloseCodexAsync_DeniedHelperIsRetriedAfterDesktopExitsAndAllTrackedProcessesFinish()
    {
        using var fixture = new ShutdownFixture();
        var requests = new List<int>();
        bool deniedOnce = false;
        var service = fixture.CreateService(process =>
        {
            requests.Add(process.Identity.ProcessId);
            if (process.Identity.ProcessId == fixture.Helper.Id && !deniedOnce)
            {
                deniedOnce = true;
                throw new Win32Exception(5, "Injected access denied for isolated fixture.");
            }
            process.Terminate();
        });

        await service.CloseCodexAsync(1, true, CancellationToken.None);
        service.VerifyDesktopStopped();

        Assert.Equal(fixture.Desktop.Id, requests[0]);
        Assert.True(deniedOnce);
        Assert.True(fixture.Desktop.HasExited);
        Assert.True(fixture.Helper.HasExited);
        string logs = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.LogDirectory)));
        Assert.Contains("continuing other selected processes", logs);
    }

    [Fact]
    public async Task CloseCodexAsync_DeniedHelperStillAlivePreventsAuthorizationStage()
    {
        using var fixture = new ShutdownFixture();
        var service = fixture.CreateService(process =>
        {
            if (process.Identity.ProcessId == fixture.Helper.Id) { throw new Win32Exception(5, "Injected persistent access denied."); }
            process.Terminate();
        });
        bool authorizationStageReached = false;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.CloseCodexAsync(1, true, CancellationToken.None);
            authorizationStageReached = true; // Only a marker, never any real authorization operation.
        });

        Assert.True(fixture.Desktop.HasExited);
        Assert.False(fixture.Helper.HasExited);
        Assert.False(authorizationStageReached);
        Assert.Contains(fixture.Helper.Id.ToString(), exception.Message);
        Assert.Contains("Authorization has not been changed", exception.Message);
    }

    [Fact]
    public async Task CloseCodexAsync_ForceDisabledDoesNotRequestTermination()
    {
        using var fixture = new ShutdownFixture();
        bool terminationRequested = false;
        var service = fixture.CreateService(_ => terminationRequested = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseCodexAsync(1, false, CancellationToken.None));
        Assert.False(terminationRequested);
        Assert.False(fixture.Desktop.HasExited);
        Assert.False(fixture.Helper.HasExited);
    }

    private sealed class ShutdownFixture : IDisposable
    {
        private readonly TempDirectory directory = new();
        private readonly DesktopProcessSnapshot desktopIdentity;
        private readonly DesktopProcessSnapshot helperIdentity;

        public ShutdownFixture()
        {
            Desktop = StartHiddenHelper();
            Helper = StartHiddenHelper();
            desktopIdentity = new DesktopProcessSnapshot(Desktop.Id, 0, Desktop.SessionId, Desktop.StartTime.ToUniversalTime());
            helperIdentity = new DesktopProcessSnapshot(Helper.Id, Desktop.Id, Helper.SessionId, Helper.StartTime.ToUniversalTime());
            LogDirectory = Path.Combine(directory.Path, "shutdown-test-logs");
        }

        public Process Desktop { get; }
        public Process Helper { get; }
        public string LogDirectory { get; }

        public CodexProcessService CreateService(Action<TrackedDesktopProcess> terminate,
            Func<IReadOnlyList<CapturedDesktopProcess>, IReadOnlyList<CapturedDesktopProcess>>? filterCapturedRows = null)
        {
            // Discover only the two processes this fixture started. Nothing enumerates or targets user applications.
            IReadOnlyList<CapturedDesktopProcess> Capture(int _)
            {
                var rows = new List<CapturedDesktopProcess>();
                if (!Desktop.HasExited) { rows.Add(new CapturedDesktopProcess(desktopIdentity, "ChatGPT.exe")); }
                if (!Helper.HasExited) { rows.Add(new CapturedDesktopProcess(helperIdentity, "owned-helper.exe")); }
                return filterCapturedRows?.Invoke(rows) ?? rows;
            }
            string installedFixturePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps", "OpenAI.Codex_test_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
            return new CodexProcessService(new SafeLogger(LogDirectory), Desktop.SessionId, Capture,
                () => Array.Empty<CodexWindowInfo>(),
                identity => identity.ProcessId == Desktop.Id ? installedFixturePath : null, terminate);
        }

        private static Process StartHiddenHelper()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[System.Threading.Thread]::Sleep(60000)");
            return Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned shutdown fixture.");
        }

        public void Dispose()
        {
            foreach (var process in new[] { Desktop, Helper })
            {
                try
                {
                    if (!process.HasExited) { process.Kill(); _ = process.WaitForExit(5000); }
                }
                finally { process.Dispose(); }
            }
            directory.Dispose();
        }
    }
}
