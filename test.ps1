param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $repo 'scripts\BuildCommon.ps1')
$dotnet = Get-RepositoryDotnet -Repository $repo
& $dotnet restore (Join-Path $repo 'CodexProfileOverlay.sln') --locked-mode --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw "Locked restore failed ($LASTEXITCODE)." }
& $dotnet test (Join-Path $repo 'CodexProfileOverlay.sln') -c $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
