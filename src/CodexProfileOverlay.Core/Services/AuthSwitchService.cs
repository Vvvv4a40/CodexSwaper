namespace CodexProfileOverlay.Core.Services;

public sealed class AuthSwitchService
{
    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly IAtomicFileReplacer replacer;
    private readonly BackupMaintenanceService backups;
    private readonly SemaphoreSlim switchGate = new(1, 1);

    public AuthSwitchService(
        AppPaths paths,
        ProfileDiscoveryService profileDiscovery,
        ActiveProfileStore activeProfileStore,
        IAtomicFileReplacer? replacer = null,
        BackupMaintenanceService? backups = null)
    {
        this.paths = paths;
        this.profileDiscovery = profileDiscovery;
        this.activeProfileStore = activeProfileStore;
        this.replacer = replacer ?? new AtomicFileReplacer();
        this.backups = backups ?? new BackupMaintenanceService(paths);
    }

    public async Task<AuthSwitchResult> SwitchAsync(string targetProfileName, CancellationToken cancellationToken = default)
    {
        if (!await switchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A profile switch is already in progress.");
        }

        SwitchBackup? backup = null;
        string? previousProfile = null;

        try
        {
            PathSafety.RequireRegularPath(paths.SharedAuthFile);
            PathSafety.RequireRegularPath(paths.ActiveProfileFile);
            previousProfile = activeProfileStore.Read();
            var targetProfile = profileDiscovery.GetRequiredProfile(targetProfileName);
            if (string.Equals(previousProfile, targetProfile.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected profile is already active.");
            }

            if (!File.Exists(targetProfile.AuthFilePath))
            {
                throw new FileNotFoundException($"Profile '{targetProfile.Name}' does not contain auth.json.", targetProfile.AuthFilePath);
            }

            if (!File.Exists(paths.SharedAuthFile))
            {
                throw new FileNotFoundException("The shared auth.json does not exist.", paths.SharedAuthFile);
            }

            backup = backups.CreateSwitchBackup(paths.SharedAuthFile, targetProfile.AuthFilePath, previousProfile);
            string completedBackup;
            try
            {
                if (previousProfile is not null)
                {
                    var currentProfile = profileDiscovery.GetRequiredProfile(previousProfile);
                    replacer.ReplaceFromSource(paths.SharedAuthFile, currentProfile.AuthFilePath);
                }
                replacer.ReplaceFromSource(targetProfile.AuthFilePath, paths.SharedAuthFile);
                activeProfileStore.Write(targetProfile.Name);
                completedBackup = backups.CompleteSwitchBackup(backup);
            }
            catch
            {
                RestoreBackupIfPossible(backup, previousProfile);
                RestoreActiveProfile(previousProfile);
                backups.MarkRolledBack(backup);
                throw;
            }

            return new AuthSwitchResult(targetProfile.Name, previousProfile, completedBackup);
        }
        finally
        {
            switchGate.Release();
        }
    }

    public void Rollback(AuthSwitchResult result)
    {
        if (!switchGate.Wait(0))
        {
            throw new InvalidOperationException("A profile switch is already in progress.");
        }
        try
        {
            if (string.IsNullOrWhiteSpace(result.BackupPath))
            {
                throw new InvalidOperationException("The switch result does not contain a rollback backup.");
            }

            backups.RestoreCompletedSwitchBackup(result.BackupPath, paths.SharedAuthFile, paths.ActiveProfileFile);
        }
        finally { switchGate.Release(); }
    }

    private void RestoreBackupIfPossible(SwitchBackup backup, string? previousProfile)
    {
        backups.VerifyPreviousAuthentication(backup);

        new AtomicFileReplacer().ReplaceFromSource(backup.PreviousAuthFile, paths.SharedAuthFile);
        if (previousProfile is not null)
        {
            string profileAuth = profileDiscovery.GetRequiredProfile(previousProfile).AuthFilePath;
            new AtomicFileReplacer().ReplaceFromSource(backup.PreviousAuthFile, profileAuth);
        }
    }

    private void RestoreActiveProfile(string? previousProfile)
    {
        PathSafety.RequireRegularPath(paths.ActiveProfileFile);
        if (previousProfile is not null)
        {
            activeProfileStore.Write(previousProfile);
        }
        else if (File.Exists(paths.ActiveProfileFile))
        {
            File.Delete(paths.ActiveProfileFile);
        }
    }
}
