# MyPOS — one-time setup for a Windows Server (2019/2022) with IIS and PostgreSQL.
# Safe to run again: it only adds what is missing. It does NOT delete files, sites or databases.
# Run in PowerShell as Administrator. It asks once for the PostgreSQL "postgres" password.
#
# What it does
#  1. IIS features + URL Rewrite + ARR (reverse proxy) + .NET 8 Hosting Bundle
#  2. OpenSSH Server (so GitHub can deploy) + a deploy key (printed at the end for the GitHub secret)
#  3. Folders under C:\Project\POS\POSprod and C:\Project\POS\POSStageing (releases + "current" link)
#  4. IIS sites: production :80, staging :8080, and the two APIs on 127.0.0.1:5101 / :5201
#     ("Default Web Site" is stopped, not deleted, because it holds port 80)
#  5. PostgreSQL: login "mypos_app", databases POSMAIN (production) and POSMAIN_STAGING (staging)
#  6. Settings files with fresh random secrets, firewall rules, nightly backup task

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Root = 'C:\Project\POS'
$Envs = @(
  @{ Name = 'production'; Folder = 'POSprod';     WebPort = 80;   ApiPort = 5101; Db = 'POSMAIN';         Site = 'MyPOS-prod' },
  @{ Name = 'staging';    Folder = 'POSStageing'; WebPort = 8080; ApiPort = 5201; Db = 'POSMAIN_STAGING'; Site = 'MyPOS-staging' }
)
$Tmp = Join-Path $env:TEMP 'mypos-setup'; New-Item -ItemType Directory -Force $Tmp | Out-Null
function Step($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "    OK  $t" -ForegroundColor Green }
function Note($t) { Write-Host "    ..  $t" }
function Rnd($n) {
  $chars = [char[]]((48..57) + (65..90) + (97..122)); $bytes = New-Object byte[] $n
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}
function Get-File($url, $name) {
  $path = Join-Path $Tmp $name
  if (-not (Test-Path $path)) { Note "downloading $name"; Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $path }
  return $path
}

# ---------------------------------------------------------------- 1. IIS + modules
Step '1/6 IIS, URL Rewrite, ARR, .NET 8 Hosting Bundle'
Install-WindowsFeature Web-Server, Web-Default-Doc, Web-Static-Content, Web-Http-Errors, Web-Stat-Compression, Web-Dyn-Compression, Web-WebSockets, Web-Mgmt-Console | Out-Null
Ok 'IIS features'
if (-not (Test-Path "$env:windir\System32\inetsrv\rewrite.dll")) {
  $msi = Get-File 'https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi' 'rewrite.msi'
  Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart" -Wait
}
Ok 'URL Rewrite'
if (-not (Test-Path "$env:ProgramFiles\IIS\Application Request Routing\requestRouter.dll")) {
  $msi = Get-File 'https://download.microsoft.com/download/E/9/8/E9849D6A-020E-47E4-9FD0-A023E99B54EB/requestRouter_amd64.msi' 'arr.msi'
  Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart" -Wait
}
Ok 'Application Request Routing'
if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
  $exe = Get-File 'https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe' 'dotnet-hosting.exe'
  Start-Process $exe -ArgumentList '/install /quiet /norestart' -Wait
}
Ok '.NET 8 Hosting Bundle'
iisreset /restart | Out-Null
Import-Module WebAdministration
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.webServer/proxy' -Name 'enabled' -Value 'True'
Ok 'reverse proxy enabled'

