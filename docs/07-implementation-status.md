# Implementation status and next work

## Why this document exists

Architecture documents describe the intended system; source code and executed
checks establish what exists today. Keeping those distinct prevents a coding
agent or package consumer from assuming a draft feature has been verified on a
Windows desktop merely because it compiles.

This snapshot was prepared on **2026-11-13**. It is an implementation draft,
not a published release. The source archive uses the explicit MinVer fallback
version `0.1.0-alpha.0` when it has no Git history.

## Implemented source

| Area | Current behavior |
| --- | --- |
| Repository | `.slnx`, six SDK-style projects, central package versions, MinVer, MIT metadata, shared agent guidance |
| Protocol | Version 1, strict UTF-8 length-prefixed JSON, 16 MiB frame limit, bounded JSON depth, request IDs and errors |
| Activation | Optional environment activation; same-user Windows pipe ACL, token authentication, per-connection hello handshake |
| Transport | One active bridge connection, bounded frame IO, cancellation, reconnect on the next sidecar request |
| Discovery | Current open forms and recursive control collections on one registered UI thread; no static startup-only tree |
| Identity | Weak managed object registry plus bridge generation; replacement/disposal invalidates old targets; HWND recreation and reparenting preserve identity |
| Inspection | Flat bounded tree, state and capabilities; password-value redaction; bounded inspect-only list items and tree index paths |
| Actions | Semantic invocation/focus, editable text, checked state, numeric/date values, selection, scrolling and ensure-visible |
| Modal behavior | Queued receipts with `queued`, `started`, `completed`, `failed`; pipe loop remains independent of UI handlers |
| Data | Bounded DataGridView row/column collection-index reads using formatted cell values |
| Capture | Visible-screen PNGs of control/window/region/app windows; ancestor and desktop clipping; metadata and combined image budget |
| Builder | Optional limits, subtree exclusions and named actions for application-defined control types |
| Sidecar | Official MCP SDK stdio host, 19 fixed tools, startup-independent process management and bounded retained output |
| Shipping exclusion | Conditional consumer dependency and source symbol; graph, PE metadata, output and config checks |
| Sample | Standard controls, modal dialog, replacement/reparenting/HWND recreation, grid/tree, scrolling and DPI-aware manifest |

The protocol includes selectors for bridge-level integrations; the fixed MCP
tools use observed `generation`/`id` targets. Selection positions and tree paths
are observations of the current item view, not persistent identities for
virtual elements.

## Validation performed in this environment

The build host is Linux with a locally installed .NET SDK **10.0.404**. The SDK
download was checked against the SHA-512 hash in Microsoft's release metadata.

| Check | Result |
| --- | --- |
| Solution format and project evaluation | Passed |
| Full solution Debug cross-build | Passed, zero warnings/errors |
| Full solution Release cross-build | Passed, zero warnings/errors |
| Protocol tests on .NET 10 | 20 passed, zero failed/skipped |
| MCP stdio smoke test | Passed: initialize, 19 tools, schemas, offline status, unavailable-app error, session continuity, EOF shutdown |
| Stripped Release consumer | Passed: no bridge/protocol/JSON dependency, no forbidden PE assembly references, no instrumentation output |
| Explicitly instrumented Release consumer | Passed: bridge present |
| Clean instrumented-to-disabled rebuild | Passed: stripping assertions hold after the transition |
| Stripping verifier negative control | Passed: correctly rejected the instrumented build |
| NuGet packaging | Passed: bridge, protocol and server tool packages at `0.1.0-alpha.0` |
| Installed .NET tool | Passed: installed the locally packed server into an isolated tool directory and repeated the 19-tool MCP smoke test through its generated command |
| Actual packed-package consumer | Passed: isolated net48 consumer restored from the local feed, with a fresh cache; enabled/disabled builds and stripping checks passed |
| Package license metadata | Passed: MIT expression verified |
| Windows integration test project | Cross-compiled, zero warnings/errors; seven tests present |
| Windows integration test execution | **Not run: this host is Linux** |
| Interactive screen/DPI capture | **Not run: requires a Windows interactive desktop** |

