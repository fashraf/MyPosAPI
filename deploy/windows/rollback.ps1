# rollback.ps1 -Env staging|production — back to the previous release (web + api) in well under a minute.
# Run again to go one more release back. The database is not touched.
param([Parameter(Mandatory)][string]$Env)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$sw = [Diagnostics.Stopwatch]::StartNew()
$logFile = Join-Path $c.base 'releases.log'
$lines = @(if (Test-Path $logFile) { Get-Content $logFile | Where-Object { $_ } })
if ($lines.Count -lt 2) { Write-Host 'Nothing to roll back to yet.'; exit 1 }
$current = $lines[-1]; $target = $lines[-2]
$tWeb = [regex]::Match($target, 'web=(\S+)').Groups[1].Value
$tApi = [regex]::Match($target, 'api=(\S+)').Groups[1].Value
Log "Rolling $Env back: $current  ->  web=$tWeb api=$tApi"
Set-Current $c 'web' $tWeb; Restart-WebAppPool -Name $c.site -ErrorAction SilentlyContinue
Set-Current $c 'api' $tApi; Restart-WebAppPool -Name "$($c.site)-api" -ErrorAction SilentlyContinue
if (Test-Health $c 90) {
  Add-Content (Join-Path $c.base 'releases.bad.log') "$current rolled-back=$((Get-Date).ToUniversalTime().ToString('s'))Z"
  $lines[0..($lines.Count - 2)] | Set-Content $logFile
  Log ("OK: $Env is back on web=$tWeb api=$tApi in {0:N0}s." -f $sw.Elapsed.TotalSeconds)
} else { Log 'WARNING: rolled-back version is not healthy.'; exit 1 }
