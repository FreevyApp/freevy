# Builds a Freevy release: publishes the app and packs it with Velopack into an installer
# (Freevy-win-Setup.exe) plus the update packages the installed apps download.
#
#   powershell -NoProfile -File build\release.ps1 -Version 1.0.0
#
# Output: build\out\releases\. Upload that folder's files to https://freevy.app/releases/
# (forge-manager scripts/publish-freevy-release.js). Earlier releases are fetched from there
# first, so Velopack can build small delta updates.
#
# Releases are unsigned on purpose (see README, "Verifying a release"). Signing stays possible: pass the
# thumbprint of a code signing certificate in the current user's store (or set FREEVY_SIGN_THUMBPRINT).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Feed = 'https://freevy.app/releases',
    [string]$CertThumbprint = $env:FREEVY_SIGN_THUMBPRINT
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'out'
$publish = Join-Path $out "publish\$Version"
$releases = Join-Path $out 'releases'
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw 'vpk is missing: dotnet tool install -g vpk --version 1.2.161 --add-source https://api.nuget.org/v3/index.json'
}

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Output "== Publishing Freevy $Version"
dotnet publish (Join-Path $root 'src\Freevy.App\Freevy.App.csproj') -c Release -r win-x64 --self-contained false `
    -p:Version=$Version -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Output '== Fetching earlier releases for delta updates (fine to fail on the first release)'
vpk download http --url $Feed --outputDir $releases 2>&1 | Out-Null

$signing = @()
if ($CertThumbprint) {
    $thumb = $CertThumbprint -replace '\s', ''
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
    if (-not $cert) { throw "No certificate $thumb in the current user's store." }
    Write-Output "== Signing as: $($cert.Subject)"
    $signing = @('--signParams', "/sha1 $thumb /fd sha256 /tr http://timestamp.digicert.com /td sha256")
} else {
    Write-Output '== No certificate given: this build is unsigned'
}

Write-Output '== Packing'
# The setup installs the .NET 10 Desktop Runtime and WebView2 when the PC lacks them.
vpk pack --packId Freevy --packVersion $Version --runtime win-x64 --packDir $publish --mainExe Freevy.exe `
    --packTitle Freevy --packAuthors Freevy --icon (Join-Path $root 'src\Freevy.App\Assets\freevy.ico') `
    --framework 'net10-x64-desktop,webview2' --shortcuts 'Desktop,StartMenuRoot' --outputDir $releases `
    --instLicense (Join-Path $PSScriptRoot 'installer-terms.md') @signing
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Output "== Done: $releases"
Get-ChildItem $releases | Sort-Object LastWriteTime | ForEach-Object { '{0,-40} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
