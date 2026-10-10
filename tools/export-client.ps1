param(
    [string]$Preset = "Windows Desktop",
    [string]$Output = "",
    [string]$GodotBin = $env:GODOT_BIN
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not $GodotBin) {
    if (Get-Command godot -ErrorAction SilentlyContinue) {
        $GodotBin = (Get-Command godot).Source
    } else {
        throw 'Set GODOT_BIN to the Godot 4.7.2 .NET executable.'
    }
}

# Godot's Windows GUI executable does not provide console output for --version.
# Prefer its console companion for version checks, export logs and exit status.
if ([System.IO.Path]::GetExtension($GodotBin) -eq '.exe' -and $GodotBin -notlike '*_console.exe') {
    $consoleBin = [System.IO.Path]::ChangeExtension($GodotBin, $null) + '_console.exe'
    if (Test-Path $consoleBin) { $GodotBin = $consoleBin }
}

$version = & $GodotBin --version
if ($LASTEXITCODE -ne 0 -or $version -notlike '4.7.2.*.mono.*') {
    throw 'This project requires Godot 4.7.2 .NET. Check GODOT_BIN.'
}

if ($Preset -eq 'windows' -or $Preset -eq 'win') {
    $Preset = 'Windows Desktop'
} elseif ($Preset -eq 'macos' -or $Preset -eq 'mac') {
    $Preset = 'macOS'
}

if (-not $Output) {
    if ($Preset -eq 'macOS') {
        $Output = Join-Path $repoRoot 'build\export\macos\DeadGround.zip'
    } else {
        $Output = Join-Path $repoRoot 'build\export\windows\DeadGround.exe'
    }
} elseif (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $repoRoot $Output
}

$outputDir = Split-Path $Output -Parent
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

Push-Location $repoRoot
try {
    Write-Host "Building C# solution for Godot client..."
    dotnet build src/Game.Godot/Game.Godot.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    Write-Host "Exporting preset '$Preset' to '$Output'..."
    & $GodotBin --headless --path (Join-Path $repoRoot 'src/Game.Godot') --export-release $Preset $Output
    if ($LASTEXITCODE -ne 0) { throw "Export failed with exit code $LASTEXITCODE." }
    Write-Host "Export completed successfully: $Output"
} finally {
    Pop-Location
}
