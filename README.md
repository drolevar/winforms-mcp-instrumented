# WinForms MCP Instrumented

**MCP automation through an application's own Windows Forms controls.**

WinForms MCP Instrumented connects a .NET Framework 4.8 WinForms application to
an AI coding agent through an independent MCP server. The application exposes
its live managed control tree and standard control operations; the server keeps
the MCP connection alive while the application closes, crashes, or restarts.

This is a documentation-first implementation draft. Package names are proposed
and no public release is implied. Read the
[purpose and rationale](docs/00-overview.md) first, then the
[implementation status](docs/07-implementation-status.md) for the exact supported
scope and validation results.

## Why this exists

A coding agent needs more than source code to work on a desktop application. It
needs to see which controls exist now, understand their current state, operate
them, and inspect the result. The application can expose that information more
directly by cooperating.

The process boundary serves two concrete purposes:

- The modern MCP SDK and hosting dependencies stay outside the .NET Framework
  application's dependency graph.
- The agent can restart the application without losing its MCP server session.

The in-process bridge is generic. Standard controls need no individual
registration. Optional builder configuration supplies exclusions, limits, and
custom semantic actions. A build property removes the bridge and all its calls
from the consuming application's shipping build.

## What the draft includes

- Live windows and control-tree inspection, with opaque instance identities.
- Menu, toolbar, status-strip and context-menu items in the same tree, with
  click and best-effort open/close through the existing `invoke_control` tool,
  and a public adapter seam for third-party control suites.
- Semantic button invocation, focus, text, numeric/date values, checked states,
  selections, scrolling, and bounded DataGridView reads.
- Dynamic creation, replacement, disposal, and reparenting observed on the next
  request. Native handle recreation preserves managed control identity.
- Queued action receipts, so a modal dialog can be inspected and closed while
  the action that opened it is still in progress.
- Visible-screen PNG capture of controls, windows, regions, or app windows,
  with bounds and capture metadata.
- A separate stdio MCP tool with launch, close, restart, status, and bounded
  retained application output.
- A sample app, protocol tests, Windows integration tests, build-exclusion
  checks, actual packed-package consumer checks, and Windows/portable CI.

The current bridge covers **one registered UI thread**, managed `Control`
instances, and menu, toolbar, status-strip and context-menu items (`kind: "item"`
nodes that can be listed, inspected and clicked). Menu `open` is best-effort and
reports the observed state: in a plain-WinForms measurement (once per condition) a
dropdown did not stay visible while its window was not active, and that
inactive-window case has not been run through the bridge. Applications can add
adapters for third-party control suites. Native common dialogs, hosted foreign
UI, and virtual elements (grid rows and cells) do not yet have adapters. Popups shown from code and never assigned to a control are
not discovered, and individual elements cannot be excluded. Screen capture needs
an interactive desktop and visible, non-minimized windows; overlapping windows can
appear in the pixels. See [control behavior](docs/04-controls-and-capture.md) and
[element adapters](docs/09-element-adapters.md).

## Build and run on Windows

Prerequisites: a .NET 10 SDK, PowerShell 7, and Windows with .NET Framework 4.8.
The build obtains Framework reference assemblies through NuGet. `global.json`
allows the latest installed .NET 10 feature band. Use an IDE/MSBuild version
that supports `.slnx` if opening the solution in an IDE.

```powershell
dotnet build WinFormsMcp.Instrumented.slnx -c Debug
```

