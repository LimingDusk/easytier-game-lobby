# Run from a writable copy of the launcher, before starting GameLobby.
param([string]$Destination = (Join-Path $PSScriptRoot 'engine'))
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$url = 'https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip'
$expected = '27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84'
$temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
New-Item -ItemType Directory $temp | Out-Null
try {
    $zip = Join-Path $temp 'engine.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) { throw 'EasyTier checksum mismatch.' }
    Expand-Archive $zip (Join-Path $temp 'unpacked')
    $core = Get-ChildItem (Join-Path $temp 'unpacked') -Recurse -Filter easytier-core.exe | Select-Object -First 1
    if (!$core -or !(Test-Path (Join-Path $core.DirectoryName 'easytier-cli.exe'))) { throw 'Incomplete EasyTier archive.' }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Copy-Item (Join-Path $core.DirectoryName '*') $Destination -Recurse -Force
    Write-Host 'EasyTier is ready. Start GameLobby.exe.'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
