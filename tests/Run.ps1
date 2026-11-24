[CmdletBinding()]
param([string]$ArtifactsPath)

$ErrorActionPreference = 'Stop'
if (!$ArtifactsPath) { $ArtifactsPath = Join-Path $PSScriptRoot '../obj/window-checks' }
$project = (Resolve-Path (Join-Path $PSScriptRoot '../Pulse.csproj')).Path
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
dotnet build $project -c Release --artifacts-path $artifacts -p:PulseWindowChecks=true
if ($LASTEXITCODE -ne 0) { throw 'The test build failed.' }

$exe = Join-Path $artifacts 'bin/Pulse/release_win-x64/Pulse.exe'
foreach ($language in 'en-US', 'ru-RU', 'de-CH') {
    $report = Join-Path $artifacts ("check-{0}-{1}.txt" -f $language, [guid]::NewGuid())
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['PULSE_CHECK_LANGUAGE'] = $language
    $start.EnvironmentVariables['PULSE_CHECK_REPORT'] = $report
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(60000)) {
            $process.Kill()
            throw "Window checks timed out for $language."
        }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
            throw "Window checks exited without success for $language (code $($process.ExitCode))."
        }
        $result = Get-Content -Raw -LiteralPath $report
        if ($result -ne ("PASS " + $language.Split('-')[0])) { throw $result }
        Write-Output $result
    } finally {
        $process.Dispose()
    }
}
