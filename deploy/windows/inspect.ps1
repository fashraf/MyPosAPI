# MyPOS server check — READ-ONLY. Changes nothing.
# Run on the server: open PowerShell as Administrator, paste this whole file, press Enter.
# It prints a report and saves it to C:\Project\POS\server-report.txt. Send that file (or the text) to Claude.

$ErrorActionPreference = 'SilentlyContinue'
$out = New-Object System.Collections.Generic.List[string]
function H($t) { $out.Add(''); $out.Add("==== $t ====") }
function L($t) { $out.Add([string]$t) }

H 'Machine'
$os = Get-CimInstance Win32_OperatingSystem
L "$($os.Caption) $($os.Version)  build $($os.BuildNumber)"
L "Computer: $env:COMPUTERNAME   User: $env:USERNAME"
L ("RAM: {0:N1} GB total, {1:N1} GB free" -f ($os.TotalVisibleMemorySize/1MB), ($os.FreePhysicalMemory/1MB))
L ("CPU: " + (Get-CimInstance Win32_Processor | Select-Object -First 1).Name + "  x" + (Get-CimInstance Win32_Processor).Count)
Get-PSDrive -PSProvider FileSystem | ForEach-Object { L ("Drive {0}: {1:N1} GB free of {2:N1} GB" -f $_.Name, ($_.Free/1GB), (($_.Used + $_.Free)/1GB)) }

H 'C:\Project\POS (2 levels)'
if (Test-Path 'C:\Project\POS') {
  Get-ChildItem 'C:\Project\POS' -Force | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File -Force | Measure-Object Length -Sum).Sum
    L ("{0,-40} {1,10:N1} MB  {2}" -f $_.Name, ($size/1MB), $_.LastWriteTime)
    if ($_.PSIsContainer) { Get-ChildItem $_.FullName -Force | Select-Object -First 15 | ForEach-Object { L ("   - " + $_.Name + $(if ($_.PSIsContainer) { '\' } else { '' })) } }
  }
} else { L 'Folder not found' }

H 'IIS'
$iis = Get-WindowsFeature Web-Server
L ("IIS installed: " + $(if ($iis) { $iis.Installed } else { (Test-Path "$env:windir\System32\inetsrv\appcmd.exe") }))
if (Test-Path "$env:windir\System32\inetsrv\appcmd.exe") {
  $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
  L '-- Sites:'; & $appcmd list site | ForEach-Object { L "  $_" }
  L '-- Site folders:'; & $appcmd list vdir | ForEach-Object { L "  $_" }
  L '-- App pools:'; & $appcmd list apppool | ForEach-Object { L "  $_" }
  L '-- Modules of interest:'
  & $appcmd list modules | Select-String -Pattern 'AspNetCore|Rewrite|ApplicationRequestRouting|ARR' | ForEach-Object { L "  $_" }
}
L ("URL Rewrite: " + (Test-Path "$env:windir\System32\inetsrv\rewrite.dll"))
L ("ARR (proxy): " + (Test-Path "$env:ProgramFiles\IIS\Application Request Routing\requestRouter.dll"))
L ("ASP.NET Core Module V2: " + (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"))

H '.NET'
$dn = Get-Command dotnet
if ($dn) { & dotnet --list-runtimes | ForEach-Object { L "  $_" } } else { L 'dotnet not found' }

H 'SQL Server'
$inst = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
if ($inst) { $inst.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { L ("Instance: {0} -> {1}" -f $_.Name, $_.Value) } } else { L 'No SQL Server instance found in registry' }
Get-Service | Where-Object { $_.Name -like 'MSSQL*' -or $_.Name -like 'SQLAgent*' } | ForEach-Object { L ("Service: {0} [{1}]" -f $_.Name, $_.Status) }
$sqlcmd = Get-Command sqlcmd
L ("sqlcmd: " + $(if ($sqlcmd) { $sqlcmd.Source } else { 'not found' }))
if ($sqlcmd -and $inst) {
  foreach ($p in ($inst.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' })) {
    $server = if ($p.Name -eq 'MSSQLSERVER') { '.' } else { ".\$($p.Name)" }
    L "-- Databases on ${server}:"
    & sqlcmd -S $server -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT name + '  (' + CAST(CAST(SUM(size)*8/1024.0 AS DECIMAL(10,1)) AS VARCHAR) + ' MB)' FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id GROUP BY name ORDER BY name" 2>&1 | ForEach-Object { L "  $_" }
    L ("-- Login mode (1 = Windows only, 0 = mixed): " + (& sqlcmd -S $server -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT SERVERPROPERTY('IsIntegratedSecurityOnly')" 2>&1 | Select-Object -First 1))
    L ("-- Version: " + (& sqlcmd -S $server -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR) + ' ' + CAST(SERVERPROPERTY('Edition') AS VARCHAR)" 2>&1 | Select-Object -First 1))
  }
}

H 'Remote access (for GitHub deploys)'
$ssh = Get-Service sshd
L ("OpenSSH Server: " + $(if ($ssh) { "$($ssh.Status), start type $($ssh.StartType)" } else { 'not installed' }))
L ("OpenSSH capability: " + ((Get-WindowsCapability -Online -Name 'OpenSSH.Server*').State))

H 'Ports in use'
foreach ($port in 22, 80, 443, 1433, 5001, 5101, 5201, 8080) {
  $c = Get-NetTCPConnection -State Listen -LocalPort $port
  if ($c) { $c | Select-Object -First 1 | ForEach-Object { $pn = (Get-Process -Id $_.OwningProcess).ProcessName; L ("{0,5}  in use by {1} (pid {2})" -f $port, $pn, $_.OwningProcess) } }
  else { L ("{0,5}  free" -f $port) }
}

H 'Firewall (inbound allow rules for web/ssh ports)'
Get-NetFirewallRule -Direction Inbound -Enabled True -Action Allow | ForEach-Object {
  $pf = $_ | Get-NetFirewallPortFilter
  if ($pf.LocalPort -match '^(22|80|443|8080)$') { L ("{0,-45} port {1}" -f $_.DisplayName, $pf.LocalPort) }
}

H 'Tools'
foreach ($t in 'git', 'node', 'npm', 'pwsh') { $c = Get-Command $t; L ("{0,-5} {1}" -f $t, $(if ($c) { $c.Source } else { 'not found' })) }

New-Item -ItemType Directory -Force 'C:\Project\POS' | Out-Null
$out | Set-Content -Encoding UTF8 'C:\Project\POS\server-report.txt'
$out | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host 'Saved: C:\Project\POS\server-report.txt  -> send this to Claude.' -ForegroundColor Green
