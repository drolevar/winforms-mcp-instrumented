#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipWindowsTests,
    [switch]$SkipPack,
    # Opt-in: launches the demo on the interactive desktop, moves its window, and captures the
    # screen. Needs Python 3 and a visible desktop, so it is not part of the default workflow.
    [switch]$DesktopExercise,
    [string]$VersionOverride
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'WinFormsMcp.Instrumented.slnx'
$sample = Join-Path $repoRoot 'samples/InstrumentedDemo/InstrumentedDemo.csproj'
$protocolTests = Join-Path $repoRoot 'tests/WinFormsMcp.Instrumented.Protocol.Tests/WinFormsMcp.Instrumented.Protocol.Tests.csproj'
$serverTests = Join-Path $repoRoot 'tests/WinFormsMcp.Instrumented.Server.Tests/WinFormsMcp.Instrumented.Server.Tests.csproj'
$windowsTests = Join-Path $repoRoot 'tests/WinFormsMcp.Instrumented.Tests/WinFormsMcp.Instrumented.Tests.csproj'
$validationRoot = Join-Path $repoRoot 'artifacts/validation'
$packageRoot = Join-Path $repoRoot 'artifacts/packages'
$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($VersionOverride)) {
    $versionArguments = @("-p:MinVerVersionOverride=$VersionOverride")
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE ($($Arguments[0]))."
    }
}

function Assert-Stripped([string]$ArtifactsRoot) {
    & (Join-Path $PSScriptRoot 'verify-stripped.ps1') `
        -OutputPath (Join-Path $ArtifactsRoot 'bin/InstrumentedDemo/release') `
        -AssetsPath (Join-Path $ArtifactsRoot 'obj/InstrumentedDemo/project.assets.json')
}

Push-Location $repoRoot
try {
    # These are generated validation artifacts owned by this script.
    if (Test-Path -LiteralPath $validationRoot) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null

    $debugRoot = Join-Path $validationRoot 'debug'
    $debugArguments = @('-c', 'Debug', '--artifacts-path', $debugRoot,
        '-p:EnableWinFormsMcpInstrumentation=true') + $versionArguments
    Invoke-DotNet -Arguments (@('build', $solution) + $debugArguments)
    Invoke-DotNet -Arguments (@('test', $protocolTests, '--no-build', '--no-restore') + $debugArguments)
    Invoke-DotNet -Arguments (@('test', $serverTests, '--no-build', '--no-restore') + $debugArguments)
    if ($IsWindows -and -not $SkipWindowsTests) {
        Invoke-DotNet -Arguments (@('test', $windowsTests, '--no-build', '--no-restore') + $debugArguments)
    }
    else {
        Write-Warning 'Windows UI runtime tests were skipped. This build is not Windows runtime verification.'
    }

    if ($DesktopExercise) {
        if (-not $IsWindows) { throw '-DesktopExercise requires an interactive Windows desktop.' }
        $debugBin = Join-Path $debugRoot 'bin'
        & python (Join-Path $PSScriptRoot 'exercise-demo.py') `
            --captures (Join-Path $validationRoot 'captures') `
            --report (Join-Path $validationRoot 'exercise-report.json') -- `
            dotnet (Join-Path $debugBin 'WinFormsMcp.Instrumented.Server/debug/WinFormsMcp.Instrumented.Server.dll') `
            --app (Join-Path $debugBin 'InstrumentedDemo/debug/InstrumentedDemo.exe')
        if ($LASTEXITCODE -ne 0) { throw "Desktop exercise reported failures (exit code $LASTEXITCODE)." }
    }

    $strippedRoot = Join-Path $validationRoot 'stripped'
    $releaseArguments = @('-c', 'Release', '--artifacts-path', $strippedRoot,
        '-p:EnableWinFormsMcpInstrumentation=false') + $versionArguments
    Invoke-DotNet -Arguments (@('build', $solution) + $releaseArguments)
    Assert-Stripped $strippedRoot
    if ($DesktopExercise) {
        & (Join-Path $PSScriptRoot 'verify-no-endpoint.ps1') `
            -StrippedExecutable (Join-Path $strippedRoot 'bin/InstrumentedDemo/release/InstrumentedDemo.exe') `
            -InstrumentedExecutable (Join-Path $debugRoot 'bin/InstrumentedDemo/debug/InstrumentedDemo.exe')
    }

    $instrumentedRoot = Join-Path $validationRoot 'instrumented'
    $instrumentedArguments = @('-c', 'Release', '--artifacts-path', $instrumentedRoot,
        '-p:EnableWinFormsMcpInstrumentation=true') + $versionArguments
    Invoke-DotNet -Arguments (@('build', $sample) + $instrumentedArguments)
    $bridgeOutput = Join-Path $instrumentedRoot 'bin/InstrumentedDemo/release/WinFormsMcp.Instrumented.dll'
    if (-not (Test-Path -LiteralPath $bridgeOutput -PathType Leaf)) {
        throw 'Instrumented Release sample did not include the bridge.'
    }

    # A clean transition using the previous mode clears its tracked copied files.
    Invoke-DotNet -Arguments (@('clean', $sample, '--verbosity', 'minimal') + $instrumentedArguments)
    $disabledAgainArguments = @('-c', 'Release', '--artifacts-path', $instrumentedRoot,
        '-p:EnableWinFormsMcpInstrumentation=false') + $versionArguments
    Invoke-DotNet -Arguments (@('build', $sample) + $disabledAgainArguments)
    Assert-Stripped $instrumentedRoot

    if (-not $SkipPack) {
        if (Test-Path -LiteralPath $packageRoot) {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        Invoke-DotNet -Arguments (@('pack', $solution, '--no-build', '--no-restore',
            '-o', $packageRoot) + $releaseArguments)
        & (Join-Path $PSScriptRoot 'verify-package-consumer.ps1') -PackagePath $packageRoot
    }
}
finally {
    Pop-Location
}

