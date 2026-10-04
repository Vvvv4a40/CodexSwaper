namespace CodexProfileOverlay.Core.Services;

public sealed record DesktopProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    int SessionId,
    DateTime? StartTimeUtc,
    DateTime? ExitTimeUtc = null);

public static class DesktopProcessSelector
{
    public static IReadOnlyList<DesktopProcessSnapshot> RestoreLineage(
        IEnumerable<DesktopProcessSnapshot> currentSnapshot,
        IEnumerable<DesktopProcessSnapshot> remembered)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(remembered);

        var restored = new List<DesktopProcessSnapshot>();
        var indexes = new Dictionary<int, int>();
        foreach (var process in currentSnapshot)
        {
            if (!indexes.TryAdd(process.ProcessId, restored.Count))
            {
                throw new ArgumentException("The process snapshot contains duplicate process IDs.", nameof(currentSnapshot));
            }

            restored.Add(process);
        }

        var rememberedIds = new HashSet<int>();
        foreach (var process in remembered)
        {
            if (!rememberedIds.Add(process.ProcessId))
            {
                throw new ArgumentException("The remembered lineage contains duplicate process IDs.", nameof(remembered));
            }

            if (!indexes.TryGetValue(process.ProcessId, out var index))
            {
                indexes.Add(process.ProcessId, restored.Count);
                restored.Add(process);
                continue;
            }

            var current = restored[index];
            if (current.StartTimeUtc is DateTime currentStarted && currentStarted != process.StartTimeUtc)
            {
                throw new InvalidOperationException($"Process ID {process.ProcessId} was reused; its remembered lineage cannot be trusted.");
            }

            if (current.StartTimeUtc is null && process.ExitTimeUtc is null)
            {
                throw new InvalidOperationException($"The identity of process ID {process.ProcessId} could not be verified.");
            }

            restored[index] = process;
        }

        return restored.AsReadOnly();
    }

    public static IReadOnlyList<DesktopProcessSnapshot> SelectTree(
        IEnumerable<DesktopProcessSnapshot> snapshot,
        IEnumerable<int> verifiedRootProcessIds,
        int sessionId,
        int excludedProcessId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(verifiedRootProcessIds);

        var processes = new Dictionary<int, DesktopProcessSnapshot>();
        var children = new Dictionary<int, List<DesktopProcessSnapshot>>();
        foreach (var process in snapshot)
        {
            if (!processes.TryAdd(process.ProcessId, process))
            {
                throw new ArgumentException("The process snapshot contains duplicate process IDs.", nameof(snapshot));
            }

            if (process.SessionId != sessionId)
            {
                continue;
            }

            if (!children.TryGetValue(process.ParentProcessId, out var siblings))
            {
                siblings = [];
                children.Add(process.ParentProcessId, siblings);
            }

            siblings.Add(process);
        }

        // Excluding the overlay also excludes its descendants, even if one is supplied as a root.
        var excluded = new HashSet<int> { excludedProcessId };
        var excludedQueue = new Queue<DesktopProcessSnapshot>();
        if (processes.TryGetValue(excludedProcessId, out var excludedProcess) && excludedProcess.SessionId == sessionId)
        {
            excludedQueue.Enqueue(excludedProcess);
        }

        while (excludedQueue.TryDequeue(out var excludedParent))
        {
            if (!children.TryGetValue(excludedParent.ProcessId, out var descendants))
            {
                continue;
            }

            foreach (var child in descendants)
            {
                if (IsPlausibleChild(excludedParent, child) && excluded.Add(child.ProcessId))
                {
                    excludedQueue.Enqueue(child);
                }
            }
        }

        var selected = new List<DesktopProcessSnapshot>();
        var visited = new HashSet<int>();
        var queue = new Queue<DesktopProcessSnapshot>();
        foreach (var rootProcessId in verifiedRootProcessIds)
        {
            if (excluded.Contains(rootProcessId) || !processes.TryGetValue(rootProcessId, out var root) ||
                root.SessionId != sessionId || !visited.Add(rootProcessId))
            {
                continue;
            }

            selected.Add(root);
            queue.Enqueue(root);
        }

        while (queue.TryDequeue(out var selectedParent))
        {
            if (!children.TryGetValue(selectedParent.ProcessId, out var descendants))
            {
                continue;
            }

            foreach (var child in descendants)
            {
                if (!excluded.Contains(child.ProcessId) && IsPlausibleChild(selectedParent, child) && visited.Add(child.ProcessId))
                {
                    selected.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return selected.AsReadOnly();
    }

    private static bool IsPlausibleChild(DesktopProcessSnapshot parent, DesktopProcessSnapshot child)
    {
        if (child.StartTimeUtc is not DateTime childStarted)
        {
            return true;
        }

        return (parent.StartTimeUtc is not DateTime parentStarted || childStarted >= parentStarted) &&
            (parent.ExitTimeUtc is not DateTime parentExited || childStarted <= parentExited);
    }
}
