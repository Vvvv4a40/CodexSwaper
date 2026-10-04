using CodexProfileOverlay;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopExecutableIdentityCacheTests
{
    private static readonly DateTime Started = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string InstalledPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
        "OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");

    [Fact]
    public void SameVerifiedProcessDoesNotRepeatExecutableQuery()
    {
        var cache = new DesktopExecutableIdentityCache();
        int queries = 0;
        string? Query() { queries++; return InstalledPath; }
        Assert.True(cache.GetOrVerify(10, Started, "ChatGPT", Query));
        Assert.True(cache.GetOrVerify(10, Started, "ChatGPT", Query));
        Assert.Equal(1, queries);
    }

    [Fact]
    public void ReusedPidDoesNotInheritValidation()
    {
        var cache = new DesktopExecutableIdentityCache();
        Assert.True(cache.GetOrVerify(10, Started, "ChatGPT", () => InstalledPath));
        Assert.False(cache.GetOrVerify(10, Started.AddTicks(1), "ChatGPT", () => @"C:\untrusted\ChatGPT.exe"));
        Assert.True(cache.GetOrVerify(10, Started.AddTicks(1), "ChatGPT", () => InstalledPath));
    }

    [Theory]
    [InlineData("codex-windows-sandbox-service")]
    [InlineData("CodexProfileOverlay")]
    [InlineData("ChatGPT-helper")]
    public void OtherExecutableNameCannotUseCachedDecision(string name)
    {
        var cache = new DesktopExecutableIdentityCache();
        Assert.True(cache.GetOrVerify(10, Started, "ChatGPT", () => InstalledPath));
        Assert.False(cache.GetOrVerify(10, Started, name, () => throw new InvalidOperationException("Unexpected path query.")));
    }

    [Fact]
    public void MissingPathIsRetriedRatherThanCached()
    {
        var cache = new DesktopExecutableIdentityCache();
        Assert.False(cache.GetOrVerify(10, Started, "ChatGPT", () => null));
        Assert.True(cache.GetOrVerify(10, Started, "ChatGPT", () => InstalledPath));
    }

    [Fact]
    public void QueryFailureCannotPublishVerifiedEntry()
    {
        var cache = new DesktopExecutableIdentityCache();
        Assert.Throws<InvalidOperationException>(() => cache.GetOrVerify(10, Started, "ChatGPT", () => throw new InvalidOperationException()));
        Assert.False(cache.GetOrVerify(10, Started, "ChatGPT", () => @"C:\untrusted\ChatGPT.exe"));
    }

    [Fact]
    public void CacheIsBoundedAndEvictionRevalidatesThePath()
    {
        var cache = new DesktopExecutableIdentityCache();
        for (int id = 1; id <= 129; id++)
        {
            Assert.True(cache.GetOrVerify(id, Started, "ChatGPT", () => InstalledPath));
        }
        int queries = 0;
        Assert.True(cache.GetOrVerify(1, Started, "ChatGPT", () => { queries++; return InstalledPath; }));
        Assert.Equal(1, queries);
    }
}
