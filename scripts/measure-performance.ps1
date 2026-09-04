[CmdletBinding()]
param(
    [ValidateRange(1, 86400)]
    [int] $DurationSeconds = 600,

    [ValidateRange(100, 60000)]
    [int] $SampleIntervalMilliseconds = 1000,

    [string] $OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $timestamp = [DateTimeOffset]::Now.ToString("yyyyMMdd-HHmmss")
    $OutputPath = Join-Path $repositoryRoot "artifacts/performance/process-$timestamp.csv"
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$writer = [IO.StreamWriter]::new($OutputPath, $false, $utf8WithoutBom)
$culture = [Globalization.CultureInfo]::InvariantCulture
$processNames = @("LuvLetter", "LuvLetter.Indexer")
$clock = [Diagnostics.Stopwatch]::StartNew()

function Write-Sample {
    param(
        [DateTimeOffset] $Timestamp,
        [double] $ElapsedSeconds,
        [string] $ProcessName,
        [int] $ProcessId,
        [long] $WorkingSetBytes,
        [long] $PrivateBytes,
        [double] $CpuMilliseconds,
        [int] $ThreadCount,
        [int] $HandleCount
    )

    $values = @(
        $Timestamp.ToString("O", $culture),
        $ElapsedSeconds.ToString("F3", $culture),
        $ProcessName,
        $ProcessId.ToString($culture),
        $WorkingSetBytes.ToString($culture),
        $PrivateBytes.ToString($culture),
        $CpuMilliseconds.ToString("F3", $culture),
        $ThreadCount.ToString($culture),
        $HandleCount.ToString($culture)
    )
    $writer.WriteLine([string]::Join(",", $values))
}

try {
    $writer.WriteLine(
        "timestamp,elapsed_seconds,process_name,process_id,working_set_bytes," +
        "private_bytes,total_cpu_milliseconds,thread_count,handle_count")

    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $sampledAt = [DateTimeOffset]::Now
        $elapsed = $clock.Elapsed.TotalSeconds
        $workingSetTotal = 0L
        $privateTotal = 0L
        $cpuTotal = 0.0
        $threadTotal = 0
        $handleTotal = 0
        $processCount = 0

        foreach ($processName in $processNames) {
            foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
                try {
                    $process.Refresh()
                    $workingSet = $process.WorkingSet64
                    $privateBytes = $process.PrivateMemorySize64
                    $cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                    $threads = $process.Threads.Count
                    $handles = $process.HandleCount
                    Write-Sample $sampledAt $elapsed $process.ProcessName $process.Id `
                        $workingSet $privateBytes $cpuMilliseconds $threads $handles

                    $workingSetTotal += $workingSet
                    $privateTotal += $privateBytes
                    $cpuTotal += $cpuMilliseconds
                    $threadTotal += $threads
                    $handleTotal += $handles
                    $processCount++
                }
                catch [InvalidOperationException] {
                    # The process exited between discovery and sampling.
                }
                finally {
                    $process.Dispose()
                }
            }
        }

        Write-Sample $sampledAt $elapsed "TOTAL" $processCount $workingSetTotal `
            $privateTotal $cpuTotal $threadTotal $handleTotal
        $writer.Flush()
        $remainingMilliseconds = [Math]::Max(
            0,
            [Math]::Ceiling(($DurationSeconds - $clock.Elapsed.TotalSeconds) * 1000))
        if ($remainingMilliseconds -gt 0) {
            Start-Sleep -Milliseconds ([Math]::Min(
                $SampleIntervalMilliseconds,
                [int] $remainingMilliseconds))
        }
    }
}
finally {
    $clock.Stop()
    $writer.Dispose()
}

Write-Host "Performance samples written to $OutputPath"
