using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class ProfileDiscoveryService
{
    private readonly string profilesDirectory;

    public ProfileDiscoveryService(string profilesDirectory)
    {
        this.profilesDirectory = Path.GetFullPath(profilesDirectory);
    }

    public IReadOnlyList<ProfileInfo> DiscoverProfiles()
    {
        PathSafety.RequireRegularPath(profilesDirectory);
        if (!Directory.Exists(profilesDirectory))
        {
            return Array.Empty<ProfileInfo>();
        }

        return Directory.EnumerateDirectories(profilesDirectory)
            .Where(static directory => !PathSafety.IsReparsePoint(directory))
            .Select(CreateProfileInfo)
            .Where(static profile => profile is not null)
            .Cast<ProfileInfo>()
            .OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();
    }

    public ProfileInfo GetRequiredProfile(string profileName)
    {
        string validName = ProfileName.RequireValid(profileName);
        string directory = Path.Combine(profilesDirectory, validName);
        string fullDirectory = Path.GetFullPath(directory);
        EnsureInsideProfilesRoot(fullDirectory);

        string authFile = Path.Combine(fullDirectory, "auth.json");
        PathSafety.RequireRegularPath(authFile);
        if (!File.Exists(authFile))
        {
            throw new FileNotFoundException($"Profile '{validName}' does not contain auth.json.", authFile);
        }

        return new ProfileInfo(validName, fullDirectory, authFile);
    }

    private ProfileInfo? CreateProfileInfo(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        EnsureInsideProfilesRoot(fullDirectory);

        string name = Path.GetFileName(fullDirectory);
        if (!ProfileName.IsValid(name))
        {
            return null;
        }

        string authFile = Path.Combine(fullDirectory, "auth.json");
        if (PathSafety.IsReparsePoint(fullDirectory)
            || (File.Exists(authFile) && PathSafety.IsReparsePoint(authFile)))
        {
            return null;
        }
        return File.Exists(authFile)
            ? new ProfileInfo(name, fullDirectory, authFile)
            : null;
    }

    private void EnsureInsideProfilesRoot(string fullPath)
    {
        PathSafety.RequireInsideRoot(profilesDirectory, fullPath);
    }
}
