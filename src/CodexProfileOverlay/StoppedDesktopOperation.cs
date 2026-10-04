namespace CodexProfileOverlay;

internal static class StoppedDesktopOperation
{
    // Rollback must stop the new desktop too; an old shutdown guard cannot prove the new app stopped.
    public static async Task RunAsync(CodexProcessService processService, int gracefulTimeoutSeconds,
        bool allowForceClose, Action operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var guard = await processService.CloseCodexAsync(gracefulTimeoutSeconds, allowForceClose, cancellationToken).ConfigureAwait(false);
        processService.VerifyDesktopStopped(guard);
        await Task.Run(operation, cancellationToken).ConfigureAwait(false);
    }
}
