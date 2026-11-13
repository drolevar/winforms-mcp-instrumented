# Working on WinForms MCP Instrumented

## Read before changing behavior

Read `docs/00-overview.md`, `docs/02-architecture.md`, and
`docs/07-implementation-status.md` first. Consult the protocol and control
behavior documents before changing either side of the pipe. Explain the problem,
the reason for a decision, and its consequences in `docs/` before implementing a
change to architecture or public behavior. Keep the README a useful entry point.

This is an application instrumentation project. Preserve its defining boundaries:

- The application bridge targets .NET Framework 4.8. The MCP sidecar targets
  .NET 10. Modern MCP and hosting packages belong only to the sidecar.
- The protocol project targets .NET Standard 2.0 and has no WinForms/MCP SDK
  dependency. Its JSON dependency is intentional and centrally versioned.
- Standard controls work without app-specific registration. Builder extensions
  are optional. State an unsupported control capability explicitly.
- Access managed controls only on their owning UI thread. Keep pipe work off
  that thread. Never synchronously wait on a UI action that can open a modal
  dialog; return an action receipt and allow further requests.
- Identity belongs to the managed control instance and bridge generation, not
  its HWND, text, Name, or position. Never silently retarget a stale ID.
- Do not add arbitrary code evaluation or unrestricted property/method
  reflection as an automation shortcut.
- Limits, errors, capture modes, and implemented scope must remain explicit.

## Build and dependency rules

Use SDK-style projects and `WinFormsMcp.Instrumented.slnx`. Put dependency
versions only in `Directory.Packages.props`. Use MinVer for release versions;
the wire protocol version is a separate compatibility decision. Keep build
dependencies private. Do not invent a package publication or repository URL.

`EnableWinFormsMcpInstrumentation` controls the consuming application's source
and dependency graph. It must never turn the bridge library's own Release
package into a no-op. A disabled consumer build must have no instrumentation
assembly references or instrumentation-only deployment files. Apply the same
properties to restore and build; isolate build outputs between variants.

Typical Windows development commands (PowerShell 7, .NET 10 SDK, .NET 4.8):

```powershell
./eng/build.ps1
```

Use the exact commands in `docs/06-build-and-distribution.md` when building an
individual project or a source archive. Windows execution is required for the
WinForms tests; visible-screen capture additionally needs an interactive desktop.

## Validation and reporting

Test meaningful behavior: malformed framing, bounds, stale identities,
replacement versus handle recreation, modal progress, and build stripping.
Keep tests deterministic and explain Windows-only requirements. A successful
cross-compile does not establish that UI behavior worked on Windows.

When adding a capability, update the implementation status and relevant tests.
When a verification cannot run, record the command and concrete reason. Do not
report a planned or skipped test as passed. Avoid editing unrelated files.

Keep stdout reserved for MCP in the sidecar. Diagnostics go to stderr or bounded
tool results. Never log the pipe token. Preserve current-user pipe access and
explicit authentication; deserialize with type-name activation disabled.

Use small cohesive types, explicit error handling, and cancellation-aware IO.
Prefer the existing style and bounded APIs over new dependency chains. Format
C# with four spaces and use nullable annotations. Keep the bridge compatible
with its runtime even though it is compiled by a modern SDK.

The project is MIT licensed. Preserve license notices. Creating tags, publishing
packages, and changing external repositories are separate delivery actions and
should happen only when included in the user's requested work.
