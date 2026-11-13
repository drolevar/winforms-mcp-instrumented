#requires -Version 7.0
<#
.SYNOPSIS
Starts a stripped and an instrumented build with bridge activation variables set and
checks which of them opens the named-pipe endpoint.

.DESCRIPTION
Complements verify-stripped.ps1, which inspects files and metadata. This script observes
runtime behavior: the stripped executable must not create the pipe even though
WINFORMS_MCP_PIPE and WINFORMS_MCP_TOKEN are set, because it contains no bridge to
activate. The instrumented executable is a positive control; without it, "no pipe" could
simply mean that the probe or the application never started.

Needs an interactive Windows desktop (the sample opens a window). It starts and stops only
the two processes it creates, identified by their process IDs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StrippedExecutable,
    [Parameter(Mandatory)][string]$InstrumentedExecutable,
    [int]$SettleSeconds = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Named-pipe endpoint verification requires Windows.' }

function Test-PipeExists([string]$Name) {
    # [System.IO.Directory] enumerates \\.\pipe\ without connecting to (and disturbing) any endpoint.
    return @([System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_.EndsWith("\$Name", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}

function Start-WithActivation([string]$Executable, [string]$PipeName) {
    $info = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Executable).Path)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Split-Path -Parent $info.FileName
    $info.Environment['WINFORMS_MCP_PIPE'] = $PipeName
    # A throwaway token: it is never written anywhere and only this script's children see it.
    $info.Environment['WINFORMS_MCP_TOKEN'] = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    return [System.Diagnostics.Process]::Start($info)
}

$strippedPipe = 'wfmi-verify-stripped-' + [guid]::NewGuid().ToString('N')
$instrumentedPipe = 'wfmi-verify-instrumented-' + [guid]::NewGuid().ToString('N')
$processes = @()
try {
    $stripped = Start-WithActivation $StrippedExecutable $strippedPipe
    $processes += $stripped
    $instrumented = Start-WithActivation $InstrumentedExecutable $instrumentedPipe
    $processes += $instrumented
    Start-Sleep -Seconds $SettleSeconds

    foreach ($process in @($stripped, $instrumented)) {
        $process.Refresh()
        if ($process.HasExited) { throw "Process $($process.Id) exited early with code $($process.ExitCode); the check is inconclusive." }
    }
    if (-not (Test-PipeExists $instrumentedPipe)) {
        throw 'Positive control failed: the instrumented build did not open its pipe, so the stripped result proves nothing.'
    }
    if (Test-PipeExists $strippedPipe) {
        throw 'The stripped build opened a bridge endpoint despite the activation variables.'
    }
    Write-Host 'Verified: the instrumented build opened its endpoint; the stripped build opened none.'
}
finally {
    foreach ($process in $processes) {
        try {
            if (-not $process.HasExited) {
                if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(3000)) { $process.Kill() }
            }
        }
        catch { Write-Warning "Could not stop process $($process.Id): $($_.Exception.Message)" }
        $process.Dispose()
    }
}
