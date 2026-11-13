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

## Windows host notes found by running the workflow

These came out of the first real Windows run (2026-11-13). Each one makes a
correct repository look broken, so they are recorded with their causes.

**Scripts from an extracted archive are blocked.** A source archive extracted on
Windows marks every file with a `Zone.Identifier` stream (Mark-of-the-Web). Under
the default `RemoteSigned` execution policy, `pwsh -File .\eng\build.ps1` then fails
with "is not digitally signed". This is a deliberate Windows safeguard, not a
defect in the script, so the repository neither disables it nor ships a bypass.
Review the scripts, then either run `Unblock-File` on the `eng` scripts yourself or,
better, clone the repository with Git: Git-written files carry no mark. Compiling
with `dotnet` is unaffected.

**A machine-level NuGet source mapping hides the local package feed.** The
package-consumer check restores from the freshly packed directory. If the user or
machine `NuGet.Config` defines `packageSourceMapping` (common in managed
environments, usually `nuget.org` → `*`), NuGet treats every source without a
mapping as ineligible and fails with `NU1101` even though the local feed has the
package. The generated consumer configuration therefore clears inherited
mappings and declares its own: `WinFormsMcp.Instrumented` and
`WinFormsMcp.Instrumented.*` come only from the local feed, everything else from
`nuget.org`. The side benefit is intentional: a package ID of ours can never be
satisfied from the public feed during this check.

**Close the demo before rebuilding.** A running instrumented demo locks its
copied bridge and protocol assemblies, so a rebuild fails with `MSB3021`/`MSB3027`.
The sidecar deliberately does not kill the application when MCP closes, so a
crashed agent session can leave one behind. Use `app_stop`, or stop the process
by its ID.

## Verification scripts and test projects

| Item | What it establishes | Needs |
| --- | --- | --- |
| `tests/WinFormsMcp.Instrumented.Protocol.Tests` | Framing and contract serialization | Any OS |
| `tests/WinFormsMcp.Instrumented.Server.Tests` | Sidecar contracts that need no app: error shape, option guards | Any OS |
| `tests/WinFormsMcp.Instrumented.Tests` | Bridge against a real pipe and STA message loop | Windows, net48 |
| `eng/verify-stripped.ps1` | Files and PE metadata of a stripped build | Any OS with pwsh |
| `eng/verify-package-consumer.ps1` | Enabled and stripped consumers of the packed packages | Packed packages |
| `eng/exercise-demo.py` | The whole workflow through the real sidecar and demo, including captures, desktop-edge clipping, modal dialog and restart | Windows, interactive desktop, Python 3 |
| `eng/verify-no-endpoint.ps1` | Runtime: a stripped build opens no pipe with activation variables set; an instrumented build does | Windows, interactive desktop |

`eng/build.ps1` runs the first five by default. `-DesktopExercise` adds the last
two. They are opt-in because they open windows, move the demo window across the
desktop and copy screen pixels; a headless runner cannot honestly run them.
`exercise-demo.py` writes its screenshots and a JSON report under
`artifacts/validation/`, and always stops the application it launched.

## Packing and publishing

Each public package carries MIT license metadata and the package README. Keep
MinVer and test/build infrastructure private. The tool package declares
`PackAsTool=true` and a stable command name, `winforms-mcp-instrumented`.

Local `dotnet pack` output is a review artifact. Publishing to NuGet, creating a
remote repository, and tagging a release are separate actions; none is implied by
creating this draft. Third-party packages retain their own licenses.

### Trusted publishing to nuget.org

`.github/workflows/publish.yml` publishes without a stored API key. NuGet
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
lets the workflow exchange GitHub's OIDC token for a short-lived nuget.org key
(`NuGet/login`). There is no long-lived secret to leak or rotate; the only stored
value, `NUGET_USER`, is a public profile name.

Why it is shaped this way:

- **Tag-triggered only.** Package IDs and versions are permanent on nuget.org, so
  nothing publishes from a branch or pull request. A `v*` tag is the deliberate act.
- **It reuses `eng/build.ps1`.** The tests, stripping checks, packing and
  packed-package restore that gate a developer build gate the release too; a
  failure publishes nothing. There is no second, unverified packing path.
- **The tag is the version.** MinVer derives it, so the workflow checks out full
  history and verifies that all three packages carry the tag's version.
- **The tag must be on `main`.** A tag on an unreviewed branch commit cannot publish.
- **A `release` environment** holds `NUGET_USER` and can require a reviewer's
  approval before the job runs.

One-time setup (the repository owner does this; nothing in the repository can):

| Where | What |
| --- | --- |
| nuget.org → Trusted Publishing → Create | Owner: the publishing account. GitHub Actions; repository owner/name of this repository; workflow file `publish.yml`; environment `release`. Scope **Push** only (not unlist, not stage); initially "new packages and versions", then "only new versions" once all three IDs exist |
| Same form, package patterns | The exact IDs `WinFormsMcp.Instrumented`, `WinFormsMcp.Instrumented.Protocol`, `WinFormsMcp.Instrumented.Server`, one per line. Exact IDs rather than `WinFormsMcp.Instrumented*` so a compromised workflow cannot create or update any other package |
| GitHub → Settings → Environments | `release`, secret `NUGET_USER` = nuget.org profile name (not an e-mail); required reviewers recommended |

The policy matches the workflow filename and environment name exactly; a mismatch
fails as "no matching policy". The first release creates the package IDs for good,
so check them first: no `PackageId` is set, so they default to the project names.
To release, push a tag such as `v0.1.0-alpha.1`.

