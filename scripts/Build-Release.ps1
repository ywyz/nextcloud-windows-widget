#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [string]$Dotnet = 'dotnet',
    [string]$SignTool,
    [string]$TimeStampUrl = 'http://time.certum.pl'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo "artifacts/publish-$Version"
$release = Join-Path $repo "artifacts/release-$Version"
if (!$SignTool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $SignTool = Get-ChildItem -Path "$kits/*/x64/signtool.exe" |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$SignTool) { throw 'Windows SDK SignTool is required.' }
$cert = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
if (!$cert.HasPrivateKey) { throw 'Signing key is unavailable.' }
$pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.PublicKey.ExportSubjectPublicKeyInfo()))
$trustSource = Get-Content -LiteralPath (Join-Path $repo 'src/NextcloudWidget.Updates/PublisherTrust.cs') -Raw
if (!$trustSource.Contains($pin)) { throw 'Signing certificate does not match the trusted release publisher.' }
$project = Join-Path $repo 'src/NextcloudWidget.App/NextcloudWidget.App.csproj'
& $Dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $repo "artifacts/build-$Version") -p:Version=$Version -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $publish --nologo
if ($LASTEXITCODE) { throw 'Publish failed.' }
[IO.Directory]::CreateDirectory($release) | Out-Null
$asset = 'NextcloudWidget-win-x64.exe'
$exe = Join-Path $release $asset
Copy-Item -LiteralPath (Join-Path $publish $asset) -Destination $exe -Force
& $SignTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimeStampUrl /td SHA256 $exe
if ($LASTEXITCODE) { throw 'Authenticode signing failed.' }
& $SignTool verify /pa /all /tw $exe
if ($LASTEXITCODE) { throw 'Windows rejected the executable signature or timestamp.' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
$manifest = [ordered]@{ product = 'Nextcloud Windows Widget'; version = $Version; asset = $asset; size = (Get-Item -LiteralPath $exe).Length; sha256 = $hash }
$path = Join-Path $release 'update-win-x64.json'
[IO.File]::WriteAllText($path, (($manifest | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($path))
$cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new([Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber, $cert)
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
$signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$cms.ComputeSignature($signer)
$cms.CheckSignature($true)
[IO.File]::WriteAllBytes("$path.p7s", $cms.Encode())
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), "$hash  $asset" + [Environment]::NewLine)
Write-Output $release
