namespace CodexProfileOverlay.Core.Services;

public sealed class AtomicFileReplacer : IAtomicFileReplacer
{
    public void ReplaceFromSource(string sourceFile, string destinationFile)
    {
        sourceFile = PathSafety.RequireRegularPath(sourceFile);
        destinationFile = PathSafety.RequireRegularPath(destinationFile);
        string destinationDirectory = Path.GetDirectoryName(destinationFile)
            ?? throw new InvalidOperationException("Destination file has no directory.");
        Directory.CreateDirectory(destinationDirectory);
        PathSafety.RequireRegularPath(destinationDirectory);

        string tempFile = Path.Combine(destinationDirectory, $".auth-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceFile, tempFile, overwrite: false);
            PathSafety.RequireRegularPath(destinationFile);
            if (File.Exists(destinationFile))
            {
                File.Replace(tempFile, destinationFile, null);
            }
            else
            {
                File.Move(tempFile, destinationFile);
            }
        }
        finally
        {
            if (File.Exists(tempFile) && !PathSafety.IsReparsePoint(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
