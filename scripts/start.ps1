#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launchedProcess = $null
$standardOutputReader = $null
$standardErrorReader = $null
$originalControlCMode = $null
$launchExitCode = 0

function Show-RuntimeOutput {
    param(
        [System.IO.StreamReader]$Reader,
        [switch]$IsError
    )

    if ($null -eq $Reader) {
        return
    }

    # Bound each pass so a noisy application cannot starve the keyboard controls.
    for ($lineNumber = 0; $lineNumber -lt 200; $lineNumber++) {
        $line = $Reader.ReadLine()
        if ($null -eq $line) {
            break
        }

        if ($IsError) {
            Write-Host $line -ForegroundColor Red
        }
        else {
            Write-Host $line
        }
    }
}

function Stop-LaunchedProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    Write-Host "Stopping LuvLetter (PID $($Process.Id))..."
    # A tray-only application may have no main window. Allow a normal close when
    # available, then terminate only the process owned by this launch session.
    try {
        if ($Process.CloseMainWindow() -and $Process.WaitForExit(5000)) {
            return
        }
        if (-not $Process.HasExited) {
            $Process.Kill()
            if (-not $Process.WaitForExit(5000)) {
                throw "LuvLetter (PID $($Process.Id)) did not stop within five seconds."
            }
        }
    }
    catch {
        # The tray may finish shutting down between the liveness check and close.
        if (-not $Process.HasExited) {
            throw
        }
    }
}

try {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $projectPath = Join-Path $repositoryRoot 'src\LuvLetter\LuvLetter.csproj'
    $outputDirectory = Join-Path $repositoryRoot "src\LuvLetter\bin\$Configuration\net10.0-windows"

    # Running binaries may be locked during the build. Leave the existing app intact.
    if (Get-Process -Name LuvLetter -ErrorAction SilentlyContinue) {
        throw 'LuvLetter is already running. Exit it from the system tray before starting again.'
    }

    $vswhereCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    )
    $vswhere = $vswhereCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $vswhere) {
        throw 'Install Visual Studio 2026 or Build Tools with Desktop development with C++, MSVC v145, a Windows SDK, and the .NET 10 SDK.'
    }

    # Include both the IDE and standalone Build Tools installations.
    $installationPaths = @(& $vswhere -latest -products '*' -version '[18.0,)' `
        -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.NetCore.Component.SDK `
        -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installationPaths.Count -eq 0) {
        throw 'No suitable Visual Studio 2026 installation was found. Install the C++ desktop workload and the .NET SDK component.'
    }

    $msbuild = Join-Path $installationPaths[0] 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        throw "Visual Studio MSBuild was not found at: $msbuild"
    }

    Write-Host "Building LuvLetter ($Configuration)..."
    Push-Location -LiteralPath $repositoryRoot
    try {
        # Full MSBuild follows the application's native project references as well.
        & $msbuild $projectPath /restore /m /nologo "/p:Configuration=$Configuration"
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed (exit code $LASTEXITCODE). See the MSBuild errors above. Check the .NET 10 SDK, MSVC v145 tools, and Windows SDK installation."
        }
    }
    finally {
        Pop-Location
    }

    foreach ($fileName in @('LuvLetter.exe', 'LuvLetter.Native.dll', 'LuvLetter.Indexer.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory $fileName) -PathType Leaf)) {
            throw "Required build output is missing: $fileName"
        }
    }

    $logDirectory = Join-Path $env:LOCALAPPDATA 'LuvLetter\Logs'
    $null = New-Item -ItemType Directory -Path $logDirectory -Force
    $launchStamp = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $standardOutputPath = Join-Path $logDirectory "launch-$launchStamp.stdout.log"
    $standardErrorPath = Join-Path $logDirectory "launch-$launchStamp.stderr.log"

    $executable = Join-Path $outputDirectory 'LuvLetter.exe'
    $launchedProcess = Start-Process -FilePath $executable -WorkingDirectory $outputDirectory `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $standardOutputPath -RedirectStandardError $standardErrorPath
    # Retain the OS handle; never rediscover a process by its name or a reused PID.
    $null = $launchedProcess.Handle
    $standardOutputReader = [System.IO.StreamReader]::new(
        [System.IO.File]::Open($standardOutputPath, 'Open', 'Read', 'ReadWrite'))
    $standardErrorReader = [System.IO.StreamReader]::new(
        [System.IO.File]::Open($standardErrorPath, 'Open', 'Read', 'ReadWrite'))

    Write-Host "LuvLetter launched (PID $($launchedProcess.Id), $Configuration)."
    Write-Host 'Use the system tray or press Ctrl twice to open the input box.'
    Write-Host "Standard output: $standardOutputPath"
    Write-Host "Standard error:  $standardErrorPath"
    Write-Host 'Runtime output is shown below as the application emits it.'

    $hasConsoleInput = -not [Console]::IsInputRedirected
    if ($hasConsoleInput) {
        $originalControlCMode = [Console]::TreatControlCAsInput
        [Console]::TreatControlCAsInput = $true
        Write-Host 'Press Q, Enter, or Ctrl+C here to stop this process, or exit from the system tray.'
    }
    else {
        Write-Host 'Console input is redirected. Exit LuvLetter from the system tray to end this session.'
    }
    Write-Host ''

    $stopRequested = $false
    while (-not $launchedProcess.HasExited) {
        Show-RuntimeOutput -Reader $standardOutputReader
        Show-RuntimeOutput -Reader $standardErrorReader -IsError

        if ($hasConsoleInput -and [Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq [ConsoleKey]::Q -or $key.Key -eq [ConsoleKey]::Enter -or
                $key.KeyChar -eq [char]3) {
                $stopRequested = $true
                Stop-LaunchedProcess -Process $launchedProcess
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }

    # Flush the redirected stream handlers before reading the last output lines.
    $launchedProcess.WaitForExit()
    while (-not $standardOutputReader.EndOfStream -or -not $standardErrorReader.EndOfStream) {
        Show-RuntimeOutput -Reader $standardOutputReader
        Show-RuntimeOutput -Reader $standardErrorReader -IsError
    }
    Write-Host "LuvLetter exited (code $($launchedProcess.ExitCode)). Logs remain in $logDirectory."
    if (-not $stopRequested) {
        $launchExitCode = $launchedProcess.ExitCode
    }
}
catch {
    [Console]::Error.WriteLine("LuvLetter debug session failed: $($_.Exception.Message)")
    $launchExitCode = 1
}
finally {
    # Also attempt cleanup when PowerShell cancels execution. Closing the terminal
    # forcibly can bypass finally; use the keyboard controls or tray exit instead.
    try {
        Stop-LaunchedProcess -Process $launchedProcess
    }
    catch {
        [Console]::Error.WriteLine("Cannot stop the launched process: $($_.Exception.Message)")
        $launchExitCode = 1
    }
    if ($null -ne $originalControlCMode) {
        [Console]::TreatControlCAsInput = $originalControlCMode
    }
    if ($null -ne $standardOutputReader) { $standardOutputReader.Dispose() }
    if ($null -ne $standardErrorReader) { $standardErrorReader.Dispose() }
    if ($null -ne $launchedProcess) { $launchedProcess.Dispose() }
}

exit $launchExitCode
