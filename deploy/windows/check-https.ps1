# Which IIS site answers https://<domain>, and is it the right environment? Fixes it when it is not.
#
#   powershell -ExecutionPolicy Bypass -File C:\Project\POS\bin\check-https.ps1 -Env staging -Domain pos.wateeq.com
param(
  [Parameter(Mandatory)][ValidateSet('staging', 'production')][string]$Env,
  [Parameter(Mandatory)][string]$Domain
)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$Domain = $Domain.Trim().ToLower()

Write-Host "`n== Every IIS binding" -ForegroundColor Cyan
Get-Website | ForEach-Object {
  $site = $_
  $site.bindings.Collection | ForEach-Object { "{0,-22} {1,-6} {2,-40} {3}" -f $site.name, $_.protocol, $_.bindingInformation, $site.physicalPath }
}

$wanted = $c.site
$https = @(Get-WebBinding -Protocol https | Where-Object { $_.bindingInformation -match "^\*?[^:]*:443:$([regex]::Escape($Domain))$" -or $_.bindingInformation -eq '*:443:' })
Write-Host "`n== https bindings that can answer $Domain" -ForegroundColor Cyan
if ($https.Count -eq 0) { throw "No https binding for $Domain. Run setup-https.ps1 again." }

$fixed = $false
foreach ($b in $https) {
  $owner = ($b.ItemXPath -replace ".*name='([^']+)'.*", '$1')
  "{0,-22} {1}" -f $owner, $b.bindingInformation
  if ($owner -ne $wanted -and $b.bindingInformation -match [regex]::Escape($Domain)) {
    Write-Host "   WRONG SITE: $Domain is served by '$owner' instead of '$wanted'. Moving it..." -ForegroundColor Yellow
    $thumb = (Get-ChildItem IIS:\SslBindings | Where-Object { $_.Host -eq $Domain -and $_.Port -eq 443 } | Select-Object -First 1).Thumbprint
    Remove-WebBinding -Name $owner -Protocol https -Port 443 -HostHeader $Domain
    New-WebBinding -Name $wanted -Protocol https -Port 443 -HostHeader $Domain -SslFlags 1
    if ($thumb) { (Get-WebBinding -Name $wanted -Protocol https -Port 443 -HostHeader $Domain).AddSslCertificate($thumb, 'My') }
    $fixed = $true
  }
}
if (-not (Get-WebBinding -Name $wanted -Protocol https -Port 443 -HostHeader $Domain)) {
  Write-Host "   '$wanted' has no https binding for $Domain - run setup-https.ps1 again." -ForegroundColor Yellow
}

Write-Host "`n== What each address serves" -ForegroundColor Cyan
foreach ($u in "http://localhost:$($c.webPort)/", "https://$Domain/") {
  try {
    $html = (Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 15 -Headers @{ 'Cache-Control' = 'no-cache' }).Content
    $js = [regex]::Match($html, 'assets/index-[^"'']+\.js').Value
    "{0,-36} {1}" -f $u, $(if ($js) { $js } else { '(no app bundle found)' })
  } catch { "{0,-36} ERROR {1}" -f $u, $_.Exception.Message }
}
Write-Host "Both lines must show the same assets/index-....js. If they do, the server is right and the phone/laptop has an old copy cached: close the tab and open https://$Domain again (or clear the site data once)."
if ($fixed) { Write-Host "`nFixed: $Domain now points to $wanted." -ForegroundColor Green }
