$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $repo 'scripts\BuildCommon.ps1')
$dotnet = Get-RepositoryDotnet -Repository $repo
& $dotnet restore (Join-Path $repo 'CodexProfileOverlay.sln') --locked-mode --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw "Dependency restore or NuGet vulnerability audit failed ($LASTEXITCODE)." }
$output = @(& $dotnet list (Join-Path $repo 'CodexProfileOverlay.sln') package --vulnerable --include-transitive --format json)
if ($LASTEXITCODE -ne 0) { throw "Dependency vulnerability query failed ($LASTEXITCODE)." }
$report = ($output -join [Environment]::NewLine) | ConvertFrom-Json
if ($null -eq $report.projects) { throw 'The dependency auditor did not return a project report.' }
$vulnerabilities = @($report.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $null -ne $_ } | ForEach-Object { $_.vulnerabilities } | Where-Object { $null -ne $_ })
if ($vulnerabilities.Count -gt 0) { throw "Dependency audit found $($vulnerabilities.Count) known vulnerability entries. Review the package report before release." }
Write-Host 'Dependency audit passed: no known vulnerabilities were reported for direct or transitive packages.'
