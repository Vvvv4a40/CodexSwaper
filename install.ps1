param(
    [string]$Source = "",
    [switch]$StartWithWindows,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $repo 'scripts\BuildCommon.ps1')
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $repo "artifacts\publish"
}

$sourceExe = Join-Path $Source "CodexProfileOverlay.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "CodexProfileOverlay.exe was not found in $Source. Run .\publish.ps1 first."
}
$Source = Split-Path -Parent (Resolve-Path -LiteralPath $sourceExe).Path

$installRoot = Join-Path $env:LOCALAPPDATA "CodexProfileOverlay"
$shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startMenuShortcut = Join-Path $shortcutDir "Codex Profile Overlay.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Codex Profile Overlay.lnk"

foreach ($installPath in @($Source, $installRoot, $startMenuShortcut, $desktopShortcut)) { Assert-NoReparsePointAncestor -Path $installPath }
Assert-NoReparsePointsInTree -Path $Source
$installedExe = Join-Path $installRoot 'CodexProfileOverlay.exe'
$currentSession = (Get-Process -Id $PID).SessionId
Get-Process CodexProfileOverlay -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.SessionId -eq $currentSession -and $_.Path -eq $installedExe) { Stop-Process -Id $_.Id -Force }
}
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
foreach ($installName in @('CodexProfileOverlay.exe', 'LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'licenses')) {
    $installSource = Join-Path $Source $installName
    $installTarget = Join-Path $installRoot $installName
    Assert-NoReparsePointAncestor -Path $installTarget
    if (Test-Path -LiteralPath $installTarget -PathType Container) { Assert-NoReparsePointsInTree -Path $installTarget }
    if (Test-Path -LiteralPath $installSource -PathType Container) {
        New-Item -ItemType Directory -Path $installTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $installSource -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $installTarget -Recurse -Force
        }
    } elseif (Test-Path -LiteralPath $installSource -PathType Leaf) {
        Copy-Item -LiteralPath $installSource -Destination $installTarget -Force
    }
}

function New-CodexProfileOverlayShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.Description = "Codex Profile Overlay"
    $link.IconLocation = "$Target,0"
    $link.Save()
}

$installedExe = Join-Path $installRoot "CodexProfileOverlay.exe"
New-CodexProfileOverlayShortcut -Path $startMenuShortcut -Target $installedExe
New-CodexProfileOverlayShortcut -Path $desktopShortcut -Target $installedExe

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "CodexProfileOverlay" -Value ('"{0}"' -f $installedExe)
}

if ($Launch) {
    Start-Process -FilePath $installedExe
}

Write-Host "Installed to $installRoot"
