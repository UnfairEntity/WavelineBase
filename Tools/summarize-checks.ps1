# Turns a batch-mode test run into Logs\TestResults\summary.txt (called by run-checks.bat).
param(
    [string]$ResultsXml,
    [string]$EditorLog,
    [string]$SummaryPath,
    [int]$UnityExitCode
)

$lines = @("WavelineBase checks - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - EditMode (batch mode) - Unity exit code $UnityExitCode")
$failed = $false

# Compile errors stop the tests from running at all, so report them first.
if (Test-Path $EditorLog) {
    $compileErrors = Select-String -Path $EditorLog -Pattern 'error CS\d{4}' |
        ForEach-Object { $_.Line.Trim() } | Select-Object -Unique
    if ($compileErrors) {
        $failed = $true
        $lines += "COMPILE ERRORS ($(@($compileErrors).Count)):"
        $lines += $compileErrors | ForEach-Object { "    $_" }
    }
}

if (Test-Path $ResultsXml) {
    [xml]$xml = Get-Content -Raw $ResultsXml
    $run = $xml.'test-run'
    $status = if ([int]$run.failed -eq 0) { 'ALL PASSED' } else { 'FAILURES' }
    $lines += "[Checks] ${status}: $($run.passed) passed, $($run.failed) failed, $($run.skipped) skipped (of $($run.total))"
    if ([int]$run.failed -gt 0) { $failed = $true }
    foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']")) {
        $lines += "FAILED: $($case.fullname)"
        $message = $case.failure.message.InnerText
        if (-not $message) { $message = [string]$case.failure.message }
        $lines += ($message -split "`n" | Select-Object -First 6 | ForEach-Object { "    " + $_.TrimEnd() })
    }
} else {
    $failed = $true
    $lines += "No test results were written - see $EditorLog (usually compile errors, or the project was open in the Editor)."
}

New-Item -ItemType Directory -Force -Path (Split-Path $SummaryPath) | Out-Null
$lines | Set-Content -Path $SummaryPath -Encoding UTF8
$lines | ForEach-Object { Write-Output $_ }
if ($failed) { exit 1 } else { exit 0 }
