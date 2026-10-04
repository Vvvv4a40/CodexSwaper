namespace CodexProfileOverlay.Core.Services;

/// <summary>Rejects redirected paths before credential and backup operations.</summary>
public static class PathSafety
{
    public static string RequireRegularPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("A credential or storage path contains a symbolic link, junction, or other reparse point.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return fullPath;
    }

    public static string RequireInsideRoot(string root, string path, bool allowRoot = false)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        bool isRoot = fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!(allowRoot && isRoot) && !fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Resolved path escapes the expected storage directory.");
        }

        RequireRegularPath(fullRoot);
        return RequireRegularPath(fullPath);
    }

    public static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
