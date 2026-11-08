param(
    [string]$Inbox = (Join-Path $PSScriptRoot '..\..\FXViewer\bin\Debug\net10.0-windows\data\trading-central\direct-inbox')
)

$Inbox = [System.IO.Path]::GetFullPath($Inbox)
$done = Join-Path $Inbox 'done'
$failed = Join-Path $Inbox 'failed'
New-Item -ItemType Directory -Force $Inbox, $done, $failed | Out-Null

$files = Get-ChildItem -Path $Inbox -Filter '*.json' -File | Sort-Object Name
if (-not $files) {
    Write-Output "INBOX empty: $Inbox"
    exit 0
}

$project = Join-Path $PSScriptRoot 'Fx.TradingCentral.csproj'
$worst = 0
foreach ($file in $files) {
    Write-Output "FILE $($file.Name)"
    & dotnet run --project $project -c Release -- signal --file $file.FullName
    $code = $LASTEXITCODE
    Write-Output "EXIT $code $($file.Name)"
    $target = if ($code -eq 0 -or $code -eq 3) { $done } else { $failed }
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force
    if ($code -ne 0 -and $code -ne 3) { $worst = 1 }
    elseif ($code -eq 3 -and $worst -eq 0) { $worst = 3 }
}
exit $worst
