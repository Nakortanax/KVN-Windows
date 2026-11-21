# KVN experimental combined Windows package, never installs or connects VPN.
# Execute only on owner's Windows x64 PC. No profiles, tokens or credentials bundled.
param(
    [string]$AwgZip = "$env:USERPROFILE\ForgejoBuild\AmneziaWG-Home-Windows\dist\KVN-AWG31-Windows-x64-20261120-095929.zip",
    [string]$CsqttZip = "$env:USERPROFILE\ForgejoBuild\VPNSL-1.0.3-Windows-x64.zip",
    [string]$WorkRoot = "$env:USERPROFILE\KVN-Dual-Compile"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Require-Hash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing dependency $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "SHA256 verification failed: $Path" }
}
function Copy-ZipEntry([string]$Archive, [string]$Name, [string]$Destination, [string]$ExpectedHash) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $matches = @($zip.Entries | Where-Object {
            $_.FullName -eq $Name -or $_.FullName.EndsWith("/$Name", [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matches.Count -ne 1) { throw "Missing or ambiguous $Name in $Archive" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($matches[0], $Destination, $true)
    }
    finally { $zip.Dispose() }
    Require-Hash $Destination $ExpectedHash
}

Require-Hash $AwgZip "0fbf13becf3398043eb9c7a56cdb75e1601b17673adec7726c451061011695cd"
# Pinned CSQTT 1.0.3 binary is an integration fixture; network compatibility is not yet proven.
$project = Join-Path $PSScriptRoot "VPNSL.Windows.csproj"
if (-not (Test-Path $project)) { throw "Missing KVN WPF source" }
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$name = "KVN-Dual-AWG-CSQTT-LAB-x64-$timestamp"
$stage = Join-Path $WorkRoot $name
$dist = Join-Path $WorkRoot "dist"
New-Item -ItemType Directory -Force -Path $stage,$dist | Out-Null

& dotnet.exe publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $stage
if ($LASTEXITCODE -ne 0) { throw "KVN WPF publish failed" }

Copy-ZipEntry $AwgZip "amneziawg.exe" (Join-Path $stage "amneziawg.exe") "c7edc12d6319b72d233e96fb1d8c1963e0fd95226504f9a8430bd6d0c00ec3f9"
Copy-ZipEntry $AwgZip "awg.exe" (Join-Path $stage "awg.exe") "cfaf4246a1909c8cff2aa3b8aa4e6d3676bbcde9bea735301d7f7915d9421491"
Copy-ZipEntry $AwgZip "wintun.dll" (Join-Path $stage "wintun.dll") "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"
Copy-ZipEntry $AwgZip "COPYING" (Join-Path $stage "AWG-LICENSE.txt") "53fc1661100082d648e253d44c674a453650a542d3e8f26e6f36adf2ffa58fec"
Copy-ZipEntry $CsqttZip "client.exe" (Join-Path $stage "client.exe") "0e7421fd1183ed64c873f530c7d5d94ee940ae923a861f245c1900cf0252cbe0"
Copy-Item (Join-Path $PSScriptRoot "WDTTSL-LICENSE.txt") (Join-Path $stage "CSQTT-LICENSE.txt")

$note = @"
KVN LAB — Windows x64, AmneziaWG 3.1 + CSQTT.
This is a private integration test, NOT a production VPN release.
AmneziaWG requires a manually selected full-tunnel .conf and bundled patched AWG binary.
CSQTT uses a pinned Windows Rust client from WDTTSL 1.0.3.
CSQTT uses temporary WFP rules blocking outbound IPv6. No permanent adapter change.
WFP filter installation and removal tested; real IPv6 and DNS leak tests pending.
KVN light UI: Connection, Configurations, Diagnostics.
CSQTT .kvn credentials and saved passwords are encrypted with Windows DPAPI.
The program stores KVN settings separately from existing VPNSL settings.
Do not install over or run alongside an active VPN without disconnecting that VPN first.
No VPN private keys, server tokens or profiles are bundled.
A framework-dependent package: Microsoft Windows Desktop Runtime 8.x x64 required.
The source and licenses of original components are included as notices.
"@
[IO.File]::WriteAllText((Join-Path $stage "README-LAB.txt"), $note, [Text.UTF8Encoding]::new($false))

foreach ($file in Get-ChildItem -LiteralPath $stage -File) {
    if ($file.Extension -in @(".conf",".kvn",".jsonl",".env")) {
        throw "Forbidden confidential extension in package: $($file.Name)"
    }
}
$main = Join-Path $stage "KVN.Windows.exe"
if (-not (Test-Path -LiteralPath $main)) { throw "KVN.Windows.exe missing" }
$smoke = Start-Process -FilePath $main -ArgumentList "--wpf-smoke-test" -WorkingDirectory $stage -Wait -PassThru
if ($smoke.ExitCode -ne 0) { throw "WPF smoke test failed" }
Start-Sleep -Seconds 2

$zipPath = Join-Path $dist "$name.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$hash *$name.zip`r`n")
Write-Output "KVN_LAB_PACKAGE_OK"
Write-Output "ARTIFACT=$zipPath"
Write-Output "SHA256=$hash"
