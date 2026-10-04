using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal static class DesktopProcessDiagnostics
{
    // Invoked before normal startup, profile discovery, settings, registry, login or shutdown code.
    public static void WriteReport(string outputPath)
    {
        string reportPath = Path.GetFullPath(outputPath);
        string reportDirectory = Path.GetDirectoryName(reportPath) ?? throw new ArgumentException("A report directory is required.");
        Directory.CreateDirectory(reportDirectory);
        var logger = new SafeLogger(Path.Combine(reportDirectory, "diagnostic-logs"));
        using Process current = Process.GetCurrentProcess();
        var captured = DesktopProcessCatalog.Capture(current.SessionId);
        var windows = new CodexWindowFinder(logger).FindMainWindows();
        var roots = new HashSet<int>(windows.Select(window => window.ProcessId));
        var inspected = new List<object>();
        foreach (var candidate in captured.Where(process => process.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            || process.ExecutableName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)))
        {
            string? path = null;
            string? error = null;
            bool desktop = false;
            try
            {
                path = DesktopProcessCatalog.ExecutablePath(candidate.Identity);
                desktop = path is not null && DesktopProcessCatalog.IsInstalledDesktopPath(path);
                if (desktop) { roots.Add(candidate.Identity.ProcessId); }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
            {
                error = exception.Message;
            }
            inspected.Add(new { candidate.Identity.ProcessId, candidate.ExecutableName, path, isInstalledDesktop = desktop, error });
        }
        var selected = DesktopProcessSelector.SelectTree(captured.Select(process => process.Identity), roots, current.SessionId, current.Id);
        var selectedRows = new List<object>();
        foreach (var identity in selected)
        {
            string name = captured.First(process => process.Identity.ProcessId == identity.ProcessId).ExecutableName;
            bool? exited = null;
            string? queryError = null;
            try
            {
                using var pinned = DesktopProcessCatalog.Open(identity, name);
                exited = pinned?.HasExited;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                queryError = exception.Message;
            }
            // Probe access only. Never call TerminateProcess or send WM_CLOSE in diagnostics.
            using var terminateAccess = ProcessNative.OpenProcess(0x1001, false, (uint)identity.ProcessId);
            int terminateAccessError = terminateAccess.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            selectedRows.Add(new
            {
                identity.ProcessId, identity.ParentProcessId, identity.SessionId, identity.StartTimeUtc,
                executable = name, role = roots.Contains(identity.ProcessId) ? "desktop" : "helper",
                exited, queryError, terminateAccessError,
            });
        }
        var report = new
        {
            version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion,
            mode = "read-only process diagnostics",
            windows = windows.Select(window => new { window.ProcessId, window.ProcessName, window.StartTimeUtc }),
            candidates = inspected,
            selected = selectedRows,
            sessionId = current.SessionId,
            systemSessionExcluded = current.SessionId != 0,
            authorizationAccessed = false,
            shutdownRequested = false,
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
