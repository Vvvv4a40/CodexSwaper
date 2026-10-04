using System.Text;

namespace CodexProfileOverlay.Core.Services;

public sealed class BoundedLineReader(TextReader reader, int maximumLineCharacters)
{
    private readonly char[] buffer = new char[4096];
    private int offset;
    private int available;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (offset == available)
            {
                available = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (available == 0) { return line.Length == 0 ? null : line.ToString().TrimEnd('\r'); }
            }
            int newline = Array.IndexOf(buffer, '\n', offset, available - offset);
            int length = (newline < 0 ? available : newline) - offset;
            if (line.Length + length > maximumLineCharacters) { throw new InvalidDataException("CLI response exceeded the line size limit."); }
            line.Append(buffer, offset, length);
            offset += length;
            if (newline >= 0)
            {
                offset++;
                return line.ToString().TrimEnd('\r');
            }
        }
    }
}
