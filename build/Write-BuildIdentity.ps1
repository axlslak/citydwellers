param(
    [Parameter(Mandatory=$true)][string]$RepositoryRoot,
    [Parameter(Mandatory=$true)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$identity = 'unknown'
try {
    $git = Get-Command git -ErrorAction Stop
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $top = & $git.Source -C $root rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and [IO.Path]::GetFullPath($top).TrimEnd('\', '/') -eq $root) {
        $revision = & $git.Source -C $root rev-parse --verify HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $revision -match '^[0-9a-f]{40,64}$') {
            $changes = & $git.Source -C $root status --porcelain --untracked-files=normal 2>$null
            if ($LASTEXITCODE -eq 0) {
                $identity = $revision
                if ($changes) { $identity += '-modified' }
            }
        }
    }
} catch {
    # Source archives and machines without Git must not reuse a stale stamp.
    $identity = 'unknown'
}

$output = [IO.Path]::GetFullPath($OutputFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$source = "// Generated at build time; do not edit.`r`n[assembly: System.Reflection.AssemblyInformationalVersion(`"$identity`")]`r`n"
if (-not [IO.File]::Exists($output) -or [IO.File]::ReadAllText($output) -ne $source) {
    [IO.File]::WriteAllText($output, $source, (New-Object Text.UTF8Encoding($false)))
}
Write-Host "City Dwellers build: $identity"
