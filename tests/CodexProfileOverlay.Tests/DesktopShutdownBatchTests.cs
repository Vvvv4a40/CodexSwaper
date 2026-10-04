using System.ComponentModel;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopShutdownBatchTests
{
    private static readonly DateTime StartedAt = new(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DeniedNewestWorkerDoesNotPreventDesktopRootTermination()
    {
        var root = Process(10, 1);
        var renderer = Process(11, 10, seconds: 1);
        var worker = Process(12, 11, seconds: 2);
        DesktopProcessSnapshot[] input = [worker, renderer, root];
        var attempted = new List<int>();
        var failures = new List<(DesktopProcessSnapshot Process, Exception Error)>();
        var denied = new Win32Exception(5);

        DesktopShutdownBatch.RequestTermination(input, new HashSet<int> { 10, 11 }, process =>
        {
            attempted.Add(process.ProcessId);
            if (process.ProcessId == worker.ProcessId)
            {
                throw denied;
            }
        }, (process, error) => failures.Add((process, error)));

        Assert.Equal(new int[] { 10, 11, 12 }, attempted.ToArray());
        var failure = Assert.Single(failures);
        Assert.Same(worker, failure.Process);
        Assert.Same(denied, failure.Error);
        Assert.Equal(5, ((Win32Exception)failure.Error).NativeErrorCode);
        Assert.Equal(new int[] { 12, 11, 10 }, input.Select(process => process.ProcessId).ToArray());
    }

    [Fact]
    public void VerifiedDesktopProcessesComeBeforeWorkersRegardlessOfInputOrder()
    {
        DesktopProcessSnapshot[] input =
        [
            Process(13, 12, seconds: 4),
            Process(21, 10, seconds: 3),
            Process(20, 10, seconds: 2),
            Process(12, 10, seconds: 1),
            Process(10, 1),
        ];
        var attempted = new List<int>();

        DesktopShutdownBatch.RequestTermination(input, new HashSet<int> { 10, 20, 21 },
            process => attempted.Add(process.ProcessId), (_, _) => Assert.Fail("No termination should fail."));

        Assert.Equal(new int[] { 10, 20, 21, 12, 13 }, attempted.ToArray());
    }

    [Fact]
    public void DeniedRootDoesNotPreventOtherDesktopAndWorkerRequests()
    {
        var root = Process(10, 1);
        DesktopProcessSnapshot[] input = [Process(12, 11, seconds: 2), Process(11, 10, seconds: 1), root];
        var attempted = new List<int>();
        var denied = new Win32Exception(5);
        var failures = new List<(DesktopProcessSnapshot Process, Exception Error)>();

        DesktopShutdownBatch.RequestTermination(input, new HashSet<int> { 10, 11 }, process =>
        {
            attempted.Add(process.ProcessId);
            if (process.ProcessId == root.ProcessId)
            {
                throw denied;
            }
        }, (process, error) => failures.Add((process, error)));

        Assert.Equal(new int[] { 10, 11, 12 }, attempted.ToArray());
        var failure = Assert.Single(failures);
        Assert.Same(root, failure.Process);
        Assert.Same(denied, failure.Error);
    }

    [Fact]
    public void IdentityMismatchPropagatesWithoutFailureCallbackOrFurtherTermination()
    {
        var mismatch = new InvalidOperationException("Process ID was reused; its identity no longer matches.");
        var attempted = new List<int>();
        var failureCount = 0;

        var error = Assert.Throws<InvalidOperationException>(() => DesktopShutdownBatch.RequestTermination(
            [Process(11, 10, seconds: 1), Process(10, 1)], new HashSet<int> { 10 }, process =>
            {
                attempted.Add(process.ProcessId);
                throw mismatch;
            }, (_, _) => failureCount++));

        Assert.Same(mismatch, error);
        Assert.Equal(new int[] { 10 }, attempted.ToArray());
        Assert.Equal(0, failureCount);
    }

    [Fact]
    public void TrackedWorkersAreSortedParentFirstAndSourceRemainsUnchanged()
    {
        DesktopProcessSnapshot[] input =
        [
            Process(13, 12, seconds: 3),
            Process(12, 11, seconds: 2),
            Process(11, 10, seconds: 1),
            Process(10, 1),
        ];
        var original = input.ToArray();
        var attempted = new List<int>();

        DesktopShutdownBatch.RequestTermination(input, new HashSet<int> { 10 },
            process => attempted.Add(process.ProcessId), (_, _) => Assert.Fail("No termination should fail."));

        Assert.Equal(new int[] { 10, 11, 12, 13 }, attempted.ToArray());
        Assert.Equal(original, input);
    }

    [Fact]
    public void CancellationPropagatesWithoutFailureCallbackOrFurtherTermination()
    {
        var cancelled = new OperationCanceledException();
        var attempted = new List<int>();
        var failureCount = 0;

        var error = Assert.Throws<OperationCanceledException>(() => DesktopShutdownBatch.RequestTermination(
            [Process(11, 10, seconds: 1), Process(10, 1)], new HashSet<int> { 10 }, process =>
            {
                attempted.Add(process.ProcessId);
                throw cancelled;
            }, (_, _) => failureCount++));

        Assert.Same(cancelled, error);
        Assert.Equal(new int[] { 10 }, attempted.ToArray());
        Assert.Equal(0, failureCount);
    }

    [Fact]
    public void FatalFailurePropagatesWithoutFailureCallbackOrFurtherTermination()
    {
        var fatal = new OutOfMemoryException();
        var attempted = new List<int>();
        var failureCount = 0;

        var error = Assert.Throws<OutOfMemoryException>(() => DesktopShutdownBatch.RequestTermination(
            [Process(11, 10, seconds: 1), Process(10, 1)], new HashSet<int> { 10 }, process =>
            {
                attempted.Add(process.ProcessId);
                throw fatal;
            }, (_, _) => failureCount++));

        Assert.Same(fatal, error);
        Assert.Equal(new int[] { 10 }, attempted.ToArray());
        Assert.Equal(0, failureCount);
    }

    [Fact]
    public void UnexpectedFailureIsNotClassifiedAsRecoverable()
    {
        var unexpected = new IOException("Unexpected failure.");
        var failureCount = 0;

        var error = Assert.Throws<IOException>(() => DesktopShutdownBatch.RequestTermination(
            [Process(10, 1)], new HashSet<int> { 10 }, _ => throw unexpected, (_, _) => failureCount++));

        Assert.Same(unexpected, error);
        Assert.Equal(0, failureCount);
    }

    [Fact]
    public void EmptySelectionMakesNoRequestsOrFailureCallbacks()
    {
        DesktopShutdownBatch.RequestTermination([], new HashSet<int> { 10 },
            _ => Assert.Fail("No process was selected."), (_, _) => Assert.Fail("No request was made."));
    }

    [Fact]
    public void UnknownCreationTimesFollowKnownCreationTimesWithinEachGroup()
    {
        DesktopProcessSnapshot[] input =
        [
            new(12, 11, 1, null),
            new(20, 10, 1, null),
            Process(11, 10, seconds: 1),
            Process(10, 1),
        ];
        var attempted = new List<int>();

        DesktopShutdownBatch.RequestTermination(input, new HashSet<int> { 10, 20 },
            process => attempted.Add(process.ProcessId), (_, _) => Assert.Fail("No termination should fail."));

        Assert.Equal(new int[] { 10, 20, 11, 12 }, attempted.ToArray());
    }

    private static DesktopProcessSnapshot Process(int id, int parent, int seconds = 0) =>
        new(id, parent, 1, StartedAt.AddSeconds(seconds));
}
