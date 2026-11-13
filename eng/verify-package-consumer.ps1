#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$fixtureRoot = Join-Path $repoRoot 'artifacts/validation/package-consumer'
$bridgePackages = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nupkg' -File |
    Where-Object { $_.Name -match '^WinFormsMcp\.Instrumented\.[0-9]' })
if ($bridgePackages.Count -ne 1) {
    throw "Expected one bridge package in $packageRoot; found $($bridgePackages.Count)."
}

Add-Type -AssemblyName System.IO.Compression.ZipFile
$archive = [System.IO.Compression.ZipFile]::OpenRead($bridgePackages[0].FullName)
try {
    $nuspec = @($archive.Entries | Where-Object { $_.Name -like '*.nuspec' })
    if ($nuspec.Count -ne 1) { throw 'Bridge package has no unique nuspec.' }
    $reader = [System.IO.StreamReader]::new($nuspec[0].Open())
    try { [xml]$packageMetadata = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }

$metadata = $packageMetadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
if ($metadata.id -ne 'WinFormsMcp.Instrumented') { throw 'Unexpected bridge package ID.' }
$bridgeVersion = [string]$metadata.version
$license = $metadata.SelectSingleNode('*[local-name()="license"]')
if ($null -eq $license -or $license.GetAttribute('type') -ne 'expression' -or $license.InnerText -ne 'MIT') {
    throw 'Bridge package is missing its MIT license expression.'
}

[xml]$central = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw
$referenceVersion = [string]($central.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq 'Microsoft.NETFramework.ReferenceAssemblies' }).Version
if ([string]::IsNullOrWhiteSpace($referenceVersion)) {
    throw 'Central reference-assemblies version was not found.'
}

if (Test-Path -LiteralPath $fixtureRoot) {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null

# Empty local build files deliberately isolate the real-package consumer from repo project references.
'<Project />' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Directory.Build.props') -Encoding utf8
'<Project />' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Directory.Build.targets') -Encoding utf8

$packageVersionXml = [System.Security.SecurityElement]::Escape($bridgeVersion)
$referenceVersionXml = [System.Security.SecurityElement]::Escape($referenceVersion)
$packageRootXml = [System.Security.SecurityElement]::Escape($packageRoot)
@"
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="WinFormsMcp.Instrumented" Version="$packageVersionXml" />
    <PackageVersion Include="Microsoft.NETFramework.ReferenceAssemblies" Version="$referenceVersionXml" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Directory.Packages.props') -Encoding utf8

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="built-packages" value="$packageRootXml" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'NuGet.Config') -Encoding utf8

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <IsPackable>false</IsPackable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
    <EnableWinFormsMcpInstrumentation Condition="'$(EnableWinFormsMcpInstrumentation)' == ''">false</EnableWinFormsMcpInstrumentation>
    <DefineConstants Condition="'$(EnableWinFormsMcpInstrumentation)' == 'true'">$(DefineConstants);WINFORMS_MCP_INSTRUMENTED</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup Condition="'$(EnableWinFormsMcpInstrumentation)' == 'true'">
    <PackageReference Include="WinFormsMcp.Instrumented" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'PackageConsumer.csproj') -Encoding utf8

@'
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#if WINFORMS_MCP_INSTRUMENTED
        using (WinFormsMcp.Instrumented.McpBridge.Start())
#endif
        {
            Application.Run(new Form());
        }
    }
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Program.cs') -Encoding utf8

$project = Join-Path $fixtureRoot 'PackageConsumer.csproj'
$enabledArtifacts = Join-Path $fixtureRoot 'enabled'
$disabledArtifacts = Join-Path $fixtureRoot 'disabled'

# A private cache makes each check consume these newly built bytes, including when
# an archive version is reused locally. It cannot silently use a stale global cache.
$packageCache = Join-Path $fixtureRoot 'packages'
& dotnet build $project -c Release --artifacts-path $enabledArtifacts `
    -p:EnableWinFormsMcpInstrumentation=true "-p:RestorePackagesPath=$packageCache"
if ($LASTEXITCODE -ne 0) { throw 'Enabled package-consumer build failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $enabledArtifacts 'bin/PackageConsumer/release/WinFormsMcp.Instrumented.dll'))) {
    throw 'Enabled package consumer did not copy the bridge.'
}

& dotnet build $project -c Release --artifacts-path $disabledArtifacts `
    -p:EnableWinFormsMcpInstrumentation=false "-p:RestorePackagesPath=$packageCache"
if ($LASTEXITCODE -ne 0) { throw 'Disabled package-consumer build failed.' }
& (Join-Path $PSScriptRoot 'verify-stripped.ps1') `
    -OutputPath (Join-Path $disabledArtifacts 'bin/PackageConsumer/release') `
    -AssetsPath (Join-Path $disabledArtifacts 'obj/PackageConsumer/project.assets.json') `
    -ApplicationAssemblyName 'PackageConsumer'

Write-Host "Verified enabled and stripped consumers of WinFormsMcp.Instrumented $bridgeVersion"

