# Stop any running MiniIde.exe processes.
$procs = Get-Process MiniIde -ErrorAction SilentlyContinue
if (-not $procs) { Write-Host "MiniIde: not running"; exit 0 }
$procs | ForEach-Object { Write-Host "Stopping MiniIde PID=$($_.Id)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 300
if (Get-Process MiniIde -ErrorAction SilentlyContinue) { Write-Host "Warning: still alive"; exit 1 }
Write-Host "Stopped."
