param(
    [Parameter(Mandatory=$true)][string]$RepositoryRoot,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath.TrimEnd([char[]]'\/')
$identity = $null
$reason = 'Git was not found'
$gitCommand = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue
if (-not $gitCommand) { $gitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue }
$gitPath = if ($gitCommand) { $gitCommand.Source } else { $null }
if (-not $gitPath) {
    foreach ($installRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $installRoot) { continue }
        foreach ($relative in @('Git\cmd\git.exe', 'Programs\Git\cmd\git.exe')) {
            $candidate = Join-Path $installRoot $relative
            if (Test-Path -LiteralPath $candidate) { $gitPath = $candidate; break }
        }
        if ($gitPath) { break }
    }
}
function Invoke-RevisionGit([string[]]$GitArguments) {
    # Capture native stderr without Windows PowerShell turning it into a terminating error.
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $result = & $gitPath -C $root @GitArguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    if ($exitCode -ne 0) { throw "git $($GitArguments[0]) failed (exit $exitCode): $($result -join ' ')" }
    return ($result | ForEach-Object { $_.ToString() })
}
if ($gitPath) {
    try {
        $top = (Invoke-RevisionGit -GitArguments @('rev-parse', '--show-toplevel')) -join ''
        $top = (Resolve-Path -LiteralPath $top).ProviderPath.TrimEnd([char[]]'\/')
        if (-not [string]::Equals($top, $root, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source directory is not the Git repository root'
        }
        $revision = ((Invoke-RevisionGit -GitArguments @('rev-parse', '--verify', 'HEAD')) -join '').Trim()
        if ($revision -notmatch '^[0-9a-f]{40,64}$') { throw 'Git returned an invalid HEAD revision' }
        $changes = @(Invoke-RevisionGit -GitArguments @('status', '--porcelain', '--untracked-files=normal'))
        $identity = $revision
        if ($changes.Count -gt 0) { $identity += '-modified' }
        Write-Host "Build revision obtained from $gitPath"
    } catch { $reason = $_.Exception.Message }
}
if (-not $identity) {
    # Offline source copies still get a reproducible identity, never a guessed Git HEAD.
    # Hash build/source assets, not runtime settings, dependencies or generated outputs.
    Write-Warning "Git build revision unavailable: $reason. Using a source fingerprint; online ancestry cannot be determined."
    $files = New-Object 'System.Collections.Generic.List[string]'
    function Add-SourceFiles([string]$directory) {
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($entry.PSIsContainer) {
                if ($entry.Name -notmatch '^(obj|bin|debug|release|settings|data|\.git|\.vs|\.dependencies)$') {
                    Add-SourceFiles $entry.FullName
                }
            } else { $files.Add($entry.FullName) }
        }
    }
    foreach ($name in @('shared', 'bankers', 'plugins', 'executables', 'build')) {
        $folder = Join-Path $root $name
        if (Test-Path -LiteralPath $folder) { Add-SourceFiles $folder }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File) {
        if ($file.Extension -in @('.props', '.targets', '.sln')) { $files.Add($file.FullName) }
    }
    $files.Sort([StringComparer]::Ordinal)
    $manifest = New-Object Text.StringBuilder
    foreach ($file in $files) {
        $relative = $file.Substring($root.Length + 1).Replace('\', '/')
        [void]$manifest.Append($relative).Append(' ').Append((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash).Append("`n")
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $digest = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString())) }
    finally { $hasher.Dispose() }
    $identity = 'source-' + ([BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant())
}
$output = [IO.Path]::GetFullPath($OutputFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$source = "// Generated at build time; do not edit.`r`n[assembly: System.Reflection.AssemblyInformationalVersion(`"$identity`")]`r`n"
if (-not [IO.File]::Exists($output) -or [IO.File]::ReadAllText($output) -ne $source) {
    [IO.File]::WriteAllText($output, $source, (New-Object Text.UTF8Encoding($false)))
}
Write-Host "City Dwellers build: $identity"
