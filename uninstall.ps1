param(
    [switch]$RemoveNonSecretSettings
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $repo 'scripts\BuildCommon.ps1')
$installRoot = Join-Path $env:LOCALAPPDATA "CodexProfileOverlay"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Codex Profile Overlay.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Codex Profile Overlay.lnk"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

foreach ($uninstallPath in @($installRoot, $startMenuShortcut, $desktopShortcut)) { Assert-NoReparsePointAncestor -Path $uninstallPath }
$installedExe = Join-Path $installRoot 'CodexProfileOverlay.exe'
$currentSession = (Get-Process -Id $PID).SessionId
Get-Process CodexProfileOverlay -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.SessionId -eq $currentSession -and $_.Path -eq $installedExe) { Stop-Process -Id $_.Id -Force }
}
foreach ($shortcut in @($startMenuShortcut, $desktopShortcut)) {
    if (Test-Path -LiteralPath $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}
if (Test-Path -LiteralPath $runKey) {
    Remove-ItemProperty -Path $runKey -Name "CodexProfileOverlay" -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $installRoot) {
    $ownedNames = @('CodexProfileOverlay.exe', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'licenses')
    if ($RemoveNonSecretSettings) { $ownedNames += @('settings.json', 'profiles.json', 'active-profile.txt', 'profile-status.json') }
    foreach ($ownedName in $ownedNames) {
        $ownedPath = [IO.Path]::GetFullPath((Join-Path $installRoot $ownedName))
        if (-not $ownedPath.StartsWith([IO.Path]::GetFullPath($installRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall path escapes the install directory.' }
        Assert-NoReparsePointAncestor -Path $ownedPath
        if (Test-Path -LiteralPath $ownedPath -PathType Container) { Assert-NoReparsePointsInTree -Path $ownedPath }
        if (Test-Path -LiteralPath $ownedPath) { Remove-Item -LiteralPath $ownedPath -Recurse -Force }
    }
}

Write-Host "Uninstalled application files. .codex, .codex-profiles, auth.json files, chats, settings, databases, and history were not removed."
