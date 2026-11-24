[CmdletBinding()]
param([string]$ArtifactsPath, [switch]$Extended)

$ErrorActionPreference = 'Stop'
if (!$ArtifactsPath) { $ArtifactsPath = Join-Path $PSScriptRoot '../obj/window-checks' }
$project = (Resolve-Path (Join-Path $PSScriptRoot '../Pulse.csproj')).Path
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
dotnet build $project -c Release --artifacts-path $artifacts -p:PulseWindowChecks=true
if ($LASTEXITCODE -ne 0) { throw 'The test build failed.' }

$exe = Join-Path $artifacts 'bin/Pulse/release_win-x64/Pulse.exe'
$runs = @('en-US', 'ru-RU', 'de-CH')
if ($Extended) {
    $extendedDirectory = Join-Path $artifacts ("extended-" + [guid]::NewGuid())
    $runs += 'profile', 'recordings', 'volume'
}
foreach ($run in $runs) {
    $extendedPhase = $run -in 'profile', 'recordings', 'volume'
    $language = if ($extendedPhase) { 'en-US' } else { $run }
    $report = Join-Path $artifacts ("check-{0}-{1}.txt" -f $language, [guid]::NewGuid())
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['PULSE_CHECK_LANGUAGE'] = $language
    $start.EnvironmentVariables['PULSE_CHECK_REPORT'] = $report
    $timeout = 120000
    if ($extendedPhase) {
        $start.EnvironmentVariables['PULSE_EXTENDED_CHECKS'] = $extendedDirectory
        $start.EnvironmentVariables['PULSE_EXTENDED_PHASE'] = $run
        $timeout = 20 * 60 * 1000
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit($timeout)) {
            $process.Kill()
            throw "Window checks timed out during $run."
        }
        if (!(Test-Path -LiteralPath $report)) {
            throw "Window checks exited without a report during $run (code $($process.ExitCode))."
        }
        $result = Get-Content -Raw -LiteralPath $report
        if ($result -ne ("PASS " + $language.Split('-')[0])) { throw $result }
        if ($process.ExitCode -ne 0) { throw "$run exited with code $($process.ExitCode)." }
        Write-Output $result
        if ($extendedPhase) { Write-Output ("Extended reports: " + $start.EnvironmentVariables['PULSE_EXTENDED_CHECKS']) }
    } finally {
        $process.Dispose()
    }
}
