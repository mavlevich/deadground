param([string]$GodotBin = $env:GODOT_BIN, [Parameter(ValueFromRemainingArguments=$true)][string[]]$GodotArgs)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'godot-version.ps1')
$godotVersion = Get-GodotVersion -RepositoryRoot $repoRoot
if (-not $GodotBin) { $GodotBin = (Get-Command godot -ErrorAction Stop).Source }
$version = & $GodotBin --version
if ($LASTEXITCODE -ne 0 -or $version -notlike "$godotVersion.stable.mono.*") {
    throw "This project requires Godot $godotVersion .NET. Set GODOT_BIN to its executable."
}
Push-Location $repoRoot
try {
    dotnet build src/Game.Godot/Game.Godot.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $GodotBin --headless --editor --path (Join-Path $repoRoot 'src/Game.Godot') --import
    if ($LASTEXITCODE -ne 0) { throw 'Asset import failed.' }
    & $GodotBin --path (Join-Path $repoRoot 'src/Game.Godot') @GodotArgs
    if ($LASTEXITCODE -ne 0) { throw "Godot exited with code $LASTEXITCODE." }
} finally {
    Pop-Location
}
