param(
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$AssetsFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Only the release copy is rewritten. NuGet's package cache remains pristine.
# Exact known literals make a changed dependency fail the build for review.
# GeneratePathProperty is not reliable for this build-only/excluded asset.
# NuGet's restore graph records the actual cache(s), including custom locations.
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$package = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -ceq 'Mono.Cecil/0.11.6' })
if ($package.Count -ne 1) { throw 'Mono.Cecil 0.11.6 is absent from project.assets.json; restore the host project first.' }
$cecilAssembly = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties) {
    $candidate = Join-Path (Join-Path $folder.Name $package[0].Value.path) 'lib/net40/Mono.Cecil.dll'
    if (Test-Path -LiteralPath $candidate) { $cecilAssembly = $candidate; break }
}
if (!$cecilAssembly) { throw 'Restored Mono.Cecil package does not contain lib/net40/Mono.Cecil.dll.' }
Add-Type -LiteralPath $cecilAssembly
$root = [IO.Path]::GetFullPath($RuntimeDirectory)
$clientless = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -ieq 'AOSharp.Clientless.dll' })
if ($clientless.Count -ne 1) { throw 'Expected exactly one AOSharp.Clientless assembly in the output directory.' }
$paths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$paths.Add('\AOSharp.Clientless.DLL', '/AOSharp.Clientless.dll')
$paths.Add('GameData\ItemData.bin', 'GameData/ItemData.bin')
$paths.Add('GameData\ItemData.idx', 'GameData/ItemData.idx')
$paths.Add('GameData\SkillTrickle.json', 'GameData/SkillTrickle.json')
$paths.Add('GameData\StaticDynelData.bin', 'GameData/StaticDynelData.bin')
$paths.Add('GameData\PlayfieldNames.json', 'GameData/PlayfieldNames.json')
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        Get-AllTypes $type.NestedTypes
    }
}
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.InMemory = $true
$reader.ReadSymbols = $false
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($clientless[0].FullName, $reader)
$temp = Join-Path $root ('portable-clientless-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$changed = 0
try {
    if ($assembly.Name.HasPublicKey) { throw 'Refusing to rewrite a signed clientless assembly.' }
    foreach ($type in (Get-AllTypes $assembly.MainModule.Types)) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldstr) { continue }
                $literal = [string]$instruction.Operand
                if ($paths.ContainsKey($literal)) {
                    $instruction.Operand = $paths[$literal]
                    [void]$seen.Add($paths[$literal])
                    $changed++
                } elseif ($paths.ContainsValue($literal)) {
                    [void]$seen.Add($literal)
                }
            }
        }
    }
    if ($seen.Count -ne $paths.Count) { throw "Clientless path layout changed: found $($seen.Count) of $($paths.Count) expected literals." }
    if ($changed -gt 0) { $assembly.Write($temp) }
} finally {
    $assembly.Dispose()
}
try {
    if ($changed -gt 0) { Move-Item -LiteralPath $temp -Destination $clientless[0].FullName -Force }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}

# Mono probes the assembly identity, including filename case. Windows copies
# may preserve an old destination's case, so use a two-step rename there too.
foreach ($file in @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -ieq '.dll' })) {
    try { $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) }
    catch [BadImageFormatException] { continue } # Native navigation libraries.
    $expected = $identity.Name + '.dll'
    if ($file.Name -ceq $expected) { continue }
    $destination = Join-Path $root $expected
    if ((Test-Path -LiteralPath $destination) -and $file.FullName -ine $destination) {
        throw "Conflicting assembly destination: $destination"
    }
    $intermediate = Join-Path $root ([Guid]::NewGuid().ToString('N') + '.rename')
    Move-Item -LiteralPath $file.FullName -Destination $intermediate
    try { Move-Item -LiteralPath $intermediate -Destination $destination }
    catch { Move-Item -LiteralPath $intermediate -Destination $file.FullName; throw }
}
Write-Host "Portable clientless output prepared: $changed path literals corrected; managed DLL filenames normalized."
