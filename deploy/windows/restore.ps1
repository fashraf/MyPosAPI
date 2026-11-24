# restore.ps1 -Env staging|production -File <name>.dump — restores a backup (stops the API meanwhile).
param([Parameter(Mandatory)][string]$Env, [Parameter(Mandatory)][string]$File)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$path = Join-Path $c.base "backups\$File"
if (-not (Test-Path $path)) { throw "No such backup: $File" }
Stop-WebAppPool -Name "$($c.site)-api" -ErrorAction SilentlyContinue
$env:PGPASSWORD = $c.dbPassword
& (Join-Path $c.pgBin 'pg_restore.exe') -h localhost -U $c.dbUser -d $c.database --clean --if-exists --no-owner $path
Remove-Item Env:\PGPASSWORD
Start-WebAppPool -Name "$($c.site)-api" -ErrorAction SilentlyContinue
if (Test-Health $c 120) { Log "Restored $File into $($c.database)." } else { Log 'Restored, but health check failed.'; exit 1 }
