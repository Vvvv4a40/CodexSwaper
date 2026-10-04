using System.ComponentModel;

namespace CodexProfileOverlay.Core.Services;

public static class DesktopShutdownBatch
{
    public static void RequestTermination(
        IEnumerable<DesktopProcessSnapshot> selectedProcesses,
        IReadOnlySet<int> verifiedDesktopProcessIds,
        Action<DesktopProcessSnapshot> terminate,
        Action<DesktopProcessSnapshot, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(selectedProcesses);
        ArgumentNullException.ThrowIfNull(verifiedDesktopProcessIds);
        ArgumentNullException.ThrowIfNull(terminate);
        ArgumentNullException.ThrowIfNull(onFailure);

        // Stop the verified desktop application first, so it cannot keep spawning workers.
        // A denied worker must not prevent termination requests for the rest of the tree.
        var ordered = selectedProcesses
            .OrderBy(process => verifiedDesktopProcessIds.Contains(process.ProcessId) ? 0 : 1)
            .ThenBy(process => process.StartTimeUtc ?? DateTime.MaxValue);

        foreach (var process in ordered)
        {
            try
            {
                terminate(process);
            }
            catch (Win32Exception exception)
            {
                onFailure(process, exception);
            }
        }
    }
}
