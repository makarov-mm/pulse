# Builds the portable single-file Pulse.exe and records the result of the Windows regression checks beside it.
[CmdletBinding()]
param(
    # Folder that receives Pulse.exe and test-results.txt. Defaults to ./publish.
    [string]$Output,
    # Publish without running tests/Run.ps1.
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
if (!$Output) { $Output = Join-Path $PSScriptRoot 'publish' }
$Output = [IO.Path]::GetFullPath($Output)

dotnet publish (Join-Path $PSScriptRoot 'Pulse.csproj') -c Release -p:PublishProfile=Portable -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
$exe = Join-Path $Output 'Pulse.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "The publish step did not produce $exe." }

if (!$SkipChecks) {
    $report = Join-Path $Output 'test-results.txt'
    $lines = @("Pulse window checks, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", '')
    try {
        $lines += & (Join-Path $PSScriptRoot 'tests/Run.ps1') | ForEach-Object { "$_" }
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