The complete workflow (all tests, stripping checks, packing, and a restore of the
packed packages) is `./eng/build.ps1`; add `-DesktopExercise` on an interactive
desktop to also drive the demo through the real MCP server and capture the
screen. If PowerShell reports that the script "is not digitally signed", the
files carry a Mark-of-the-Web from an archive download; see
[Windows host notes](docs/06-build-and-distribution.md#windows-host-notes-found-by-running-the-workflow).

The default Debug sample includes the bridge. Running its executable normally
does not open an endpoint; the server supplies activation settings when it
launches the app.

Example MCP client configuration, using the built server directly:

```json
{
  "mcpServers": {
    "winforms-instrumented": {
      "command": "dotnet",
      "args": [
        "C:\\src\\winforms-mcp-instrumented\\src\\WinFormsMcp.Instrumented.Server\\bin\\Debug\\net10.0\\WinFormsMcp.Instrumented.Server.dll",
        "--app",
        "C:\\src\\winforms-mcp-instrumented\\samples\\InstrumentedDemo\\bin\\Debug\\net48\\InstrumentedDemo.exe"
      ]
    }
  }
}
```

Adjust the paths and your client's configuration envelope. The server starts
immediately; `app_launch` starts the configured application. `app_status` remains
available even while the app is stopped. No pipe token needs to be committed to
the MCP configuration in launch mode.

For a first exercise, ask the agent to:

1. Call `app_launch`, then `control_tree`.
2. Find `txtName`, call `set_text`, and wait for its action receipt to complete.
3. Invoke `btnGreet`, inspect `lblStatus`, and take a capture.
4. Invoke `btnOpenDialog`; inspect the new window while the opening receipt is
   `started`; invoke `btnCloseDialog`; confirm the opening action completes.
5. Select the Dynamic identity tab. Observe `txtDynamic`, replace it, and confirm
   that its old ID fails. Recreate its native handle and confirm the new managed
   object's ID stays stable.

## Integrate an application

The app references only `WinFormsMcp.Instrumented`. The server is installed or
run separately. The startup hook is deliberately small:

```csharp
// Inside the existing STA Main, after normal WinForms initialization:
#if WINFORMS_MCP_INSTRUMENTED
using (WinFormsMcp.Instrumented.McpBridge.Start())
{
    Application.Run(new MainForm());
}
#else
Application.Run(new MainForm());
#endif
```

For optional customization:

```csharp
#if WINFORMS_MCP_INSTRUMENTED
using (WinFormsMcp.Instrumented.McpBridge.Start(builder =>
{
    builder.MaxNodes = 1500;
    builder.Exclude(control => control.Name == "internalSettingsPanel");
    builder.AddAction<MyCustomControl>("reset_view", control => control.ResetView());
}))
{
    Application.Run(new MainForm());
}
#else
Application.Run(new MainForm());
#endif
```

`MyCustomControl` is an illustrative app-defined type. Exclusions affect
discovery/actions, not pixels captured from a containing window. Keep all custom
instrumentation code under the same build condition.

See the complete [conditional PackageReference integration](docs/05-integration-and-stripping.md).

## Shipping builds

The sample defaults to inclusion in Debug and exclusion in Release. Override the
decision explicitly when useful:

```powershell
# Shipping variant: no instrumentation dependency or startup code.
dotnet build samples/InstrumentedDemo -c Release -p:EnableWinFormsMcpInstrumentation=false

# Optimized variant for development and UI investigation.
dotnet build samples/InstrumentedDemo -c Release -p:EnableWinFormsMcpInstrumentation=true
```

Use clean or separate intermediate/output directories when switching variants.
The full validation script does this and inspects assembly references, restored
dependencies, and deployed files:

```powershell
./eng/build.ps1
```

On non-Windows hosts it cross-compiles the Framework projects and explicitly
skips Windows runtime tests. The protocol tests run on .NET 10. A cross-compile
does not verify WinForms behavior or screen capture.

## Packages and versions

| Package | Target | Role |
| --- | --- | --- |
| `WinFormsMcp.Instrumented` | `net48` | Application bridge |
| `WinFormsMcp.Instrumented.Protocol` | `netstandard2.0` | Shared JSON contract and framed transport |
| `WinFormsMcp.Instrumented.Server` | `net10.0` | Independent .NET tool, command `winforms-mcp-instrumented` |

`Directory.Packages.props` owns dependency versions. MinVer derives product
versions from `v`-prefixed Git tags; untagged development uses the `0.1` alpha
line. A downloaded source archive uses the explicit prerelease fallback
`0.1.0-alpha.0`, never an invented stable release. The private pipe protocol has
its own compatibility version.

From the next published version after `0.1.0-alpha.2`, `ElementAdapter` and its
companion types are public API, and `McpBridgeBuilder.AddAction` reserves the
action names `open` and `close`: a consumer of `0.1.0-alpha.x` that registered a
custom action with one of those names must rename it.

`eng/build.ps1` writes local packages under `artifacts/packages`. After building:

```powershell
dotnet tool install WinFormsMcp.Instrumented.Server --tool-path ./artifacts/tools --add-source ./artifacts/packages --prerelease
```

When selecting among several builds, pass the exact locally produced package
version with `--version`. See [build and distribution](docs/06-build-and-distribution.md)
for details and source-archive behavior. Publishing is a separate step.

## Documentation

| Document | What it explains |
| --- | --- |
| [Overview](docs/00-overview.md) | The problem, intended experience, and reading order |
| [Purpose and principles](docs/01-purpose-and-principles.md) | Why the product has these boundaries |
| [Architecture](docs/02-architecture.md) | Lifetimes, dependencies, UI dispatch, identities |
| [Bridge protocol](docs/03-bridge-protocol.md) | Framing, handshake, targets, errors, receipts |
| [Controls and capture](docs/04-controls-and-capture.md) | Standard actions and their practical limits |
| [Integration and stripping](docs/05-integration-and-stripping.md) | Minimal app setup and shipping guarantees |
| [Build and distribution](docs/06-build-and-distribution.md) | SDK projects, central versions, MinVer, packages, CI |
| [Implementation status](docs/07-implementation-status.md) | Implemented scope, validation, and next work |
| [Tool reference](docs/08-mcp-tools.md) | MCP tool arguments and common workflows |
| [Element adapters](docs/09-element-adapters.md) | Menus, toolbars and third-party elements: seam, identity, measured limits |
| [Architecture decisions](docs/adr/) | Decision records and consequences |

Coding agents should read [AGENTS.md](AGENTS.md). Claude Code guidance is in
[CLAUDE.md](CLAUDE.md).

## License

[MIT](LICENSE), copyright 2026 WinForms MCP Instrumented contributors.
