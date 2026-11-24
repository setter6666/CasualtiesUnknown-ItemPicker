param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Out = '.\ItemPicker.dll',
    [string]$Source
)

$ErrorActionPreference = 'Stop'

if (-not $Source) {
    $Source = Join-Path $PSScriptRoot 'src\ItemPicker.cs'
}
if (-not (Test-Path $Source)) { throw "source not found: $Source" }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$managed = Join-Path $GameDir 'CasualtiesUnknown_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
if (-not (Test-Path $managed)) { throw "not a Casualties: Unknown folder: $managed" }

$refs = @(
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.TextCoreTextEngineModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'Unity.TextMeshPro.dll',
    'Assembly-CSharp.dll',
    'netstandard.dll'
) | ForEach-Object { Join-Path $managed $_ }
$refs += (Join-Path $core 'BepInEx.dll')
$refs += (Join-Path $core '0Harmony.dll')

$missing = $refs | Where-Object { -not (Test-Path $_) }
if ($missing) { throw ("missing references: " + ($missing -join ', ')) }

$outPath = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
$outDir = Split-Path -Parent $outPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$cscArgs = @('/nologo', '/target:library', '/optimize+', "/out:$outPath")
foreach ($r in $refs) { $cscArgs += "/r:$r" }
$cscArgs += $Source

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host ("built: {0} ({1} B)" -f $outPath, (Get-Item $outPath).Length)
