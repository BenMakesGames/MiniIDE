# Publish MiniIde as framework-dependent single-file exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/MiniIde/MiniIde.csproj'
$out  = Join-Path $root 'publish/singlefile'
& dotnet publish $proj -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $out 'MiniIde.exe'
$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host "Published: $exe ($sizeMB MB)"
