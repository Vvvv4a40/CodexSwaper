using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SafeLoggerTests
{
    [Fact]
    public void Error_RedactsApiKeysAndBareBearerValues()
    {
        using var temp = new TempDirectory();
        var logger = new SafeLogger(temp.Path);
        logger.Error("OPENAI_API_KEY=" + "sk-test-secret-value" + " Bearer " + "opaque-test-token");
        string log = File.ReadAllText(Directory.EnumerateFiles(temp.Path).Single());
        Assert.DoesNotContain("sk-test-secret-value", log);
        Assert.DoesNotContain("opaque-test-token", log);
    }

    [Fact]
    public void Info_RotatesBoundedFilesAndLeavesUnrelatedFilesUntouched()
    {
        using var temp = new TempDirectory();
        string unrelated = Path.Combine(temp.Path, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        var logger = new SafeLogger(temp.Path, 16 * 1024);
        for (int index = 0; index < 50; index++) { logger.Info(new string('x', 20000)); }
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.InRange(Directory.GetFiles(temp.Path, "overlay-*").Length, 1, 4);
        Assert.All(Directory.GetFiles(temp.Path, "overlay-*"), path => Assert.True(new FileInfo(path).Length <= 16 * 1024));
    }

    [Fact]
    public void Info_LogWriteFailureDoesNotFailTheCallingOperation()
    {
        using var temp = new TempDirectory();
        var logger = new SafeLogger(temp.Path);
        Directory.CreateDirectory(Path.Combine(temp.Path, $"overlay-{DateTimeOffset.Now:yyyyMMdd}.log"));
        logger.Info("diagnostic message");
    }

    [Fact]
    public void Error_RedactsTokenLikeValuesAndEmails()
    {
        using var temp = new TempDirectory();
        var logger = new SafeLogger(temp.Path);

        string message = "Authorization" + ": Bearer " + "sk-secret-value " + "refresh" + "_token=very-secret " + "user" + "@example.com";
        logger.Error(message, new InvalidOperationException("session 00000000-0000-0000-0000-000000000000"));

        string log = File.ReadAllText(Directory.EnumerateFiles(temp.Path).Single());
        Assert.DoesNotContain("sk-secret-value", log, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", log, StringComparison.Ordinal);
        Assert.Contains("[redacted-id]", log, StringComparison.Ordinal);
    }
}
