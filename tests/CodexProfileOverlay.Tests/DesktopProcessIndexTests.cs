using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopProcessIndexTests
{
    [Fact]
    public async Task DuplicateSnapshotFailsBeforeWindowOrProcessActions()
    {
        using var temp = new TempDirectory();
        var duplicate = new CapturedDesktopProcess(new DesktopProcessSnapshot(10, 0, 1,
            new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)), "ChatGPT.exe");
        bool queriedWindows = false;
        bool queriedPath = false;
        bool terminated = false;
        var service = new CodexProcessService(new SafeLogger(Path.Combine(temp.Path, "logs")), 1,
            _ => [duplicate, duplicate],
            () => { queriedWindows = true; return []; },
            _ => { queriedPath = true; return null; },
            _ => terminated = true);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CloseCodexAsync(1, true, CancellationToken.None));
        Assert.False(queriedWindows);
        Assert.False(queriedPath);
        Assert.False(terminated);
    }
}
