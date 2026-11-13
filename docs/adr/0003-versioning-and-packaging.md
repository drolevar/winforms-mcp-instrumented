# ADR 0003: One product version, separate deployment boundaries

- Status: Accepted for the implementation draft
- Date: 2026-11-13

## Context

The bridge must run inside an existing .NET Framework 4.8 application. The MCP
server needs a modern runtime and must survive application rebuilds and restarts.
They share a wire contract, so developers need a clear way to identify compatible
builds. The project also needs readable solution and dependency files that coding
agents can update without scattering version edits throughout the repository.

## Decision

Use SDK-style projects and one `.slnx` solution. Declare exact dependency versions in
`Directory.Packages.props`; use common MSBuild files only for settings that truly
apply to the affected projects. Target the bridge at `net48`, shared protocol at
`netstandard2.0`, and separate MCP server at `net10.0`.

Produce separate bridge and protocol library packages plus a server .NET tool
package. Applications reference only the bridge and its transitive protocol
dependency. Installing the tool is a developer/MCP-client operation.

Use MinVer to derive the same product version from Git tags for all public packages.
The initial version range is `0.1`, release tags use the `v` prefix, and untagged
development builds have prerelease versions. CI fetches history and tags. A source
archive uses an explicit prerelease version override because it has no Git history.

Keep the pipe protocol version separate from the NuGet product version. A patch
release can remain wire-compatible, while a breaking protocol change must be
detectable before any UI action is attempted. Matching package numbers are useful
for diagnosis; they do not replace a protocol handshake.

The consuming application's build flag omits bridge dependencies and hook code
together. It does not disable compilation of the bridge project itself. A Release
build of the package is still a functional bridge; a stripped Release build of an
application does not include that bridge.

## Why

**One version reduces coordination mistakes.** The initial product is small enough
that independently versioning each package would add release and support work
without providing a useful deployment advantage. We can still evolve the private
wire protocol explicitly.

**Separate packages preserve process ownership.** A modern server library cannot
be accidentally loaded into the legacy application through the recommended setup.
The application remains rebuildable while the agent's MCP connection stays alive.

**Central package versions make dependency changes reviewable.** A reviewer can see
which external packages changed without searching every project. This is especially
valuable for keeping MCP hosting dependencies out of the bridge.

**Tag-derived versions make releases traceable.** A version identifies reviewed
source rather than a manually edited number that could differ between assemblies.
Explicit archive overrides keep downloads buildable while preserving the distinction
between a local draft build and a release.

**Modern build files do not require a runtime migration.** We can improve build
maintenance while preserving the user's .NET Framework 4.8 deployment constraint.

## Alternatives considered

- **One combined package:** easy to install but obscures the process/runtime boundary
  and encourages the application to own the server's lifetime.
- **Independent per-project release versions:** possible later, but adds unnecessary
  compatibility bookkeeping during initial development.
- **Hand-edited assembly/package versions:** introduces several places to update
  and does not inherently identify the source revision.
- **A modern .NET target for the bridge:** would require an application runtime
  migration, which is outside the problem this project is solving.
- **Build-time tooling as runtime dependencies:** exposes consumers to unrelated
  packages and weakens the stripped-build promise.

## Consequences

Release builds need Git history and tags or an explicit archive version override.
The three public packages are packed together and should be released together.
Packaging checks must inspect license/readme metadata and dependency boundaries.
Windows remains necessary for runtime verification of WinForms behavior.

The first draft is an implementation starting point, not a published package or a
release claim. Detailed commands and the current dependency baseline are in
[Build and distribution](../06-build-and-distribution.md).

## References

- [MinVer options and version derivation](https://github.com/adamralph/minver)
- [.NET SDK SLNX support](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default)
- [NuGet central package management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- [Create a .NET tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create)
- [NuGet package build-file restrictions](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets)
