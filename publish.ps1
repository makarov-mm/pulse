# Builds the portable single-file MakarovPulse.exe and records the result of the Windows regression checks beside it.
[CmdletBinding()]
param(
    # Folder that receives MakarovPulse.exe and test-results.txt. Defaults to ./publish.
    [string]$Output,
    # Publish without running tests/Run.ps1.
    [switch]$SkipChecks,
    # SHA-1 thumbprint of a trusted code-signing certificate in CurrentUser\My.
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
    # HTTPS RFC 3161 timestamp service supplied by the certificate issuer.
    [uri]$TimestampServer
)

$ErrorActionPreference = 'Stop'
if (!$Output) { $Output = Join-Path $PSScriptRoot 'publish' }
$Output = [IO.Path]::GetFullPath($Output)

if ($TimestampServer -and (!$CertificateThumbprint -or $TimestampServer.Scheme -ne 'https')) {
    throw 'Timestamping requires a signing certificate and an HTTPS timestamp server.'
}

dotnet publish (Join-Path $PSScriptRoot 'Pulse.csproj') -c Release -p:PublishProfile=Portable -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
$exe = Join-Path $Output 'MakarovPulse.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "The publish step did not produce $exe." }

if ($CertificateThumbprint) {
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if (!$signTool) {
        $sdk = Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' 'KitsRoot10'
        $signTool = Get-ChildItem (Join-Path $sdk 'bin\*\x64\signtool.exe') -File |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (!$signTool) { throw 'SignTool was not found in the Windows SDK.' }
    $signArgs = @('sign', '/s', 'My', '/sha1', $CertificateThumbprint, '/fd', 'SHA256')
    if ($TimestampServer) { $signArgs += '/tr', $TimestampServer.AbsoluteUri, '/td', 'SHA256' }
    & $signTool @signArgs $exe
    if ($LASTEXITCODE) { throw 'Authenticode signing failed.' }
    & $signTool verify /pa $exe
    if ($LASTEXITCODE) { throw 'The signed executable did not pass Windows trust verification.' }
}

$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$exe.sha256" -Value "$hash  MakarovPulse.exe" -Encoding Ascii

if (!$SkipChecks) {
    $report = Join-Path $Output 'test-results.txt'
    $lines = @("Makarov Pulse window checks, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", '')
    try {
        $lines += & (Join-Path $PSScriptRoot 'tests/Run.ps1') -Portable 2>&1 | ForEach-Object { "$_" }
        $lines += '', 'RESULT: PASS'
    } catch {
        $lines += '', "RESULT: FAIL", "$_"
        Set-Content -LiteralPath $report -Value $lines -Encoding utf8
        throw
    }
    Set-Content -LiteralPath $report -Value $lines -Encoding utf8
}

$size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Output "Published $exe ($size MB)"
