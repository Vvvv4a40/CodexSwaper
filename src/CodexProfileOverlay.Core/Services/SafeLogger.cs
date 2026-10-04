using System.Text;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public sealed class SafeLogger
{
    private const int MaximumMessageCharacters = 4096;
    private static readonly Regex SecretLikePattern = new(
        @"(?i)(?<prefix>(?:authorization\s*:\s*)?bearer\s+|(?:access|refresh|id)[_-]?token[""'\s:=]+|(?:openai[_-]?)?api[_-]?key[""'\s:=]+)[^""'\s,}]+|\bsk-[a-z0-9_-]+|(?<email>[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})|(?<identifier>\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly string logFile;
    private readonly object gate = new();
    private readonly long maximumFileBytes;

    public SafeLogger(string logDirectory, long maximumFileBytes = 1024 * 1024)
    {
        if (maximumFileBytes < 16 * 1024) { throw new ArgumentOutOfRangeException(nameof(maximumFileBytes)); }
        PathSafety.RequireRegularPath(logDirectory);
        Directory.CreateDirectory(logDirectory);
        logFile = Path.Combine(PathSafety.RequireRegularPath(logDirectory), $"overlay-{DateTimeOffset.Now:yyyyMMdd}.log");
        this.maximumFileBytes = maximumFileBytes;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        string sanitized = Sanitize(message);
        string line = $"{DateTimeOffset.Now:O} {level} {sanitized}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {Sanitize(exception.Message)}";
        }

        lock (gate)
        {
            // Logging must not invalidate an otherwise successful account transaction.
            try
            {
                PathSafety.RequireRegularPath(logFile);
                if (File.Exists(logFile) && new FileInfo(logFile).Length + Encoding.UTF8.GetByteCount(line) + 2 > maximumFileBytes)
                {
                    RotateLog();
                }
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Log write unavailable.");
            }
        }
    }

    private void RotateLog()
    {
        for (int index = 3; index >= 1; index--)
        {
            string destination = logFile + "." + index;
            string source = index == 1 ? logFile : logFile + "." + (index - 1);
            PathSafety.RequireRegularPath(source);
            PathSafety.RequireRegularPath(destination);
            if (File.Exists(source)) { File.Move(source, destination, overwrite: true); }
        }
        var logs = Directory.EnumerateFiles(Path.GetDirectoryName(logFile)!, "overlay-*.log*", SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^overlay-\d{8}\.log(?:\.[1-3])?$"))
            .Select(path => new FileInfo(PathSafety.RequireRegularPath(path)))
            .OrderByDescending(info => info.LastWriteTimeUtc).ToArray();
        foreach (var old in logs.Skip(8))
        {
            if (!old.FullName.Equals(logFile, StringComparison.OrdinalIgnoreCase)) { old.Delete(); }
        }
    }

    private static string Sanitize(string value)
    {
        string singleLine = (value.Length > MaximumMessageCharacters ? value[..MaximumMessageCharacters] : value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        try
        {
            return SecretLikePattern.Replace(singleLine, match => match.Groups["prefix"].Success
                ? match.Groups["prefix"].Value + "[redacted]"
                : match.Groups["email"].Success ? "[redacted-email]"
                : match.Groups["identifier"].Success ? "[redacted-id]" : "[redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[message redacted: pattern limit]"; }
    }
}
