<#
.SYNOPSIS
  Builds the MSIX package of the application for x64 and ARM64 and bundles the two into one .msixbundle, optionally signed.

.DESCRIPTION
  Run from the repository root, on Windows with the Windows SDK (makeappx, signtool):
    pwsh tools/msix/build-msix.ps1 -Version 1.2.3
    pwsh tools/msix/build-msix.ps1 -Version 1.2.3 -CertificatePath my.pfx -CertificatePassword ...

  Version is the release's (1.2.3, or 1.2.3-beta.1: the suffix is dropped), written into the manifest as 1.2.3.0.
  Publisher is the subject of the certificate the package is signed with, "CN=..." exactly as the certificate has it; it is
  written into the manifest too. Without a certificate the bundle is not signed and Windows will not install it: tools/msix/
  New-TestCertificate.ps1 makes one for trying it on this PC.
  The manifest of the repository is put back as it was.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Publisher,
    [string] $OutputFolder = 'dist',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string[]] $Platforms = @('x64', 'arm64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
Set-Location $root

$app = Get-ChildItem src -Directory -Filter '*.App' | Select-Object -First 1
$project = Get-ChildItem $app.FullName -Filter '*.csproj' | Select-Object -First 1
$manifestPath = Join-Path $app.FullName 'Package.appxmanifest'
[xml] $manifest = Get-Content $manifestPath -Raw
$name = $manifest.Package.Identity.Name

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "Version '$Version': expected 1.2.3 or 1.2.3-suffix." }
$packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

function Find-SdkTool([string] $tool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Recurse -Filter $tool -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "$tool not found under $kits: install the Windows SDK." }
    $found.FullName
}

$original = Get-Content $manifestPath -Raw
try {
    # The manifest, with this version and this publisher.
    $text = $original -replace '(<Identity [^>]*Version=")[^"]*(")', "`${1}$packageVersion`${2}"
    if ($Publisher) { $text = $text -replace '(<Identity [^>]*Publisher=")[^"]*(")', "`${1}$Publisher`${2}" }
    [IO.File]::WriteAllText($manifestPath, $text, (New-Object Text.UTF8Encoding $false))

    $work = Join-Path $root 'artifacts\msix'
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    $bundleInput = Join-Path $work 'bundle'
    New-Item -ItemType Directory -Force $bundleInput | Out-Null

    foreach ($platform in $Platforms) {
        $packages = Join-Path $work $platform
        $buildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
        dotnet build $project.FullName --configuration Release `
            -p:MsixPackage=true -p:Platform=$buildPlatform -p:RuntimeIdentifier="win-$platform" -p:Version=$Version `
            -p:AppxPackageDir="$packages\"
        if ($LASTEXITCODE -ne 0) { throw "Building the $platform package failed." }
        $msix = Get-ChildItem $packages -Recurse -Filter '*.msix' | Select-Object -First 1
        if (-not $msix) { throw "No .msix came out of the $platform build, under $packages." }
        Copy-Item $msix.FullName $bundleInput
    }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $original, (New-Object Text.UTF8Encoding $false))
}

New-Item -ItemType Directory -Force $OutputFolder | Out-Null
$bundle = Join-Path $OutputFolder "$($app.Name -replace '\.App$', '')-$Version.msixbundle"
Remove-Item $bundle -ErrorAction SilentlyContinue
& (Find-SdkTool 'makeappx.exe') bundle /d $bundleInput /p $bundle /bv $packageVersion /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx could not bundle the packages.' }

if ($CertificatePath) {
    & (Find-SdkTool 'signtool.exe') sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr $TimestampUrl /td SHA256 $bundle
    if ($LASTEXITCODE -ne 0) { throw 'signtool could not sign the bundle.' }
    Write-Host "Signed: $bundle"
}
else {
    Write-Host "Not signed: $bundle"
}
Write-Output $bundle
