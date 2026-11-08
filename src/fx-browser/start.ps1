$port = if ($env:FX_BROWSER_PORT) { $env:FX_BROWSER_PORT } else { 9333 }
$status = "http://127.0.0.1:$port/status"

try {
    Invoke-RestMethod $status -TimeoutSec 3 | Out-Null
    Write-Output "already running on port $port"
    exit 0
} catch {}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:LOCALAPPDATA 'fx-browser\server.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Start-Process -FilePath 'node' -ArgumentList 'server.mjs' -WorkingDirectory $dir `
    -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"

for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        Invoke-RestMethod $status -TimeoutSec 3 | Out-Null
        Write-Output "started on port $port"
        exit 0
    } catch {}
}
Write-Output "failed to start, see $log.err"
exit 1
