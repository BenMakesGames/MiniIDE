# Build + launch MiniIde. Detaches so console returns.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/MiniIde/MiniIde.csproj'
& dotnet build $proj -nologo -v m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $root 'src/MiniIde/bin/Debug/net10.0/MiniIde.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe" }
Start-Process -FilePath $exe
Write-Host "Launched $exe"
