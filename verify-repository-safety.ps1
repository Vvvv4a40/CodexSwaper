param([switch]$IncludeHistory)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

$tracked = & git -C $repo ls-files
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed."
}

$forbiddenPaths = @(
    'auth.json',
    'active-profile.txt',
    'settings.json',
    'profiles.json',
    'profile-status.json',
    '.codex-profiles',
    'removed-profiles',
    'preflight-backups',
    'backups',
    'logs'
)

$authJsonShape = (('"refresh' + '_token"'), ('"access' + '_token"'), ('"id' + '_token"')) -join '|'
$windowsUserPath = 'C:\\Users\\' + [regex]::Escape($env:USERNAME) + '\\'
$macUserPath = '/Users/' + [regex]::Escape($env:USERNAME) + '/'
$patterns = @(
    @{ Name = 'authorization-header'; Regex = '(?i)authorization\s*:\s*bearer\s+[a-z0-9._~+/=-]{12,}' },
    @{ Name = 'access-token-field'; Regex = '(?i)(access|refresh|id)[_-]?token["''\s:=]+[a-z0-9._~+/=-]{12,}' },
    @{ Name = 'cookie-header'; Regex = '(?i)\bcookie\s*:\s*[^;=]+=' },
    @{ Name = 'private-key'; Regex = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    @{ Name = 'github-token'; Regex = '\bgh[pousr]_[A-Za-z0-9]{36,}|\bgithub_pat_[A-Za-z0-9_]{40,}' },
    @{ Name = 'openai-key'; Regex = '\bsk-(?:proj-)?[A-Za-z0-9_-]{32,}' },
    @{ Name = 'user-specific-path'; Regex = "(?i)$windowsUserPath|$macUserPath" },
    @{ Name = 'auth-json-shape'; Regex = "(?i)$authJsonShape" }
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($path in $tracked) {
    $normalized = $path -replace '\\', '/'
    if ($normalized -match '(?i)\.(exe|dll|zip|pdb)$|(^|/)\.env(?:\.|$)') {
        $failures.Add("generated-or-secret-file: $normalized")
    }
    foreach ($forbidden in $forbiddenPaths) {
        if ($normalized -like "*$forbidden*") {
            $failures.Add("forbidden-path: $normalized")
        }
    }

    $fullPath = Join-Path $repo $path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -gt 2MB -or $bytes.Contains([byte]0)) {
        continue
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    foreach ($pattern in $patterns) {
        $count = [regex]::Matches($text, $pattern.Regex).Count
        if ($count -gt 0) {
            $failures.Add("$($pattern.Name): $normalized ($count match(es), value redacted)")
        }
    }
}

if ($IncludeHistory) {
    $commits = @(& git -C $repo rev-list --all)
    if ($LASTEXITCODE -ne 0 -or $commits.Count -eq 0) { throw 'Cannot enumerate repository history.' }
    $historicalPaths = @(& git -C $repo log --all --name-only --format= | Where-Object { $_ } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate historical repository paths.' }
    foreach ($path in $historicalPaths) {
        $normalized = $path -replace '\\', '/'
        if ($normalized -match '(?i)(^|/)(auth\.json|active-profile\.txt|settings\.json|profiles\.json|profile-status\.json|\.codex-profiles|logs|backups|preflight-backups|removed-profiles)(/|$)|\.(exe|dll|zip|pdb)$|(^|/)\.env(?:\.|$)') {
            $failures.Add("historical-sensitive-path: $normalized")
        }
    }
    # -l returns only matching object paths; secret values never enter console output.
    $historyPattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,}|sk-(proj-)?[A-Za-z0-9_-]{32,}|authorization[[:space:]]*:[[:space:]]*bearer[[:space:]]+[A-Za-z0-9._~+/=-]{24,}'
    $historyMatches = @(& git -C $repo grep -I -i -l -E -e $historyPattern $commits 2>$null)
    if ($LASTEXITCODE -gt 1) { throw 'Repository history secret-pattern query failed.' }
    foreach ($path in $historyMatches) { $failures.Add("historical-secret-pattern: $path (value redacted)") }
    Write-Host "History heuristic scan completed for $($commits.Count) commits."
}

if ($failures.Count -gt 0) {
    Write-Error ("Repository safety scan failed:`n" + ($failures -join "`n"))
    exit 1
}

Write-Host "Repository heuristic safety scan passed. No matching credential files, generated binaries, or secret patterns were found. This is not proof that no secret exists."
$global:LASTEXITCODE = 0
