#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$AssetsPath,
    [string]$ApplicationAssemblyName = 'InstrumentedDemo',
    [string[]]$ForbiddenAssemblyPatterns = @(
        'WinFormsMcp.Instrumented*',
        'ModelContextProtocol*',
        'Newtonsoft.Json'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $OutputPath -PathType Container)) {
    throw "Build output does not exist: $OutputPath"
}
if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) {
    throw "Restore assets file does not exist: $AssetsPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $OutputPath "$ApplicationAssemblyName.exe") -PathType Leaf)) {
    throw "The application executable is missing from $OutputPath"
}

function Test-ForbiddenAssembly([string]$Name) {
    foreach ($pattern in $ForbiddenAssemblyPatterns) {
        if ($Name -like $pattern) { return $true }
    }
    return $false
}

$violations = [System.Collections.Generic.List[string]]::new()
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $assets.ContainsKey('libraries')) {
    throw "Assets file has no libraries section: $AssetsPath"
}
foreach ($library in $assets.libraries.Keys) {
    $name = ($library -split '/', 2)[0]
    if (Test-ForbiddenAssembly $name) {
        $violations.Add("Restore graph contains $library")
    }
}

# Read metadata only. Never load or execute an application assembly to inspect it.
Add-Type -AssemblyName System.Reflection.Metadata
$binaries = Get-ChildItem -LiteralPath $OutputPath -Recurse -File |
    Where-Object { $_.Extension -in '.dll', '.exe' }

foreach ($binary in $binaries) {
    if (Test-ForbiddenAssembly $binary.BaseName) {
        $violations.Add("Deployment contains $($binary.Name)")
    }

    $stream = [System.IO.File]::OpenRead($binary.FullName)
    $pe = $null
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $pe.HasMetadata) { continue }
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($handle in $metadata.AssemblyReferences) {
                $reference = $metadata.GetAssemblyReference($handle)
                $name = $metadata.GetString($reference.Name)
                if (Test-ForbiddenAssembly $name) {
                    $violations.Add("$($binary.Name) references $name")
                }
            }
            foreach ($handle in $metadata.TypeDefinitions) {
                $definition = $metadata.GetTypeDefinition($handle)
                $typeName = $metadata.GetString($definition.Name)
                $typeNamespace = $metadata.GetString($definition.Namespace)
                if ($typeNamespace -like 'WinFormsMcp.Instrumented*' -or $typeName -eq 'McpInstrumentation') {
                    $violations.Add("$($binary.Name) contains instrumentation type $typeNamespace.$typeName")
                }
            }
        }
        finally {
            if ($null -ne $pe) { $pe.Dispose() }
        }
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($config in (Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter '*.config' -File)) {
    [xml]$content = Get-Content -LiteralPath $config.FullName -Raw
    foreach ($identity in $content.SelectNodes('//*[local-name()="assemblyIdentity"]')) {
        $name = $identity.GetAttribute('name')
        if (Test-ForbiddenAssembly $name) {
            $violations.Add("$($config.Name) contains instrumentation-only binding for $name")
        }
    }
}

if ($violations.Count -gt 0) {
    throw ("Stripped-build verification failed:`n - " + ($violations -join "`n - "))
}

Write-Host "Verified stripped application output and dependency graph: $OutputPath"

