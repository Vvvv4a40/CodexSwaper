using System.ComponentModel;
using System.Diagnostics;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopShutdownIntegrationTests
{
    [Fact]
    public async Task StoppedDesktopOperation_RollbackRunsOnlyAfterNewDesktopAndHelpersExit()
    {
        using var fixture = new ShutdownFixture();
        var service = fixture.CreateService(process => process.Terminate());
        bool restored = false;
        await StoppedDesktopOperation.RunAsync(service, 1, true, () =>
        {
            Assert.True(fixture.Desktop.HasExited);
            Assert.True(fixture.Helper.HasExited);
            restored = true;
        }, CancellationToken.None);
        Assert.True(restored);
    }

    [Fact]
    public async Task StoppedDesktopOperation_SurvivingHelperPreventsRollbackMutation()
    {
        using var fixture = new ShutdownFixture();
        var service = fixture.CreateService(process =>
        {
            if (process.Identity.ProcessId == fixture.Helper.Id) { throw new Win32Exception(5); }
            process.Terminate();
        });
        bool restored = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoppedDesktopOperation.RunAsync(service, 1, true,
            () => restored = true, CancellationToken.None));
        Assert.False(restored);
        Assert.False(fixture.Helper.HasExited);
    }

    [Fact]
    public async Task VerifyDesktopStopped_DisposedGuardCannotAuthorizeEvenAnEmptySnapshot()
    {
        using var fixture = new ShutdownFixture();
        var service = fixture.CreateService(process => process.Terminate());
        using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        guard.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.VerifyDesktopStopped(guard));
    }

    [Fact]
    public async Task VerifyDesktopStopped_NewUntrackedDesktopRemainsABlockingCandidate()
    {
        using var fixture = new ShutdownFixture();
        bool verificationPhase = false;
        var service = fixture.CreateService(process => process.Terminate(), rows =>
        {
            if (!verificationPhase) { return rows.Where(row => row.Identity.ProcessId == fixture.Desktop.Id).ToArray(); }
            return rows.Select(row => row with { ExecutableName = "ChatGPT.exe" }).ToArray();
        }, _ => fixture.InstalledDesktopPath);
        using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        verificationPhase = true;

        Assert.Throws<InvalidOperationException>(() => service.VerifyDesktopStopped(guard));
        Assert.False(fixture.Helper.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseCodexAsync_StaleExitedDesktopSnapshotDoesNotRequeryExecutablePath(bool unknownCreationTime)
    {
        using var fixture = new ShutdownFixture();
        var requests = new List<int>();
        bool capturedStaleDesktop = false;
        int executableQueriesAfterExit = 0;
        var service = fixture.CreateService(process =>
        {
            requests.Add(process.Identity.ProcessId);
            process.Terminate();
        }, rows =>
        {
            if (!fixture.Desktop.HasExited) { return rows; }
            capturedStaleDesktop = true;
            var staleIdentity = fixture.DesktopIdentity with
            {
                StartTimeUtc = unknownCreationTime ? null : fixture.DesktopIdentity.StartTimeUtc,
            };
            return rows.Append(new CapturedDesktopProcess(staleIdentity, "ChatGPT.exe")).ToArray();
        }, identity =>
        {
            if (identity.ProcessId != fixture.Desktop.Id) { return null; }
            if (fixture.Desktop.HasExited)
            {
                executableQueriesAfterExit++;
                throw new Win32Exception(5, "Injected access denied for stale exited desktop snapshot.");
            }
            return fixture.InstalledDesktopPath;
        });

        using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        service.VerifyDesktopStopped(guard);

        Assert.True(capturedStaleDesktop);
        Assert.Equal(0, executableQueriesAfterExit);
        Assert.Equal(new int[] { fixture.Desktop.Id, fixture.Helper.Id }, requests.ToArray());
        Assert.True(fixture.Desktop.HasExited);
        Assert.True(fixture.Helper.HasExited);
    }

    [Fact]
    public async Task CloseCodexAsync_TrackedPidWithChangedKnownCreationTimeRefusesShutdown()
    {
        using var fixture = new ShutdownFixture();
        int captureCount = 0;
        bool changedIdentityCaptured = false;
        bool terminationRequested = false;
        var service = fixture.CreateService(_ => terminationRequested = true, rows =>
        {
            if (++captureCount < 2) { return rows; }
            changedIdentityCaptured = true;
            return rows.Select(row => row.Identity.ProcessId == fixture.Desktop.Id
                ? row with { Identity = row.Identity with { StartTimeUtc = fixture.DesktopIdentity.StartTimeUtc!.Value.AddTicks(1) } }
                : row).ToArray();
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        });

        Assert.True(changedIdentityCaptured);
        Assert.False(terminationRequested);
        Assert.False(fixture.Desktop.HasExited);
        Assert.False(fixture.Helper.HasExited);
    }

    [Fact]
    public async Task CloseCodexAsync_TrackedLivePidWithUnknownCreationTimeRefusesShutdown()
    {
        using var fixture = new ShutdownFixture();
        int captureCount = 0;
        bool unknownIdentityCaptured = false;
        bool terminationRequested = false;
        var service = fixture.CreateService(_ => terminationRequested = true, rows =>
        {
            if (++captureCount < 2) { return rows; }
            unknownIdentityCaptured = true;
            return rows.Select(row => row.Identity.ProcessId == fixture.Desktop.Id
                ? row with { Identity = row.Identity with { StartTimeUtc = null } }
                : row).ToArray();
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        });

        Assert.True(unknownIdentityCaptured);
        Assert.False(terminationRequested);
        Assert.False(fixture.Desktop.HasExited);
        Assert.False(fixture.Helper.HasExited);
    }

    [Fact]
    public async Task CloseCodexAsync_UntrackedDesktopCandidateAccessDeniedIsNotIgnored()
    {
        using var fixture = new ShutdownFixture();
        bool terminationRequested = false;
        var denied = new Win32Exception(5, "Injected access denied for untracked desktop candidate.");
        var service = fixture.CreateService(_ => terminationRequested = true, executablePath: identity =>
            identity.ProcessId == fixture.Desktop.Id ? throw denied : null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        });

        Assert.Same(denied, exception.InnerException);
        Assert.False(terminationRequested);
        Assert.False(fixture.Desktop.HasExited);
        Assert.False(fixture.Helper.HasExited);
    }

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

        using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        service.VerifyDesktopStopped(guard);

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

        using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
        service.VerifyDesktopStopped(guard);

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
            using var guard = await service.CloseCodexAsync(1, true, CancellationToken.None);
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
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var guard = await service.CloseCodexAsync(1, false, CancellationToken.None);
        });
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
        public DesktopProcessSnapshot DesktopIdentity => desktopIdentity;
        public string InstalledDesktopPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps", "OpenAI.Codex_test_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");

        public CodexProcessService CreateService(Action<TrackedDesktopProcess> terminate,
            Func<IReadOnlyList<CapturedDesktopProcess>, IReadOnlyList<CapturedDesktopProcess>>? filterCapturedRows = null,
            Func<DesktopProcessSnapshot, string?>? executablePath = null)
        {
            // Discover only the two processes this fixture started. Nothing enumerates or targets user applications.
            IReadOnlyList<CapturedDesktopProcess> Capture(int _)
            {
                var rows = new List<CapturedDesktopProcess>();
                if (!Desktop.HasExited) { rows.Add(new CapturedDesktopProcess(desktopIdentity, "ChatGPT.exe")); }
                if (!Helper.HasExited) { rows.Add(new CapturedDesktopProcess(helperIdentity, "owned-helper.exe")); }
                return filterCapturedRows?.Invoke(rows) ?? rows;
            }
            return new CodexProcessService(new SafeLogger(LogDirectory), Desktop.SessionId, Capture,
                () => Array.Empty<CodexWindowInfo>(),
                executablePath ?? (identity => identity.ProcessId == Desktop.Id ? InstalledDesktopPath : null), terminate);
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