# ---------------------------------------------------------------- 2. OpenSSH + deploy key
Step '2/6 OpenSSH Server and the GitHub deploy key'
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
  try { Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null }
  catch {
    Note 'Windows capability not available, installing OpenSSH from Microsoft GitHub release'
    $msi = Get-File 'https://github.com/PowerShell/Win32-OpenSSH/releases/download/v9.5.0.0p1-Beta/OpenSSH-Win64-v9.5.0.0.msi' 'openssh.msi'
    Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart" -Wait
  }
}
Set-Service sshd -StartupType Automatic; Start-Service sshd
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null
if (-not (Get-NetFirewallRule -Name 'MyPOS-SSH' -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -Name 'MyPOS-SSH' -DisplayName 'MyPOS SSH (GitHub deploys)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
}
$keyDir = 'C:\ProgramData\ssh'; $key = Join-Path $keyDir 'mypos_deploy'
$keygen = (Get-Command ssh-keygen.exe -ErrorAction SilentlyContinue).Source
if (-not $keygen) { $keygen = "$env:windir\System32\OpenSSH\ssh-keygen.exe" }
if (-not (Test-Path "$key.pub")) { & $keygen -t ed25519 -f $key -N '""' -C 'github-mypos-deploy' | Out-Null }
$auth = Join-Path $keyDir 'administrators_authorized_keys'
$pub = (Get-Content "$key.pub" -Raw).Trim()
if (-not (Test-Path $auth) -or -not ((Get-Content $auth -Raw) -match [regex]::Escape($pub))) { Add-Content -Path $auth -Value $pub }
icacls $auth /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
Ok 'sshd running on port 22, deploy key authorised'

# ---------------------------------------------------------------- 3 + 4. folders and IIS sites
Step '3/6 Folders  |  4/6 IIS sites'
New-Item -ItemType Directory -Force "$Root\bin", "$Root\artifacts" | Out-Null
foreach ($e in $Envs) {
  $base = Join-Path $Root $e.Folder
  foreach ($app in 'web', 'api') {
    $rel = Join-Path $base "$app\releases\initial"
    New-Item -ItemType Directory -Force $rel | Out-Null
    $cur = Join-Path $base "$app\current"
    if (-not (Test-Path $cur)) { cmd /c mklink /J "$cur" "$rel" | Out-Null }
  }
  New-Item -ItemType Directory -Force "$base\backups", "$base\logs", "$base\config" | Out-Null
  $idx = Join-Path $base 'web\releases\initial\index.html'
  if (-not (Test-Path $idx)) { "<!doctype html><title>MyPOS $($e.Name)</title><h2 style='font-family:sans-serif'>MyPOS $($e.Name): waiting for the first deploy from GitHub</h2>" | Set-Content -Encoding UTF8 $idx }

  foreach ($pool in "$($e.Site)", "$($e.Site)-api") {
    if (-not (Test-Path "IIS:\AppPools\$pool")) { New-WebAppPool -Name $pool | Out-Null }
    Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'AlwaysRunning'
  }
  if ($e.WebPort -eq 80 -and (Get-Website -Name 'Default Web Site' -ErrorAction SilentlyContinue)) {
    Stop-Website -Name 'Default Web Site' -ErrorAction SilentlyContinue
    Set-ItemProperty 'IIS:\Sites\Default Web Site' -Name serverAutoStart -Value $false
    Note "'Default Web Site' stopped (kept) to free port 80"
  }
  if (-not (Get-Website -Name $e.Site -ErrorAction SilentlyContinue)) {
    New-Website -Name $e.Site -Port $e.WebPort -PhysicalPath (Join-Path $base 'web\current') -ApplicationPool $e.Site | Out-Null
  }
  if (-not (Get-Website -Name "$($e.Site)-api" -ErrorAction SilentlyContinue)) {
    New-Website -Name "$($e.Site)-api" -IPAddress '127.0.0.1' -Port $e.ApiPort -PhysicalPath (Join-Path $base 'api\current') -ApplicationPool "$($e.Site)-api" | Out-Null
  }
  Start-Website -Name $e.Site -ErrorAction SilentlyContinue
  if ($e.WebPort -ne 80 -and -not (Get-NetFirewallRule -Name "MyPOS-$($e.WebPort)" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name "MyPOS-$($e.WebPort)" -DisplayName "MyPOS $($e.Name) web $($e.WebPort)" -Direction Inbound -Protocol TCP -LocalPort $e.WebPort -Action Allow | Out-Null
  }
  Ok "$($e.Name): $base  ->  http://*:$($e.WebPort)  (API 127.0.0.1:$($e.ApiPort))"
}

# ---------------------------------------------------------------- 5. PostgreSQL
Step '5/6 PostgreSQL'
$psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $psql) { throw 'psql.exe not found under C:\Program Files\PostgreSQL. Install PostgreSQL (with command line tools) and run this again.' }
$pgBin = Split-Path $psql.FullName
Note "using $pgBin"
$appPwFile = "$Root\bin\.db-app-password"
if (Test-Path $appPwFile) { $appPw = (Get-Content $appPwFile -Raw).Trim() } else { $appPw = Rnd 28; Set-Content -NoNewline $appPwFile $appPw; icacls $appPwFile /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null }
$sec = Read-Host 'PostgreSQL password for the "postgres" user (set when you installed PostgreSQL)' -AsSecureString
$env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
function Pg($sql, $db = 'postgres') {
  $r = & $psql.FullName -h localhost -U postgres -d $db -v ON_ERROR_STOP=1 -tAc $sql 2>&1
  if ($LASTEXITCODE -ne 0) { throw "PostgreSQL: $r" }
  return $r
}
if (-not (Pg "SELECT 1 FROM pg_roles WHERE rolname='mypos_app'")) { Pg "CREATE ROLE mypos_app LOGIN PASSWORD '$appPw'" | Out-Null } else { Pg "ALTER ROLE mypos_app LOGIN PASSWORD '$appPw'" | Out-Null }
foreach ($e in $Envs) {
  # Match the existing database whatever its letter case (pgAdmin keeps "POSMAIN", psql folds to posmain).
  $found = Pg "SELECT datname FROM pg_database WHERE lower(datname) = lower('$($e.Db)') LIMIT 1"
  if ($found) { $e.Db = "$found".Trim(); Pg "ALTER DATABASE `"$($e.Db)`" OWNER TO mypos_app" | Out-Null; Note "database $($e.Db) exists (kept, owner set to mypos_app)" }
  else { Pg "CREATE DATABASE `"$($e.Db)`" OWNER mypos_app ENCODING 'UTF8' TEMPLATE template0" | Out-Null; Note "created database $($e.Db)" }
  Pg "GRANT ALL ON SCHEMA public TO mypos_app" $e.Db | Out-Null
}
Remove-Item Env:\PGPASSWORD
Ok 'login mypos_app; databases POSMAIN and POSMAIN_STAGING'

# ---------------------------------------------------------------- 6. settings, backup task
Step '6/6 Settings and nightly backup'
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | Select-Object -First 1).IPAddress
foreach ($e in $Envs) {
  $cfg = Join-Path $Root "$($e.Folder)\config\mypos.json"
  if (-not (Test-Path $cfg)) {
    $url = "http://$ip" + $(if ($e.WebPort -ne 80) { ":$($e.WebPort)" } else { '' })
    [ordered]@{
      environment = $e.Name; folder = $e.Folder; site = $e.Site; webPort = $e.WebPort; apiPort = $e.ApiPort; publicUrl = $url
      database = $e.Db; dbUser = 'mypos_app'; dbPassword = $appPw; pgBin = $pgBin; jwtSigningKey = (Rnd 64); keepReleases = 5
    } | ConvertTo-Json | Set-Content -Encoding UTF8 $cfg
    icacls $cfg /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
    Note "created $cfg"
  }
}
foreach ($e in $Envs) {
  $task = "MyPOS nightly backup ($($e.Name))"
  if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File $Root\bin\backup.ps1 $($e.Name) nightly"
    $trg = New-ScheduledTaskTrigger -Daily -At ($(if ($e.Name -eq 'production') { '02:30' } else { '03:00' }))
    Register-ScheduledTask -TaskName $task -Action $act -Trigger $trg -User 'SYSTEM' -RunLevel Highest | Out-Null
  }
}
Ok 'nightly backups 02:30 (production) and 03:00 (staging)'

