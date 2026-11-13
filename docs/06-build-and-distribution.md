# Build and distribution

This document defines the build and packaging contract. It was written before the
implementation so that convenience cannot silently weaken the two central promises:
the application stays on .NET Framework 4.8, and a shipping build can exclude every
instrumentation dependency and hook.

## Why this toolchain

Use SDK-style projects throughout, a `WinFormsMcp.Instrumented.slnx` solution, and
central NuGet version declarations in `Directory.Packages.props`. These make the
dependency boundary visible in ordinary project files and give application,
protocol, server, sample, and test projects the same build conventions.

The SDK used to build a project is independent of the runtime it targets. Build
with the .NET 10 SDK while keeping the bridge and sample on `net48`, the portable
protocol on `netstandard2.0`, and the separate server on `net10.0`. SDK support for
`.slnx` began in 9.0.200 and .NET 10 makes that solution format the default; there is
no reason to introduce an older `.sln` alongside it in a new repository.

Sources: [SLNX in the .NET SDK](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default),
[central package management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management).

## Project and package boundaries

| Project | Target | Distributed as | Consumer |
| --- | --- | --- | --- |
| `WinFormsMcp.Instrumented` | `net48` | NuGet library | Instrumented application |
| `WinFormsMcp.Instrumented.Protocol` | `netstandard2.0` | NuGet library | Bridge and server |
| `WinFormsMcp.Instrumented.Server` | `net10.0` | NuGet .NET tool | MCP client launches it |
| Sample application | `net48` | Source/build fixture | Developer and validation |
| Tests | Appropriate portable/Windows target | Not packed | CI and developer |

The application must never reference the server project or `ModelContextProtocol`.
Installing the server as a .NET tool is separate from adding the bridge library to
an application. This separation is what keeps MCP hosting dependencies out of the
legacy process and lets an MCP session survive an application restart.

The bridge's package dependency on the shared protocol is intentional. A direct
consumer does not need to install the protocol separately. Optional future control
adapters must remain separate packages rather than expanding the default dependency
graph for every application.

## Dependency baseline

Use exact versions in `Directory.Packages.props`, not floating versions. The following
primary package pages were checked on 2026-11-13. The committed central file is
authoritative if this baseline is subsequently updated.

