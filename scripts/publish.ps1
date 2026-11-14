# Publish a single-file self-contained exe:
#   scripts\publish.ps1            -> publish\ClaudeSessionManagerMCP.exe
#   scripts\publish.ps1 -Framework # -> framework-dependent build instead
param(
    [switch]$Framework
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$ridArgs = if ($Framework) {
    @("-c", "Release")
} else {
    @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
      "/p:PublishSingleFile=true")
}

dotnet publish (Join-Path $root "ClaudeSessionManagerMCP.csproj") @ridArgs -o (Join-Path $root "publish")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output ""
Write-Output "Done. Output in: $(Join-Path $root 'publish')"