CI is provided, but no remote CI run is claimed by this snapshot.

The environment restricts local socket creation used by out-of-process MSBuild
nodes. Validation used serial/in-process compilation options:

```text
-m:1 /nodeReuse:false /p:BuildInParallel=false /p:UseSharedCompilation=false
```

That is a host-specific verification adjustment. The ordinary Windows build
workflow does not require those options.

The complete `eng/build.ps1` workflow was also run in one invocation after the
final source changes, including package creation and the isolated consumer
checks. The final Release server passed the MCP smoke test. The test runner
printed an informational fallback notice for `VSTEST_ARTIFACTS_DIRECTORY`; the
protocol run completed with all 20 tests passing.

## Windows test coverage provided

The seven integration tests use a real named pipe and an STA thread running a
WinForms message loop. They cover dynamic discovery, stale IDs after object
replacement, stable IDs after HWND recreation, stable IDs after reparenting,
stale generations, modal action progress, and authentication/reconnection.

Run `eng/build.ps1` on Windows to execute them. The script also restores and
checks the consumer variants, packs the libraries/tool, and verifies consumption
of the actual local NuGet packages. See the build document for the complete
workflow.

## Current limitations

- **One registered UI thread.** Other UI-thread windows are skipped and counted;
  their controls are not read through the wrong dispatcher. Supporting several
  UI threads needs an explicit dispatcher registry.
- **Managed Controls first.** ToolStrip/menu items, native common dialogs,
  hosted WPF/browser/native surfaces, virtual grid item identities, and custom
  owner-drawn hit targets need dedicated adapters.
- **Semantic input.** `PerformClick`, text assignment, and control APIs do not
  simulate the full physical keyboard/mouse event sequence. There is no generic
  `SendInput` fallback in this draft.
- **Visible pixels only.** Capture may include an overlapping window and fails
  for unavailable or minimized targets. Mixed-DPI behavior still needs Windows
  validation. No offscreen, hidden-window, or occlusion-free capture is claimed.
- **Exclusion is not pixel redaction.** A containing-window screenshot can show
  pixels belonging to an excluded child. Password text is omitted from control
  metadata; the screenshot is the actual screen.
- **Bounded history.** Action receipts and diagnostics expire. Actions already
  started are not rolled back by cancellation or a disconnected client.
- **Limited diagnostics.** The bridge records its own failures; it does not
  install a global exception policy or promise full crash dumps. App stdout and
  stderr are retained only for processes the sidecar launches.
- **Development lifecycle.** The sidecar can launch/close/restart its configured
  executable. Rebuilding is left to the coding agent's normal build tools.

## Prioritized continuation

1. Run the existing Windows suite and the manual desktop exercise below. Resolve
   observed failures before calling the package production-ready.
2. Add tests for representative standard controls, action validation, item
   paging, timeout/disposal races and ancestor clipping on Windows.
3. Add ToolStrip/menu adapters and a deliberate virtual-element identity model.
   Extend multi-UI-thread coverage through registered dispatchers.
4. Add alternative capture backends only with distinct capability reporting and
   tests for occlusion, minimization, DPI, and child-window behavior.
5. Add distribution metadata for the actual remote repository, choose a release
   version/tag, and publish only when explicitly requested.

## Manual Windows desktop exercise

Launch the instrumented demo through the sidecar. Inspect the tree, edit
`txtName`, click `btnGreet`, and verify `lblStatus`. Exercise the modal dialog
while its opening receipt is `started`. On the Dynamic identity tab, observe
the ID across replacement, reparenting, and native-handle recreation. Select and
scroll grid/list/tree controls. Capture a partially clipped child, a region,
a window, and the application at multiple display scales. Repeat with a window
partially outside the desktop and with another window overlapping it; check the
reported bounds and limitations against the pixels. Finally start the stripped
sample with activation variables set and confirm there is no bridge endpoint.

This exercise checks behavior that a Linux cross-build cannot establish.
