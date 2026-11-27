# Free HTTPS certificate (Let's Encrypt, auto-renewing) for one address on a site:
#   admin.wateeq.com on the POSSA site, or a client such as pos1.wateeq.com on the clients' site.
#
#   powershell -ExecutionPolicy Bypass -File C:\Project\POS\bin\add-https-host.ps1 -Env production -Address pos1.wateeq.com -Email you@example.com
#   powershell -ExecutionPolicy Bypass -File C:\Project\POS\bin\add-https-host.ps1 -Env production -Address admin.wateeq.com -Email you@example.com -Site MyPOS-admin
param(
  [Parameter(Mandatory)][ValidateSet('staging', 'production')][string]$Env,
  [Parameter(Mandatory)][string]$Address,
  [Parameter(Mandatory)][string]$Email,
  [string]$Site
)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
if (-not $Site) { $Site = $c.site }
$Address = $Address.Trim().ToLower()
$s = Get-Website -Name $Site
if (-not $s) { throw "IIS site $Site not found." }
# A specific http binding lets Let's Encrypt check the address (a wildcard binding alone is not enough for that).
if (-not (Get-WebBinding -Name $Site -Protocol http -Port 80 -HostHeader $Address)) { New-WebBinding -Name $Site -Protocol http -Port 80 -HostHeader $Address }
$wacs = Join-Path $Root 'tools\win-acme\wacs.exe'
if (-not (Test-Path $wacs)) { throw "win-acme is missing. Run setup-https.ps1 once, or unzip win-acme (x64) to $(Split-Path $wacs)." }
& $wacs --source iis --siteid $s.id --host $Address --installation iis --emailaddress $Email --accepttos
if ($LASTEXITCODE -ne 0) { throw "Certificate failed. Check that $Address points to this server (GoDaddy DNS) and port 80 is open." }
Write-Host "DONE: https://$Address" -ForegroundColor Green
