$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot 'tools/godot-version.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('deadground-version-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'src/Game.Godot') -Force | Out-Null
try {
  foreach ($sdkVersion in @('4.8.0', '4.8.1', '5.0.0')) {
    '<Project Sdk="Godot.NET.Sdk/' + $sdkVersion + '"></Project>' | Set-Content (Join-Path $fixture 'src/Game.Godot/Game.Godot.csproj')
    $expected = $sdkVersion -replace '\.0$', ''
    $actual = Get-GodotVersion -RepositoryRoot $fixture
    if ($actual -ne $expected) { throw "SDK $sdkVersion produced $actual instead of $expected." }
  }
  foreach ($badVersion in @('4.8.0-beta1', '4.8', 'latest', '')) {
    '<Project Sdk="Godot.NET.Sdk/' + $badVersion + '"></Project>' | Set-Content (Join-Path $fixture 'src/Game.Godot/Game.Godot.csproj')
    $rejected = $false
    try { Get-GodotVersion -RepositoryRoot $fixture | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Version helper accepted invalid pin: $badVersion" }
  }
  Write-Output 'TOOLCHAIN_VERSION_SMOKE_OK'
} finally {
  Remove-Item -Path $fixture -Recurse -Force
}
