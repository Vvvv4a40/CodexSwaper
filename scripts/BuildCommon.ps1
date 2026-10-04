$ErrorActionPreference = 'Stop'

function Get-RepositoryDotnet {
    param([Parameter(Mandatory = $true)][string]$Repository)
    $localDotnet = Join-Path $Repository '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { return $localDotnet }
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw 'Install Microsoft .NET SDK 8.0.425 or place it in the repository-local .dotnet folder.' }
    return $command.Source
}

function Assert-NoReparsePointAncestor {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a path through a symbolic link or junction: $candidate"
            }
        }
        $parent = [IO.Directory]::GetParent($candidate)
        if (-not $parent) { break }
        $candidate = $parent.FullName
    }
}

function Get-ValidatedPublishTarget {
    param([Parameter(Mandatory = $true)][string]$Repository, [string]$Output)
    $repositoryPath = [IO.Path]::GetFullPath($Repository)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryPath 'artifacts'))
    if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $artifactRoot 'publish' }
    if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $repositoryPath $Output }
    $target = [IO.Path]::GetFullPath($Output).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish output must be a child directory of this repository artifacts folder.'
    }
    Assert-NoReparsePointAncestor -Path $target
    if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw 'Publish output must be a directory.'
    }
    return $target
}

function Remove-OwnedStagingDirectory {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$ArtifactRoot)
    $target = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($ArtifactRoot)
    if ([IO.Path]::GetDirectoryName($target) -ne $root -or [IO.Path]::GetFileName($target) -notmatch '^\.codexswaper-(stage|rollback)-[a-f0-9]{32}$') {
        throw 'Refusing cleanup of a directory not allocated by this publisher.'
    }
    Assert-NoReparsePointAncestor -Path $target
    if (Test-Path -LiteralPath $target) {
        Assert-NoReparsePointsInTree -Path $target
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

function Assert-NoReparsePointsInTree {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePointAncestor -Path $Path
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push([IO.Path]::GetFullPath($Path))
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a directory tree containing a symbolic link or junction: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
        }
    }
}