| Package | Baseline | Reason |
| --- | --- | --- |
| [MinVer](https://www.nuget.org/packages/MinVer/8.0.0) | `8.0.0` | Build-time Git-tag versioning |
| [ModelContextProtocol](https://packages.nuget.org/packages/ModelContextProtocol/2.4.0) | `2.4.0` | Official stdio MCP hosting, server only |
| [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.15) | `10.0.15` | Server host, kept on the .NET 10 line |
| [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json/13.0.4) | `13.0.4` | Shared JSON serialization used by the pipe protocol |
| [Microsoft.NETFramework.ReferenceAssemblies](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies/1.0.3) | `1.0.3` | Build-time .NET Framework references |
| [MSTest.TestFramework](https://www.nuget.org/packages/MSTest.TestFramework/4.5.3) / [MSTest.TestAdapter](https://www.nuget.org/packages/MSTest.TestAdapter/4.5.3) | `4.5.3` | Matching test framework and discovery adapter |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.12.0) | `18.12.0` | Test discovery and execution infrastructure |

Reference assemblies provide compile-time metadata; they do not make WinForms run
on Linux. Windows UI tests, real screenshots, DPI behavior, native handles, and
input behavior must be validated on Windows. Build-only packages such as MinVer
and reference assemblies use `PrivateAssets="all"` so they do not become runtime
requirements of a consumer. This metadata does not strip a runtime dependency.

Test framework versions belong in the same central file. Keep the framework and
adapter on a matching version. Avoid adding a second test framework without a
specific need; the Windows fixture can own a dedicated STA message loop regardless
of the test runner's default apartment.

## Versioning

MinVer derives one product version for all three published packages. Use `v`
prefixed tags, a minimum major/minor of `0.1`, and `alpha.0` as the default prerelease
identifiers. A reviewed release tag such as `v0.1.0-alpha.1` identifies a specific
source revision. Do not manually duplicate package versions in project files.

Fetch full Git history and tags in CI. A source archive does not contain that
history. When the repository root has no `.git` entry, the draft's shared build
properties supply `0.1.0-alpha.0` as an archive-only override. An explicit override
can also be supplied, for example:

```powershell
dotnet build WinFormsMcp.Instrumented.slnx -c Debug -p:MinVerVersionOverride=0.1.0-alpha.0
```

This override makes an archive build reproducible without pretending that a release
tag exists. It is not a release procedure. The archive's absence of Git history
must never cause the build to invent a stable version. Create a normal repository
at this source root to use tag-derived versions; when incorporating the project
into a larger repository, remove or adapt the archive-root detection deliberately.

See [ADR 0003](adr/0003-versioning-and-packaging.md) and the
[MinVer documentation](https://github.com/adamralph/minver) for the rationale and
the upstream property definitions.

## Instrumentation is a build dimension

`EnableWinFormsMcpInstrumentation` controls two separate things:

1. Whether the consuming application references the bridge and optional adapters.
2. Whether instrumentation startup and configuration source is compiled.

The default is `true` for Debug and `false` for other configurations. An explicit
value takes precedence. An optimized Release build with the flag enabled is useful
for reproducing Release-only defects. Shipping automation explicitly sets it to
`false` instead of depending on a configuration name.

The bridge library itself must still be buildable and packable in Release. The
switch removes instrumentation from the consuming application; it does not turn
the bridge package being produced into an empty assembly. Keep those concepts
separate in shared build files.

The derived C# symbol is `WINFORMS_MCP_INSTRUMENTED`. Only the build integration
defines that symbol; it is not a second user-facing switch. A reference-source
sample uses a conditional `ProjectReference`, while a real package consumer uses a
conditional `PackageReference`. Both must omit every use of bridge types when
disabled, including `using` directives and optional adapter declarations.

The condition belongs in the consuming project or its own imported build files.
Package-provided `.props`/`.targets` cannot be relied upon to remove their own
`PackageReference` from a graph that was already restored. NuGet's guidance
explicitly excludes restore-affecting package items from package build logic.

Sources: [conditional package references](https://learn.microsoft.com/en-us/nuget/consume-packages/migrate-packages-config-to-package-reference),
[package build-file restrictions](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets),
[C# conditional compilation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/preprocessor-directives).

## Restore and clean outputs

Changing the instrumentation flag changes the dependency graph. Restore with the
same configuration and flag that will be used to build. Do not reuse an assets file
from a different combination through `--no-restore`.

For an SDK-style consumer, a stripped Release build is:

```powershell
dotnet build path/to/MyApp.csproj -c Release -p:EnableWinFormsMcpInstrumentation=false
```

For MSBuild on Windows, use `/restore` when combining restore and build:

```powershell
msbuild path/to/MyApp.csproj /restore /t:Rebuild /p:Configuration=Release /p:EnableWinFormsMcpInstrumentation=false
```

Use clean or isolated intermediate, output, and staging directories for shipping
builds. Simultaneously running different flag combinations against the same `obj`
directory can mix restore state. Staging by copying an old `bin` directory can also
include DLLs that are no longer part of the current build. A successful compiler
invocation alone does not establish a clean distribution.

Sources: [NuGet restore](https://learn.microsoft.com/en-us/nuget/consume-packages/package-restore),
[restore/build evaluation](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets#restoring-and-building-with-one-msbuild-command).

## Validation contract

The shipping-exclusion fixture must inspect results rather than merely check that
the flag is set. For its disabled build, require:

- No instrumentation packages or project references in the evaluated dependency graph.
- No references to bridge/protocol assemblies in application-owned binaries.
- No instrumentation-only assemblies, resources, or configuration in staged output.
- Normal application startup without any instrumentation activation path.

Exercise Debug/enabled, Release/disabled, and Release/enabled. Also exercise an
enabled-to-disabled rebuild to catch stale artifacts. Run absence verification
from repository tooling or CI, not from a target imported only by the bridge
package: that target would be absent in the very build it needs to verify.

The supplied Windows tests cover live controls, HWND recreation, replacement,
reparenting, stale generations, modal invocation, authentication and reconnects.
Portable tests cover framing and contract serialization, including malformed
messages. Screenshot limits, clipping and mixed-DPI behavior still need Windows
test coverage; the implementation-status document provides a manual desktop
exercise. Portable checks cannot replace the Windows fixture.

The implementation-status document must distinguish tests that are provided from
tests that were actually executed in a particular environment.

`eng/build.ps1` owns a clean validation directory for each configuration and runs
the checks sequentially. `eng/verify-stripped.ps1` reads PE metadata rather than
loading application assemblies into the inspection process. Its default forbidden
dependency list belongs to the deliberately minimal sample; an application that
already uses Newtonsoft.Json independently must use a suitable dependency policy.

After packing, `eng/verify-package-consumer.ps1` generates an isolated SDK-style
`net48` consumer that resolves the freshly built packages from the local package
directory. It builds both enabled and disabled variants. This verifies the actual
conditional `PackageReference` path as well as the sample's `ProjectReference`
path, without relying on a published NuGet package or altering a customer project.

## Packing and publishing

Each public package carries MIT license metadata and the package README. Keep
MinVer and test/build infrastructure private. The tool package declares
`PackAsTool=true` and a stable command name, `winforms-mcp-instrumented`.

Local `dotnet pack` output is a review artifact. Publishing to NuGet, creating a
remote repository, and tagging a release are separate actions; none is implied by
creating this draft. Third-party packages retain their own licenses.

