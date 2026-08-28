$ErrorActionPreference = 'SilentlyContinue'
$dll = 'D:\KalOS-consumer\KalOS.dll'
$b = [System.IO.File]::ReadAllBytes($dll)
$s = [System.Text.Encoding]::UTF8.GetString($b)

Write-Output "=== feature markers in running consumer build ==="
foreach ($n in @('ReadApplyLog','ShowUpdateLogIfAny','ShowUpdateLogDialog','DownloadProgress','IsDownloading','UpdateStatusText','RunStartupCheck','CheckForUpdatesAsync','DownloadAndInstallAsync','SaveLastUpdateRecord','AutoCheckForUpdates','Apply log','Consolas','View apply log')) {
    Write-Output ("  {0,-28} {1}" -f $n, $s.Contains($n))
}
Write-Output ""
Write-Output "=== update.log content ==="
if (Test-Path "$env:LOCALAPPDATA\KalOS\updates\update.log") { Get-Content "$env:LOCALAPPDATA\KalOS\updates\update.log" } else { Write-Output "not present" }
Write-Output ""
Write-Output "=== release state (anonymous, what consumer sees) ==="
try {
    $r = Invoke-RestMethod -Uri 'https://api.github.com/repos/1k09-byte/KalOSTOOLKIT/releases/latest' -Headers @{ 'User-Agent' = 'KalOS-check' } -TimeoutSec 20
    Write-Output ("  latest tag: " + $r.tag_name)
    foreach ($a in $r.assets) { Write-Output ("  asset: " + $a.name + " (" + $a.size + " bytes)") }
} catch { Write-Output ("  API check failed: " + $_.Exception.Message) }
