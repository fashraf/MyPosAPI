# Put a domain with a free HTTPS certificate (Let's Encrypt, auto-renewing) in front of one environment.
#
#   powershell -ExecutionPolicy Bypass -File C:\Project\POS\bin\setup-https.ps1 -Env staging -Domain pos.wateeq.com -Email you@example.com
#
# Before running: the domain's DNS "A" record must already point at this server (GoDaddy -> DNS -> A record).
# Safe to run again (renewal is automatic; re-running only repairs what is missing).
param(
  [Parameter(Mandatory)][ValidateSet('staging', 'production')][string]$Env,
  [Parameter(Mandatory)][string]$Domain,
  [Parameter(Mandatory)][string]$Email
)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$Domain = $Domain.Trim().ToLower()
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

Step "1/6 DNS: $Domain"
$dns = @(Resolve-DnsName $Domain -Type A -ErrorAction SilentlyContinue | Where-Object { $_.Type -eq 'A' } | ForEach-Object { $_.IPAddress })
$mine = @(Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object { $_.IPAddress })
try { $mine += (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10) } catch { }
if ($dns.Count -eq 0) { throw "$Domain has no A record yet. In GoDaddy add: Type A, Name pos, Value <this server's IP>. Wait 5-30 min, then run again." }
if (-not ($dns | Where-Object { $mine -contains $_ })) { throw "$Domain points to $($dns -join ', '), not to this server ($($mine -join ', ')). Fix the A record and wait." }
Write-Host "   OK: $Domain -> $($dns -join ', ')"

Step "2/6 IIS: http://$Domain on site $($c.site)"
$site = Get-Website -Name $c.site
if (-not $site) { throw "IIS site $($c.site) not found - run setup.ps1 first." }
if (-not (Get-WebBinding -Name $c.site -Protocol http -Port 80 -HostHeader $Domain)) {
  New-WebBinding -Name $c.site -Protocol http -Port 80 -HostHeader $Domain
}
Write-Host "   OK"

Step "3/6 Firewall: ports 80 and 443"
foreach ($p in 80, 443) {
  if (-not (Get-NetFirewallRule -Name "MyPOS-$p" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name "MyPOS-$p" -DisplayName "MyPOS web $p" -Direction Inbound -Protocol TCP -LocalPort $p -Action Allow | Out-Null
  }
}
Write-Host "   OK"

Step "4/6 win-acme (Let's Encrypt client)"
$tools = Join-Path $Root 'tools\win-acme'
$wacs = Join-Path $tools 'wacs.exe'
if (-not (Test-Path $wacs)) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/win-acme/win-acme/releases/latest' -Headers @{ 'User-Agent' = 'mypos' }
  $asset = $rel.assets | Where-Object { $_.name -match 'x64\.trimmed\.zip$' } | Select-Object -First 1
  if (-not $asset) { throw "Could not find the win-acme download. Get it from https://www.win-acme.com and unzip to $tools" }
  New-Item -ItemType Directory -Force $tools | Out-Null
  $zip = Join-Path $env:TEMP $asset.name
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
  Expand-Archive $zip -DestinationPath $tools -Force
}
Write-Host "   OK: $wacs"

Step "5/6 Certificate for $Domain (adds the https binding and a daily renewal task)"
& $wacs --source iis --siteid $site.id --host $Domain --installation iis --emailaddress $Email --accepttos
if ($LASTEXITCODE -ne 0) { throw "win-acme failed (see above). Most common: port 80 blocked by a cloud firewall, or DNS not pointing here yet." }

Step "6/6 Redirect http://$Domain to https"
$cfgFile = Join-Path $c.base 'config\mypos.json'
$json = Get-Content $cfgFile -Raw | ConvertFrom-Json
$json | Add-Member -NotePropertyName httpsHost -NotePropertyValue $Domain -Force
$json | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $cfgFile
$c | Add-Member -NotePropertyName httpsHost -NotePropertyValue $Domain -Force
Write-WebConfig $c (Join-Path $c.base 'web\current')

Start-Sleep -Seconds 2
try {
  $h = Invoke-WebRequest -Uri "https://$Domain/health" -UseBasicParsing -TimeoutSec 20
  Write-Host "`nDONE: https://$Domain is live (health $($h.StatusCode))." -ForegroundColor Green
} catch {
  Write-Host "`nCertificate installed, but https://$Domain/health did not answer yet: $($_.Exception.Message)" -ForegroundColor Yellow
}
Write-Host "The order light and http://<ip>:$($c.webPort) keep working as before."
