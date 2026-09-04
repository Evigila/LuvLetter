#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

    $executable = Join-Path $outputDirectory 'LuvLetter.exe'
    Start-Process -FilePath $executable -WorkingDirectory $outputDirectory
    Write-Host 'LuvLetter launched. Use the system tray or press Ctrl twice to open the input box.'
    exit 0
}
catch {
    [Console]::Error.WriteLine("Cannot launch LuvLetter: $($_.Exception.Message)")
    exit 1
}
