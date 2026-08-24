param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = "Stop"

# AOSharp.Clientless 1.0.16 was built from this revision. Pinning the source
# keeps the binary data paired with the clientless DLL instead of following a
# moving branch.
$sourceRevision = "5f2411cc0eea283287d356fc1147d2351a1bb1c8"
$sourceRoot = "https://gitlab.com/never-knows-best/aosharp.clientless/-/raw/" + $sourceRevision + "/AOSharp.Clientless/GameData"

$files = @(
    @{
        Name = "ItemData.bin"
        Sha256 = "40f5dec59f96828741b5c1e79573df7562b4b06e7e8c691e37d052c112e567ae"
    },
    @{
        Name = "ItemData.idx"
        Sha256 = "d308e3ec47a5aa5734c75a535a5a9e9a021dd647953f9b14b544c11a65fd8"
    },
    @{
        Name = "PlayfieldNames.json"
        Sha256 = "11611a5b782fbee5a5e0919961e68c75d59d40c3a98ee9475ad1405ec561a15a"
    },
    @{
        Name = "SkillTrickle.json"
        Sha256 = "4071a1f6ef2d7ba0a5c9ed12bdeef5ed5aebb95dfea95108fdf53b0e021ac26a"
    },
    @{
        Name = "StaticDynelData.bin"
        Sha256 = "7ddd7859c5dbf5d83f87c2e5b7676316129e877132ef629262fd7d86d2525b45"
    }
)

New-Item -ItemType Directory -Path $Destination -Force | Out-Null

# Windows PowerShell on older .NET Framework installations may otherwise try
# TLS 1.0 first, which GitLab no longer accepts.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

foreach ($file in $files) {
    $destinationPath = Join-Path $Destination $file.Name
    [string] $expectedHash = $file.Sha256
    $expectedHash = $expectedHash.Trim()
    $needsDownload = $true

    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        [string] $existingHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        $needsDownload = -not [string]::Equals(
            $existingHash.Trim(),
            $expectedHash,
            [System.StringComparison]::OrdinalIgnoreCase)
    }

    if (-not $needsDownload) {
        continue
    }

    $temporaryPath = $destinationPath + ".download"
    $sourceUrl = $sourceRoot + "/" + $file.Name

    try {
        Write-Host "Downloading AOSharp.Clientless GameData/$($file.Name)..."
        Invoke-WebRequest `
            -Uri $sourceUrl `
            -OutFile $temporaryPath `
            -UseBasicParsing

        [string] $downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash

        if (-not [string]::Equals(
                $downloadedHash.Trim(),
                $expectedHash,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "Hash verification failed for {0}. Expected {1}, received {2}." -f
                $file.Name,
                $expectedHash,
                $downloadedHash)
        }

        Move-Item -LiteralPath $temporaryPath -Destination $destinationPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Write-Host "AOSharp.Clientless GameData is ready."
