param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $repo 'scripts\BuildCommon.ps1')
$Output = Get-ValidatedPublishTarget -Repository $repo -Output $Output
$artifactRoot = Join-Path $repo 'artifacts'
$zip = Join-Path $artifactRoot 'CodexProfileOverlay-win-x64-portable.zip'
$checksums = Join-Path $artifactRoot 'SHA256SUMS.txt'
$ownerMarker = Join-Path $artifactRoot '.codexswaper-artifacts-owned'
$outputMarker = Join-Path $Output '.codexswaper-publish-owned'
$ownership = 'CodexSwaper publisher output v1'
foreach ($path in @($zip, $checksums, $ownerMarker, $outputMarker)) { Assert-NoReparsePointAncestor -Path $path }
if (Test-Path -LiteralPath $Output) {
    if (-not (Test-Path -LiteralPath $outputMarker -PathType Leaf) -or (Get-Content -LiteralPath $outputMarker -Raw).Trim() -ne $ownership) {
        throw 'Existing output is not owned by this publisher. Choose a new artifacts child directory; user files will not be removed.'
    }
    Assert-NoReparsePointsInTree -Path $Output
}
if ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $checksums)) {
    if (-not (Test-Path -LiteralPath $ownerMarker -PathType Leaf) -or (Get-Content -LiteralPath $ownerMarker -Raw).Trim() -ne $ownership) {
        throw 'Existing ZIP/checksum files are not owned by this publisher. Preserve them before publishing.'
    }
}
$dotnet = Get-RepositoryDotnet -Repository $repo
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $artifactRoot ('.codexswaper-stage-' + $token)
$rollback = Join-Path $artifactRoot ('.codexswaper-rollback-' + $token)
$stagePublish = Join-Path $stage 'publish'
$stageZip = Join-Path $stage (Split-Path -Leaf $zip)
$stageChecksums = Join-Path $stage (Split-Path -Leaf $checksums)
$backups = New-Object 'System.Collections.Generic.List[object]'
$installed = New-Object 'System.Collections.Generic.List[string]'
$committed = $false
$restored = $false
Assert-NoReparsePointAncestor -Path $stage
Assert-NoReparsePointAncestor -Path $rollback
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $rollback)) { throw 'Unique staging directory already exists.' }
New-Item -ItemType Directory -Path $stagePublish -Force | Out-Null
Push-Location $repo
try {
    & $dotnet restore (Join-Path $repo 'CodexProfileOverlay.sln') --locked-mode --configfile (Join-Path $repo 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed ($LASTEXITCODE)." }
    & $dotnet publish (Join-Path $repo 'src\CodexProfileOverlay\CodexProfileOverlay.csproj') `
        -c $Configuration -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -p:Platform=x64 -o $stagePublish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE); previous artifacts were preserved." }
    $exe = Join-Path $stagePublish 'CodexProfileOverlay.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published executable is missing.' }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stagePublish 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\THIRD_PARTY_NOTICES.md') -Destination (Join-Path $stagePublish 'THIRD_PARTY_NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\licenses') -Destination (Join-Path $stagePublish 'licenses') -Recurse
    Assert-NoReparsePointsInTree -Path $stagePublish
    Compress-Archive -LiteralPath @((Join-Path $stagePublish 'CodexProfileOverlay.exe'), (Join-Path $stagePublish 'LICENSE.txt'), (Join-Path $stagePublish 'THIRD_PARTY_NOTICES.txt'), (Join-Path $stagePublish 'licenses')) -DestinationPath $stageZip
    $checksumLines = @(
        '{0}  {1}' -f (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash, (Split-Path -Leaf $exe)
        '{0}  {1}' -f (Get-FileHash -LiteralPath $stageZip -Algorithm SHA256).Hash, (Split-Path -Leaf $zip)
    )
    Set-Content -LiteralPath $stageChecksums -Value $checksumLines -Encoding ascii
    Set-Content -LiteralPath (Join-Path $stagePublish '.codexswaper-publish-owned') -Value $ownership -Encoding ascii
    # Build and archive validation complete before replacing any previous output.
    foreach ($path in @($Output, $zip, $checksums)) {
        Assert-NoReparsePointAncestor -Path $path
        if (Test-Path -LiteralPath $path) {
            New-Item -ItemType Directory -Path $rollback -Force | Out-Null
            $backup = Join-Path $rollback ('item-' + $backups.Count)
            Move-Item -LiteralPath $path -Destination $backup
            $backups.Add([pscustomobject]@{ Original = $path; Backup = $backup })
        }
    }
    foreach ($replacement in @(
        [pscustomobject]@{ Source = $stagePublish; Target = $Output },
        [pscustomobject]@{ Source = $stageZip; Target = $zip },
        [pscustomobject]@{ Source = $stageChecksums; Target = $checksums }
    )) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $replacement.Target) -Force | Out-Null
        Move-Item -LiteralPath $replacement.Source -Destination $replacement.Target
        $installed.Add($replacement.Target)
    }
    Set-Content -LiteralPath $ownerMarker -Value $ownership -Encoding ascii
    $committed = $true
    Write-Host "Published to $Output"
    Write-Host "Portable ZIP: $zip"
    Write-Host "Checksums: $checksums"
} catch {
    $publishFailure = $_
    try {
        foreach ($path in $installed) {
            Assert-NoReparsePointAncestor -Path $path
            if (Test-Path -LiteralPath $path -PathType Container) { Assert-NoReparsePointsInTree -Path $path }
            # Only outputs placed by this invocation are removed during rollback.
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        foreach ($backup in $backups) { Move-Item -LiteralPath $backup.Backup -Destination $backup.Original }
        $restored = $true
    } catch {
        Write-Warning "Automatic restoration failed. Preserved previous output at $rollback."
    }
    throw $publishFailure
} finally {
    Pop-Location
    Remove-OwnedStagingDirectory -Path $stage -ArtifactRoot $artifactRoot
    if ($committed -or $restored) { Remove-OwnedStagingDirectory -Path $rollback -ArtifactRoot $artifactRoot }
}
