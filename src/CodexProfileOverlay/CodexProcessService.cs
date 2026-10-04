using System.Diagnostics;
using System.IO;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class CodexProcessService
{
    private readonly SafeLogger logger;
    private readonly Func<int, IReadOnlyList<CapturedDesktopProcess>> captureProcesses;
    private readonly Func<IReadOnlyList<CodexWindowInfo>> findWindows;
    private readonly Func<DesktopProcessSnapshot, string?> executablePath;
    private readonly Action<TrackedDesktopProcess> terminateProcess;
    private readonly HashSet<string> verifiedDesktopPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly int sessionId;

    public CodexProcessService(SafeLogger logger)
        : this(logger, CurrentSessionId(), DesktopProcessCatalog.Capture,
            new CodexWindowFinder(logger).FindMainWindows, DesktopProcessCatalog.ExecutablePath,
            process => process.Terminate())
    {
    }

    // The same shutdown path is exercised with isolated process fixtures; production always uses the native catalog.
    internal CodexProcessService(SafeLogger logger, int sessionId,
        Func<int, IReadOnlyList<CapturedDesktopProcess>> captureProcesses,
        Func<IReadOnlyList<CodexWindowInfo>> findWindows,
        Func<DesktopProcessSnapshot, string?> executablePath,
        Action<TrackedDesktopProcess> terminateProcess)
    {
        this.logger = logger;
        this.sessionId = sessionId;
        this.captureProcesses = captureProcesses;
        this.findWindows = findWindows;
        this.executablePath = executablePath;
        this.terminateProcess = terminateProcess;
    }

    private static int CurrentSessionId()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        return currentProcess.SessionId;
    }

    public void ObserveDesktopWindow(CodexWindowInfo window)
    {
        try
        {
            string? path = executablePath(new DesktopProcessSnapshot(window.ProcessId, 0, sessionId, window.StartTimeUtc));
            if (!string.IsNullOrWhiteSpace(path)) { verifiedDesktopPaths.Add(Path.GetFullPath(path)); }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Error($"Could not remember desktop executable for PID {window.ProcessId}.", exception);
        }
    }

    public async Task CloseCodexAsync(int gracefulTimeoutSeconds, bool allowForceClose, CancellationToken cancellationToken)
    {
        var tracked = new Dictionary<int, TrackedDesktopProcess>();
        var desktopProcessIds = new HashSet<int>();
        var reportedFailures = new HashSet<(int ProcessId, string Message)>();
        var closeRequests = new HashSet<(IntPtr Window, DateTime Started)>();
        try
        {
            bool RefreshAndRequestClose()
            {
                var captured = captureProcesses(sessionId);
                var windows = findWindows();
                var roots = new HashSet<int>();
                foreach (var window in windows)
                {
                    if (captured.Any(item => item.Identity.ProcessId == window.ProcessId && item.Identity.StartTimeUtc == window.StartTimeUtc))
                    {
                        roots.Add(window.ProcessId);
                    }
                    ObserveDesktopWindow(window);
                }
                foreach (var process in captured)
                {
                    // The service and standalone CLI never become roots through a name substring.
                    string file = process.ExecutableName;
                    bool candidate = file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                        || file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)
                        || verifiedDesktopPaths.Any(path => Path.GetFileName(path).Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (!candidate || process.Identity.ProcessId == Environment.ProcessId) { continue; }
                    try
                    {
                        string? path = executablePath(process.Identity);
                        if (path is not null && (verifiedDesktopPaths.Contains(Path.GetFullPath(path)) || DesktopProcessCatalog.IsInstalledDesktopPath(path)))
                        {
                            roots.Add(process.Identity.ProcessId);
                        }
                    }
                    catch (ArgumentException) { } // The candidate exited during inspection.
                    catch (System.ComponentModel.Win32Exception exception)
                    {
                        throw new InvalidOperationException($"Cannot verify desktop candidate PID {process.Identity.ProcessId}. Close it normally before switching.", exception);
                    }
                }
                desktopProcessIds.UnionWith(roots);
                // Restore only the exact remembered identities; a recycled PID fails before any shutdown action.
                var remembered = tracked.Values.Select(process => process.Identity with { ExitTimeUtc = process.ExitTimeUtc }).ToArray();
                var capturedIdentities = DesktopProcessSelector.RestoreLineage(captured.Select(item => item.Identity), remembered);
                roots.UnionWith(remembered.Select(identity => identity.ProcessId));
                var selected = DesktopProcessSelector.SelectTree(capturedIdentities, roots, sessionId, Environment.ProcessId);
                bool retrySnapshot = windows.Any(window => !captured.Any(item => item.Identity.ProcessId == window.ProcessId
                    && item.Identity.StartTimeUtc == window.StartTimeUtc));
                foreach (var identity in selected)
                {
                    if (identity.ExitTimeUtc is not null || !captured.Any(item => item.Identity.ProcessId == identity.ProcessId)) { continue; } // Lineage only.
                    if (tracked.TryGetValue(identity.ProcessId, out var previous) && previous.Identity.StartTimeUtc != identity.StartTimeUtc)
                    {
                        throw new InvalidOperationException("Desktop identity changed while shutdown was in progress.");
                    }
                    if (!tracked.ContainsKey(identity.ProcessId))
                    {
                        string executableName = captured.First(item => item.Identity.ProcessId == identity.ProcessId).ExecutableName;
                        var process = DesktopProcessCatalog.Open(identity, executableName);
                        if (process is not null)
                        {
                            tracked.Add(identity.ProcessId, process);
                            logger.Info($"Tracking desktop process {identity.ProcessId} ({executableName}), parent {identity.ParentProcessId}, session {sessionId}, role {(desktopProcessIds.Contains(identity.ProcessId) ? "desktop" : "helper")}.");
                        }
                        else { retrySnapshot = true; }
                    }
                }
                foreach (var window in windows)
                {
                    if (!tracked.TryGetValue(window.ProcessId, out var process) || process.Identity.StartTimeUtc != window.StartTimeUtc)
                    {
                        retrySnapshot = true;
                        continue;
                    }
                    if (!closeRequests.Add((window.Hwnd, window.StartTimeUtc))) { continue; }
                    try
                    {
                        logger.Info($"Requesting desktop window close for process {window.ProcessId} ({window.ProcessName}).");
                        process.RequestClose(window.Hwnd);
                    }
                    catch (System.ComponentModel.Win32Exception exception)
                    {
                        logger.Error($"Failed to request desktop close for PID {window.ProcessId}.", exception);
                    }
                }
                return retrySnapshot || tracked.Values.Any(process => !process.HasExited);
            }

            async Task<bool> RefreshAndConfirmExitAsync()
            {
                if (RefreshAndRequestClose()) { return true; }
                // Capture again after observing exit: a parent may spawn its last helper between capture and exit.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return RefreshAndRequestClose();
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(gracefulTimeoutSeconds, 1, 60));
            while (await RefreshAndConfirmExitAsync().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow >= deadline) { break; }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            if (!await RefreshAndConfirmExitAsync().ConfigureAwait(false)) { return; }
            if (!allowForceClose)
            {
                throw new InvalidOperationException("Codex Desktop is still running. Authorization has not been changed.");
            }
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            void RequestTermination(IEnumerable<TrackedDesktopProcess> processes)
            {
                DesktopShutdownBatch.RequestTermination(processes.Select(process => process.Identity), desktopProcessIds,
                    identity =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var process = tracked[identity.ProcessId];
                        if (process.HasExited || process.TerminationRequested) { return; }
                        logger.Info($"Terminating selected desktop process {identity.ProcessId} ({process.ExecutableName}), role {(desktopProcessIds.Contains(identity.ProcessId) ? "desktop" : "helper")}, after graceful timeout.");
                        terminateProcess(process);
                    },
                    (identity, exception) =>
                    {
                        if (reportedFailures.Add((identity.ProcessId, exception.Message)))
                        {
                            logger.Error($"Shutdown request failed for PID {identity.ProcessId} ({tracked[identity.ProcessId].ExecutableName}); continuing other selected processes.", exception);
                        }
                    });
            }
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Stop the desktop parent before helpers: a live Electron parent can respawn terminated children.
                RequestTermination(tracked.Values.Where(process => desktopProcessIds.Contains(process.Identity.ProcessId)));
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (!await RefreshAndConfirmExitAsync().ConfigureAwait(false))
                {
                    logger.Info("All selected desktop processes exited before authorization switching.");
                    return;
                }
                RequestTermination(tracked.Values);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (!await RefreshAndConfirmExitAsync().ConfigureAwait(false)) { return; }
            } while (DateTimeOffset.UtcNow < deadline);
            var remaining = tracked.Values.Where(process => !process.HasExited).Select(process => $"{process.Identity.ProcessId} ({process.ExecutableName})").ToArray();
            string remainingDescription = remaining.Length == 0 ? "a changing desktop snapshot" : string.Join(", ", remaining);
            throw new InvalidOperationException($"Selected Codex Desktop processes did not exit: {remainingDescription}. Authorization has not been changed.");
        }
        finally
        {
            foreach (var process in tracked.Values) { process.Dispose(); }
        }
    }

    public void VerifyDesktopStopped()
    {
        var captured = captureProcesses(sessionId);
        if (findWindows().Count > 0)
        {
            throw new InvalidOperationException("Codex Desktop reopened before switching. Authorization has not been changed.");
        }
        foreach (var process in captured)
        {
            string file = process.ExecutableName;
            if (!file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) && !file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)
                && !verifiedDesktopPaths.Any(path => Path.GetFileName(path).Equals(file, StringComparison.OrdinalIgnoreCase))) { continue; }
            string? path = executablePath(process.Identity);
            if (path is not null && (verifiedDesktopPaths.Contains(Path.GetFullPath(path)) || DesktopProcessCatalog.IsInstalledDesktopPath(path)))
            {
                throw new InvalidOperationException("Codex Desktop is running before switching. Authorization has not been changed.");
            }
        }
    }

    public void LaunchCodex()
    {
        if (TryLaunchStartMenuApp())
        {
            return;
        }

        throw new InvalidOperationException("Could not find the installed Codex Desktop application.");
    }

    public async Task<bool> LoginProfileAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        string fullProfileDirectory = Path.GetFullPath(profileDirectory);
        Directory.CreateDirectory(fullProfileDirectory);
        string executable = CodexCliLocator.FindExecutable()
            ?? throw new FileNotFoundException("codex executable was not found.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = fullProfileDirectory,
        };
        startInfo.ArgumentList.Add("login");
        startInfo.Environment["CODEX_HOME"] = fullProfileDirectory;

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start codex login.");
        Task standardOutputTask = DrainReaderAsync(process.StandardOutput);
        Task standardErrorTask = DrainReaderAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode == 0 && File.Exists(Path.Combine(fullProfileDirectory, "auth.json"));
    }

    private static async Task DrainReaderAsync(TextReader reader)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
        {
        }
    }

    private bool TryLaunchStartMenuApp()
    {
        string? appId = ResolveStartAppId();
        if (!string.IsNullOrWhiteSpace(appId))
        {
            logger.Info("Launching Codex through Start menu AppUserModelID.");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{appId}",
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Could not start Codex through Start menu AppUserModelID.");
            return true;
        }

        string? shortcut = FindStartMenuShortcut();
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            logger.Info("Launching Codex through Start menu shortcut.");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = shortcut,
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Could not start Codex through Start menu shortcut.");
            return true;
        }

        return false;
    }

    private static string? ResolveStartAppId()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"(Get-StartApps | Where-Object { $_.AppID -like 'OpenAI.Codex_2p2nqsd0c76g0!*' } | Select-Object -First 1 -ExpandProperty AppID)\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            });

            string? output = process?.StandardOutput.ReadLine();
            process?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindStartMenuShortcut()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        ];

        foreach (string root in roots.Where(Directory.Exists))
        {
            string? shortcut = Directory.EnumerateFiles(root, "*Codex*.lnk", SearchOption.AllDirectories)
                .OrderBy(static path => path.Length)
                .FirstOrDefault();
            if (shortcut is not null)
            {
                return shortcut;
            }
        }

        return null;
    }

}
