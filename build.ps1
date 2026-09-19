param(
    [string]$GameDir = "D:\Program\Game\Steam\steamapps\common\SimplePlanes 2",
    [switch]$InstallToGame
)

[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$artifactsDir = Join-Path $projectRoot "artifacts"
$pluginDllPath = Join-Path $artifactsDir "SimplePlanes2Rangefinder.dll"
$managedDir = Join-Path $GameDir "SimplePlanes 2_Data\Managed"
$bepInExCoreDir = Join-Path $GameDir "BepInEx\core"

function Get-CSharpCompilerPath {
    $candidates = @(
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Unable to find csc.exe from .NET Framework."
}

function Assert-RequiredFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Required file not found: $Path"
    }
}

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$sourceFiles = Get-ChildItem (Join-Path $projectRoot "src") -Filter "*.cs" | Sort-Object Name | ForEach-Object { $_.FullName }
# No game assembly is referenced on purpose. Game logic and the Funky Trees expression
# engine are both reached through reflection, so a game update cannot break plugin loading.
$references = @(
    (Join-Path $bepInExCoreDir "BepInEx.dll"),
    (Join-Path $bepInExCoreDir "0Harmony.dll"),
    (Join-Path $managedDir "netstandard.dll"),
    (Join-Path $managedDir "UnityEngine.dll"),
    (Join-Path $managedDir "UnityEngine.CoreModule.dll"),
    (Join-Path $managedDir "UnityEngine.IMGUIModule.dll"),
    (Join-Path $managedDir "UnityEngine.InputLegacyModule.dll"),
    (Join-Path $managedDir "UnityEngine.PhysicsModule.dll"),
    (Join-Path $managedDir "UnityEngine.TextRenderingModule.dll")
)

foreach ($reference in $references) {
    Assert-RequiredFile $reference
}

$referenceArgs = $references | ForEach-Object { "/r:$_" }
$csc = Get-CSharpCompilerPath

& $csc /nologo /target:library /optimize+ /out:$pluginDllPath $referenceArgs $sourceFiles
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed."
}

if ($InstallToGame) {
    $gamePluginRoot = Join-Path $GameDir "BepInEx\plugins\SimplePlanes2Rangefinder"
    New-Item -ItemType Directory -Force -Path $gamePluginRoot | Out-Null
    Copy-Item -Path $pluginDllPath -Destination (Join-Path $gamePluginRoot "SimplePlanes2Rangefinder.dll") -Force
    Write-Host "Installed to $gamePluginRoot"
}

Write-Host "Built $pluginDllPath"
