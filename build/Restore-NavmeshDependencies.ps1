[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DependencyRoot
)

$ErrorActionPreference = "Stop"
$revision = "474919d017759c39a530071a0c5b7e6eb162af7a"
$repository = "anarchydevs/aosp.knows-aosharp-mods"

$assets = @(
    @{
        Source = "NavmeshMovementController/CritterAi/cai-nav.dll"
        Destination = "CritterAi/cai-nav.dll"
        Bytes = 46592
        Sha256 = "a2e191145dd53e9480ebb21d1732aa74b280d800b532eeb10e5e9804d940aebf"
    },
    @{
        Source = "NavmeshMovementController/CritterAi/cai-nav-rcn.dll"
        Destination = "CritterAi/cai-nav-rcn.dll"
        Bytes = 142336
        Sha256 = "1a0390f7e5d44dd5e0766df134bad7f153d4afed9404d7f12e1782915b268279"
    },
    @{
        Source = "NavmeshMovementController/CritterAi/cai-util.dll"
        Destination = "CritterAi/cai-util.dll"
        Bytes = 19456
        Sha256 = "af6f5e9ff6f6a430f688b8b0be2fcd0e52ef0bece2f07f963451662a6c147fa8"
    },
    @{
        Source = "AOSharp.Navigator/NavMeshes/152.Navmesh"
        Destination = "NavMeshes/152.Navmesh"
        Bytes = 1937240
        Sha256 = "da4f46630dcae195129b99340ea63ef0e96ca22a0565ec7fbc0ada54f345b961"
    }
)

function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = [System.BitConverter]::ToString($sha.ComputeHash($stream))
            return $hash.Replace("-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-Asset([string]$Path, [long]$Bytes, [string]$Sha256) {
    if (-not [System.IO.File]::Exists($Path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path
    return $item.Length -eq $Bytes -and (Get-Sha256 $Path) -eq $Sha256
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$DependencyRoot = [System.IO.Path]::GetFullPath($DependencyRoot)

foreach ($asset in $assets) {
    $destination = Join-Path $DependencyRoot $asset.Destination
    if (Test-Asset $destination $asset.Bytes $asset.Sha256) {
        Write-Host "Verified $($asset.Destination)"
        continue
    }

    $directory = Split-Path -Parent $destination
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = "$destination.download-$([Guid]::NewGuid().ToString('N'))"
    $uri = "https://raw.githubusercontent.com/$repository/$revision/$($asset.Source)"

    try {
        Write-Host "Restoring $($asset.Destination) from pinned revision $revision"
        Invoke-WebRequest -Uri $uri -OutFile $temporary -UseBasicParsing

        if (-not (Test-Asset $temporary $asset.Bytes $asset.Sha256)) {
            throw "Downloaded asset failed size or SHA-256 verification: $($asset.Destination)"
        }

        Move-Item -LiteralPath $temporary -Destination $destination -Force
    }
    finally {
        if ([System.IO.File]::Exists($temporary)) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}
