# backup.ps1 -Env staging|production [-Label nightly] — compressed PostgreSQL backup into <env>\backups (keeps 14).
param([Parameter(Mandatory)][string]$Env, [string]$Label = 'manual')
. "$PSScriptRoot\lib.ps1"
$c = Get-Cfg $Env
$file = Join-Path $c.base ("backups\{0}-{1:yyyyMMdd-HHmmss}-{2}.dump" -f $c.database, (Get-Date), ($Label -replace '[^A-Za-z0-9._-]', '_'))
$env:PGPASSWORD = $c.dbPassword
& (Join-Path $c.pgBin 'pg_dump.exe') -h localhost -U $c.dbUser -d $c.database -Fc -f $file
$code = $LASTEXITCODE; Remove-Item Env:\PGPASSWORD
if ($code -ne 0) { throw "pg_dump failed ($code)" }
Get-ChildItem (Join-Path $c.base 'backups') -Filter '*.dump' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 14 | Remove-Item -Force
Log "Backup OK: $(Split-Path $file -Leaf) ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)"
