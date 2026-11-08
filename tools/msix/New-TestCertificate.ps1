<#
.SYNOPSIS
  Makes a certificate of your own, to sign the package with and try it on this PC before a real certificate exists.

.DESCRIPTION
  Run in an elevated PowerShell (the certificate has to be trusted by the machine):
    pwsh tools/msix/New-TestCertificate.ps1
    pwsh tools/msix/build-msix.ps1 -Version 0.0.1 -Publisher 'CN=Armand Delessert' -CertificatePath test-certificate.pfx -CertificatePassword test
    Add-AppxPackage dist\...msixbundle

  The package only installs on a machine that trusts the certificate: this one. Nobody else's will, and a certificate made this
  way is for trying, never for shipping. Remove it afterwards from "Trusted People" (certlm.msc).
#>
param(
    [string] $Subject = 'CN=Armand Delessert',
    [string] $Password = 'test',
    [string] $PfxPath = 'test-certificate.pfx'
)

$ErrorActionPreference = 'Stop'
$certificate = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature -FriendlyName 'MSIX test certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password (ConvertTo-SecureString $Password -AsPlainText -Force) | Out-Null
$cer = [IO.Path]::ChangeExtension($PfxPath, '.cer')
Export-Certificate -Cert $certificate -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Write-Host "Certificate $Subject made, trusted by this PC, and kept in $PfxPath (password: $Password)."
