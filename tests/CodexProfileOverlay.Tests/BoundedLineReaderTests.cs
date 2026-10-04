using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadLineAsync_PreservesBufferedNotificationsAndResponseLines()
    {
        var reader = new BoundedLineReader(new StringReader("first\r\nsecond\nthird"), 32);
        Assert.Equal("first", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("second", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("third", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadLineAsync_RejectsOversizedOutputWithoutAllocatingAnUnboundedLine()
    {
        var reader = new BoundedLineReader(new StringReader(new string('x', 10000)), 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
    }
}
