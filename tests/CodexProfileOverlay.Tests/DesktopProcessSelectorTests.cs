using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopProcessSelectorTests
{
    private static readonly DateTime StartedAt = new(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RestoredLineageKeepsKnownExitTimesAndAppendsAbsentParents()
    {
        DesktopProcessSnapshot[] current = [Process(10, 1), Process(20, 1), Process(11, 10, seconds: 1)];
        var rememberedParent = Process(10, 1, exitSeconds: 10);
        var absentParent = Process(30, 1, seconds: 2, exitSeconds: 15);

        var restored = DesktopProcessSelector.RestoreLineage(current, [rememberedParent, absentParent]);

        Assert.Equal(new int[] { 10, 20, 11, 30 }, restored.Select(process => process.ProcessId));
        Assert.Equal(rememberedParent, restored[0]);
        Assert.Equal(absentParent, restored[3]);
        Assert.Equal(current[1], restored[1]);
        Assert.Equal(current[2], restored[2]);
        Assert.Null(current[0].ExitTimeUtc);
    }

    [Fact]
    public void RestoringLineageRejectsRecycledPidEvenWhenRememberedProcessExited()
    {
        DesktopProcessSnapshot[] current = [Process(10, 1, seconds: 20)];
        DesktopProcessSnapshot[] remembered = [Process(10, 1, exitSeconds: 10)];

        Assert.Throws<InvalidOperationException>(() => DesktopProcessSelector.RestoreLineage(current, remembered));
    }

    [Fact]
    public void UnknownCurrentIdentityRequiresRememberedExitTime()
    {
        DesktopProcessSnapshot[] current = [new(10, 1, 1, null)];
        var rememberedAlive = Process(10, 1);
        var rememberedExited = Process(10, 1, exitSeconds: 10);

        Assert.Throws<InvalidOperationException>(() => DesktopProcessSelector.RestoreLineage(current, [rememberedAlive]));

        var restored = DesktopProcessSelector.RestoreLineage(current, [rememberedExited]);

        Assert.Equal(rememberedExited, Assert.Single(restored));
    }

    [Fact]
    public void RestoringLineageRejectsDuplicateCurrentAndRememberedPids()
    {
        var process = Process(10, 1);

        var currentError = Assert.Throws<ArgumentException>(() => DesktopProcessSelector.RestoreLineage([process, process], []));
        var rememberedError = Assert.Throws<ArgumentException>(() => DesktopProcessSelector.RestoreLineage([], [process, process]));

        Assert.Equal("currentSnapshot", currentError.ParamName);
        Assert.Equal("remembered", rememberedError.ParamName);
    }

    [Fact]
    public void SelectsOnlyVerifiedDesktopTreeAndSkipsSeparateServiceAndApplication()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1),
            Process(11, 10, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(20, 1), // Another desktop application with a similar name is not a verified root.
            Process(21, 20, seconds: 1),
            Process(30, 1, session: 0), // A service is separate from the desktop tree.
            Process(31, 10, session: 0, seconds: 1),
            Process(40, 1), // An unrelated Codex-named sibling is not a verified root.
        ];

        Assert.Equal(new int[] { 10, 11, 12 }, SelectIds(snapshot, [10]));
    }

    [Fact]
    public void ForeignSessionRootsAndChildrenAreExcluded()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1),
            Process(11, 10, session: 2, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(20, 1, session: 2),
            Process(21, 20, seconds: 1),
        ];

        Assert.Equal(new int[] { 10 }, SelectIds(snapshot, [10, 20]));
        Assert.Empty(SelectIds(snapshot, [20]));
    }

    [Fact]
    public void MultipleRootsAreReturnedFirstAndOverlappingTreesAreDeduplicated()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1),
            Process(11, 10, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(20, 1),
            Process(21, 20, seconds: 1),
        ];

        Assert.Equal(new int[] { 20, 10, 11, 21, 12 }, SelectIds(snapshot, [20, 10, 20, 11]));
    }

    [Fact]
    public void ReusedParentPidDoesNotSelectOlderChildOrItsDescendants()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1, seconds: 10),
            Process(11, 10, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(13, 10, seconds: 10),
            Process(14, 13, seconds: 11),
        ];

        Assert.Equal(new int[] { 10, 13, 14 }, SelectIds(snapshot, [10]));
    }

    [Fact]
    public void RetainedExitedParentSelectsOnlyChildrenBornDuringItsLifetime()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1, seconds: 1, exitSeconds: 10),
            Process(11, 10, seconds: 1), // Both lifetime boundaries are inclusive.
            Process(12, 10, seconds: 10),
            Process(13, 10, seconds: 5),
            Process(14, 10, seconds: 11), // This reused-parent-PID relation is impossible.
            Process(15, 14, seconds: 12),
            Process(16, 11, seconds: 20), // A surviving child may create descendants after its parent exits.
            Process(17, 10, seconds: 0),
        ];

        Assert.Equal(new int[] { 10, 11, 12, 13, 16 }, SelectIds(snapshot, [10]));
    }

    [Fact]
    public void UnknownChildCreationTimeRemainsSelectedWhenParentExitIsKnown()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1, exitSeconds: 10),
            new(11, 10, 1, null),
        ];

        Assert.Equal(new int[] { 10, 11 }, SelectIds(snapshot, [10]));
    }

    [Fact]
    public void UnknownCreationTimesRemainSelectedForRuntimeValidation()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1),
            new(11, 10, 1, null),
            Process(12, 11, seconds: -1),
            new(20, 1, 1, null),
            Process(21, 20),
        ];

        var selected = DesktopProcessSelector.SelectTree(snapshot, [10, 20], 1, 99);

        Assert.Equal(new int[] { 10, 20, 11, 21, 12 }, selected.Select(process => process.ProcessId));
        Assert.Null(selected.Single(process => process.ProcessId == 11).StartTimeUtc);
        Assert.Null(selected.Single(process => process.ProcessId == 20).StartTimeUtc);
    }

    [Fact]
    public void CyclicParentRelationsDoNotRepeatProcesses()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 12),
            Process(11, 10),
            Process(12, 11),
        ];

        Assert.Equal(new int[] { 10, 11, 12 }, SelectIds(snapshot, [10]));
    }

    [Fact]
    public void ExcludedOverlayAndItsSubtreeArePrunedEvenWhenVerifiedAsRoots()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(10, 1),
            Process(11, 10, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(13, 12, seconds: 3),
            Process(14, 10, seconds: 1),
        ];

        Assert.Equal(new int[] { 10, 14 }, SelectIds(snapshot, [10, 11, 12, 13], excluded: 11));
    }

    [Fact]
    public void RememberedDescendantCanRemainARootAfterOriginalRootExits()
    {
        DesktopProcessSnapshot[] snapshot =
        [
            Process(11, 10, seconds: 1),
            Process(12, 11, seconds: 2),
            Process(20, 1),
        ];

        Assert.Equal(new int[] { 11, 12 }, SelectIds(snapshot, [10, 11]));
    }

    [Fact]
    public void MissingOrUnverifiedRootsDoNotSelectAnything()
    {
        DesktopProcessSnapshot[] snapshot = [Process(10, 1), Process(11, 10, seconds: 1)];

        Assert.Empty(SelectIds(snapshot, []));
        Assert.Empty(SelectIds(snapshot, [100]));
    }

    [Fact]
    public void DuplicateSnapshotProcessIdsAreRejected()
    {
        DesktopProcessSnapshot[] snapshot = [Process(10, 1), Process(10, 20, session: 2)];

        var exception = Assert.Throws<ArgumentException>(() => SelectIds(snapshot, [10]));

        Assert.Equal("snapshot", exception.ParamName);
    }

    private static int[] SelectIds(
        IEnumerable<DesktopProcessSnapshot> snapshot,
        IEnumerable<int> roots,
        int excluded = 99) =>
        DesktopProcessSelector.SelectTree(snapshot, roots, 1, excluded).Select(process => process.ProcessId).ToArray();

    private static DesktopProcessSnapshot Process(int id, int parent, int session = 1, int seconds = 0, int? exitSeconds = null) =>
        new(id, parent, session, StartedAt.AddSeconds(seconds), exitSeconds is int exited ? StartedAt.AddSeconds(exited) : null);
}
