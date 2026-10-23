function Get-GodotVersion {
  param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
  [xml]$project = Get-Content (Join-Path $RepositoryRoot 'src/Game.Godot/Game.Godot.csproj') -Raw
  $sdk = $project.Project.Sdk
  if ($sdk -notmatch '^Godot\.NET\.Sdk/([0-9]+\.[0-9]+\.[0-9]+)$') {
    throw 'Game.Godot.csproj must pin one stable Godot.NET.Sdk version.'
  }
  $sdkVersion = $Matches[1]
  # Godot release tags omit the zero patch, unlike the SDK package version.
  return ($sdkVersion -replace '\.0$', '')
}