# ---------------------------------------------------------------- summary
Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host ' MyPOS server is ready.' -ForegroundColor Green
Write-Host "   Production : http://$ip        (folder $Root\POSprod)"
Write-Host "   Staging    : http://${ip}:8080   (folder $Root\POSStageing)"
Write-Host '   Both show "waiting for the first deploy from GitHub" until the first deploy.'
Write-Host ''
Write-Host ' Add these in GitHub, in BOTH repos (MyPOSFO and MyPosAPI):' -ForegroundColor Yellow
Write-Host '   Settings > Secrets and variables > Actions > Secrets:'
Write-Host "     DEPLOY_HOST_STAGING     = $ip"
Write-Host "     DEPLOY_HOST_PRODUCTION  = $ip"
Write-Host '     DEPLOY_USER             = Administrator'
Write-Host '     DEPLOY_SSH_KEY          = everything between the two lines below (including BEGIN/END)'
Write-Host '   Settings > Secrets and variables > Actions > Variables:'
Write-Host '     DEPLOY_TARGET           = windows'
Write-Host '----------------------------------------------------------------'
Get-Content $key | ForEach-Object { Write-Host $_ }
Write-Host '----------------------------------------------------------------'
Write-Host ' Copy the key now. Then delete it from the server with:' -ForegroundColor Yellow
Write-Host "   Remove-Item '$key'"
