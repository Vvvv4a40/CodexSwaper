using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void RequireInsideRoot_RejectsSiblingWithTheSamePrefix()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "profiles");
        Assert.Throws<InvalidDataException>(() => PathSafety.RequireInsideRoot(root, root + "-other/auth.json"));
        Assert.Throws<InvalidDataException>(() => PathSafety.RequireInsideRoot(root, root));
        Assert.Equal(Path.GetFullPath(root), PathSafety.RequireInsideRoot(root, root, allowRoot: true));
    }

    [Fact]
    public void CredentialOperations_RejectProfileJunctionAndPreserveItsTarget()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        string outside = CreateOutsideFixture(temp);
        string linkedProfile = Path.Combine(temp.Paths.ProfilesDirectory, "linked");
        using var junction = new TestJunction(linkedProfile, outside);
        string source = Path.Combine(temp.Paths.SharedCodexDirectory, "source.json");
        File.WriteAllText(source, "source-auth");
        var discovery = new ProfileDiscoveryService(temp.Paths.ProfilesDirectory);
        var manager = new ProfileManagerService(temp.Paths, discovery, new ProfileMetadataStore(temp.Paths.ProfilesMetadataFile));

        Assert.Empty(discovery.DiscoverProfiles());
        Assert.Throws<InvalidDataException>(() => discovery.GetRequiredProfile("linked"));
        Assert.Throws<InvalidDataException>(() => manager.RemoveIncompleteProfileDirectory("linked"));
        Assert.Throws<InvalidDataException>(() => new AtomicFileReplacer().ReplaceFromSource(source, Path.Combine(linkedProfile, "auth.json")));
        Assert.Throws<InvalidDataException>(() => PathSafety.RequireRegularPath(Path.Combine(linkedProfile, "new-directory", "new-auth.json")));
        Assert.Equal("outside-auth", File.ReadAllText(Path.Combine(outside, "auth.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "marker.txt")));
    }

    [Fact]
    public async Task SwitchAsync_RejectsLinkedTargetBeforeAuthorizationOrBackupChanges()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.WriteSharedAuth("current-live-auth");
        temp.ActiveProfileStore.Write("current");
        string outside = CreateOutsideFixture(temp);
        using var junction = new TestJunction(Path.Combine(temp.Paths.ProfilesDirectory, "linked"), outside);

        await Assert.ThrowsAsync<InvalidDataException>(() => temp.CreateSwitchService().SwitchAsync("linked"));

        Assert.Equal("current-live-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
        Assert.False(Directory.Exists(temp.Paths.BackupDirectory));
        Assert.Equal("outside-auth", File.ReadAllText(Path.Combine(outside, "auth.json")));
    }

    [Fact]
    public void ActiveProfileStore_RejectsRedirectedStorageDirectory()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        string outside = CreateOutsideFixture(temp);
        using var junction = new TestJunction(temp.Paths.ApplicationDataDirectory, outside);
        Assert.Throws<InvalidDataException>(() => temp.ActiveProfileStore.Write("profile"));
        Assert.Throws<InvalidDataException>(() => temp.ActiveProfileStore.Read());
        Assert.False(File.Exists(Path.Combine(outside, "active-profile.txt")));
    }

    [Fact]
    public void RetentionCleanup_DoesNotFollowAnAbandonedTransactionJunction()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        string outside = CreateOutsideFixture(temp);
        Directory.CreateDirectory(temp.Paths.BackupDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var manifest = new SwitchBackupManifest(1, "preparing", now.AddDays(-3), null, "old", new string('A', 64), new string('B', 64), 10);
        File.WriteAllText(Path.Combine(outside, "manifest.json"), JsonSerializer.Serialize(manifest));
        using var junction = new TestJunction(Path.Combine(temp.Paths.BackupDirectory, "txn-external"), outside);

        new BackupMaintenanceService(temp.Paths, utcNow: () => now).CleanupRetention();

        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "marker.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "manifest.json")));
    }

    [Fact]
    public void LegacyCleanup_RemovesALinkWithoutDeletingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        string outside = CreateOutsideFixture(temp);
        string legacy = Path.Combine(temp.Paths.BackupDirectory, "state-20261004-000000-000-" + new string('a', 32));
        Directory.CreateDirectory(legacy);
        using var junction = new TestJunction(Path.Combine(legacy, "linked-sessions"), outside);

        new BackupMaintenanceService(temp.Paths).CleanLegacyBackups(retainNewest: 0);

        Assert.False(Directory.Exists(legacy));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "marker.txt")));
    }

    [Fact]
    public void Migration_SkipsNestedSessionJunctions()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        temp.AddProfile("old", "old-auth");
        string sessions = Path.Combine(temp.Paths.ProfilesDirectory, "old", "codex-state", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "normal.jsonl"), "normal-session");
        string outside = CreateOutsideFixture(temp);
        using var junction = new TestJunction(Path.Combine(sessions, "linked"), outside);

        SharedCodexStateMigrationResult result = new SharedCodexStateMigrationService(temp.Paths).MigrateLegacyProfileState();

        Assert.Equal(1, result.CopiedFileCount);
        Assert.Equal("normal-session", File.ReadAllText(Path.Combine(temp.Paths.SharedCodexDirectory, "sessions", "normal.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(temp.Paths.SharedCodexDirectory, "sessions", "linked")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "marker.txt")));
    }

    [Fact]
    public void Migration_RejectsRedirectedSharedDestination()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var temp = new TestLayout();
        temp.AddProfile("old", "old-auth");
        string source = Path.Combine(temp.Paths.ProfilesDirectory, "old", "codex-state", "sessions");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "new-session.jsonl"), "source-session");
        string outside = CreateOutsideFixture(temp);
        using var junction = new TestJunction(Path.Combine(temp.Paths.SharedCodexDirectory, "sessions"), outside);

        Assert.Throws<InvalidDataException>(() => new SharedCodexStateMigrationService(temp.Paths).MigrateLegacyProfileState());

        Assert.False(File.Exists(Path.Combine(outside, "new-session.jsonl")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "marker.txt")));
    }

    private static string CreateOutsideFixture(TestLayout temp)
    {
        string outside = Path.Combine(temp.LocalAppData, "outside-fixture");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "marker.txt"), "keep");
        File.WriteAllText(Path.Combine(outside, "auth.json"), "outside-auth");
        return outside;
    }
}

// Owns only a junction and its test fixture. PowerShell is addressed through the Windows system directory.
internal sealed class TestJunction : IDisposable
{
    private readonly string link;

    public TestJunction(string link, string target)
    {
        this.link = Path.GetFullPath(link);
        Directory.CreateDirectory(Path.GetDirectoryName(this.link)!);
        string script = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '"
            + this.link.Replace("'", "''", StringComparison.Ordinal) + "' -Target '"
            + Path.GetFullPath(target).Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not create the test junction.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new TimeoutException("Creating a test junction timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Creating a test junction failed: " + process.StandardError.ReadToEnd());
        }
        Assert.True(PathSafety.IsReparsePoint(this.link));
    }

    public void Dispose()
    {
        if (Directory.Exists(link)) { Directory.Delete(link, recursive: false); }
    }
}
