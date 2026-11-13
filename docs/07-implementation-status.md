# Implementation status and next work

## Why this document exists

Architecture documents describe the intended system; source code and executed
checks establish what exists today. Keeping those distinct prevents a coding
agent or package consumer from assuming a draft feature has been verified on a
Windows desktop merely because it compiles.

This snapshot was updated on **2026-11-13** after the first execution on
Windows. It is an implementation draft, not a published release. In a Git
checkout MinVer derives the version from history and tags (`0.1.0-alpha.0.N`
before any tag exists); a source archive without Git history uses the explicit
fallback `0.1.0-alpha.0`.

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
| Sidecar | Official MCP SDK stdio host, 19 fixed tools, startup-independent process management and bounded retained output; uniform camelCase `error.code/message/details` for sidecar and bridge errors |
| Shipping exclusion | Conditional consumer dependency and source symbol; graph, PE metadata, output and config checks |
| Sample | Standard controls, modal dialog, replacement/reparenting/HWND recreation, grid/tree, scrolling and DPI-aware manifest |

The protocol includes selectors for bridge-level integrations; the fixed MCP
tools use observed `generation`/`id` targets. Selection positions and tree paths
are observations of the current item view, not persistent identities for
virtual elements.

## Validation performed

This section records what was actually executed, where, and what remains
unexecuted. It supersedes the cross-build-only record of the draft's first
snapshot. A test that is provided but was not run is listed as not run.

### Windows validation, 2026-11-13

Host: Windows 11 Enterprise 10.0.26300, .NET SDK 10.0.401 (selected by
`global.json` among several installed SDKs), PowerShell 7.6, Python 3.12, two
2560×1440 displays at **100% scale** in an interactive session. The complete
`eng/build.ps1 -DesktopExercise` workflow passed in one invocation with zero
compiler warnings or errors.

| Check | Result |
| --- | --- |
| Debug, Release/stripped and instrumented Release solution builds | Passed, zero warnings/errors |
| Protocol tests (net10.0) | 20 passed |
| Server contract tests (net10.0, new) | 8 passed |
| **Windows integration tests (net48)** | **8 passed**: 7 supplied plus 1 added for `ensure_visible` ordering |
| Desktop exercise through the real sidecar and demo (`eng/exercise-demo.py`) | 57 of 57 checks passed (matrix below) |
| Runtime endpoint check (`eng/verify-no-endpoint.ps1`) | Passed: with activation variables set, the instrumented build opened its pipe and the stripped build opened none |
| Stripped sample: dependency graph, PE metadata, output files, config | Passed |
| Clean instrumented-to-stripped rebuild | Passed |
| Stripping verifier negative control | Passed: rejected an instrumented build |
| NuGet packing (bridge, protocol, server) | Passed, version `0.1.0-alpha.0.2` (MinVer: no tag yet, two commits of height) |
| Packed-package consumer, enabled and stripped | Passed, after the fixture fix below |
| Packed server installed as a .NET tool, offline MCP smoke test | Passed: 19 tools, strict camelCase error code |
| Release server MCP smoke test | Passed |

The first run of the documented script was not clean. The failures and what they
turned out to be:

| Observation | Cause | Resolution |
| --- | --- | --- |
| `build.ps1` refused to load ("not digitally signed") | Archive extraction left a Mark-of-the-Web on the scripts; policy is `RemoteSigned` | Not a code defect. Documented in [build and distribution](06-build-and-distribution.md); the repository does not bypass the safeguard |
| `NU1101` in the package-consumer check | A user-level `packageSourceMapping` made the local feed ineligible | Fixed in `eng/verify-package-consumer.ps1`: the fixture declares its own mapping |

### Defects found by running on Windows, and fixed

| Defect | Evidence | Fix and regression coverage |
| --- | --- | --- |
| `ensure_visible` on a control inside a never-shown tab selected the tab but did not scroll, yet its receipt said `completed`; a later `scroll_control` then landed at the wrong offset | Child 1,000 px below the viewport after a `completed` receipt | Two-pass reveal: select ancestor tabs, then scroll innermost-out. Regression test fails on the old code (`child top 1781, viewport 79..243`) and passes on the new; desktop exercise covers it |
| Application-bridge errors reached MCP clients as `Code`/`Message`/`Details` while sidecar errors used `code`/`message`; `smoke-mcp.py` silently accepted both | Strict-key exercise run failed four checks | Single `ErrorObject` renderer used at both sites; server tests pin the shape; smoke test and exercise now require `code` |

Both defects were invisible to the supplied tests: the first needs a never-shown
tab plus a scrollable container, the second only appears in the MCP result text.

### What the desktop exercise covered

Run `eng/exercise-demo.py` (or `eng/build.ps1 -DesktopExercise`). Each item
asserts observable state, not merely that a receipt completed.

| Area | Verified behavior |
| --- | --- |
| Lifecycle | Status offline, launch, `stop`, relaunch gives a new generation, `restart`; MCP stayed connected throughout |
| Text and buttons | `set_text`, then `invoke_control` on `btnGreet`, then `lblStatus` read back as `Hello, Ada!` |
| Values | Check box, radio, numeric (and out-of-range rejection with `invalid_argument`), trackbar, date, combo, list |
| Redaction | Password text is `null` and `textRedacted` in tree and inspect, including after `set_text` |
| Tabs, grid, tree | Tab selection, bounded `read_grid` page, cell selection, grid row scroll, tree path selection |
| Scrolling | `ensure_visible` across tab and panel; absolute `scroll_control` offset verified from reported geometry |
| Modal dialog | Opener returns a `queued` receipt immediately; stays `started` while the dialog is listed and inspectable; closing completes both actions; handler continuation ran |
| Identity | HWND recreation and reparenting keep the ID; replacement changes it; the old ID gives `stale_target` for inspect and for a queued mutation; a previous generation gives `stale_generation` |
| Capture | Control, partially clipped child (reported `clipped`, visible height equals the container edge), fully scrolled-out child refused with `capture_unavailable`, region (exact pixel size), window with frame, application (two images while a modal dialog is open), zero-size region rejected; PNG dimensions equal reported pixels; metadata declares `screen_copy` and possible occlusion |
| Desktop edges | Window dragged past the right edge, then past the left and bottom edges of the virtual desktop: capture was clipped to the desktop and reported `clipped` with the requested bounds |
| Overlap | The application capture taken during the modal dialog shows the dialog drawn over the main window, as documented |

The 100% scale of both displays is a limit of this evidence; see below.

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
  for unavailable or minimized targets. No offscreen, hidden-window, or
  occlusion-free capture is claimed.
- **Display scaling is validated at 100% only.** Both test displays were at 100%
  and the demo window reported 96 DPI. Per-monitor-DPI-aware behavior (the demo
  manifest declares PerMonitorV2), a window straddling two monitors with
  different scales, and a scale change while running were **not executed**:
  that needs display-setting changes on a machine the validator did not own.
  Treat 125%/150%/mixed-scale crops as unverified until run.
- **Minimized-window rejection is covered by code, not by the exercise.** The
  exercise did not minimize the demo window; `ScreenCapture` rejects it, but no
  executed check shows that.
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

1. Run the desktop exercise at 125%/150% and with two monitors at different
   scales; add minimize-then-capture to it. Record results here.
2. Add tests for representative standard controls that the exercise touches but
   the suite does not pin (action validation per control, item paging, a
   timeout/disposal race on the UI thread, a control disposed while an action is
   `queued`).
3. Add ToolStrip/menu adapters and a deliberate virtual-element identity model.
   Extend multi-UI-thread coverage through registered dispatchers.
4. Add alternative capture backends only with distinct capability reporting and
   tests for occlusion, minimization, DPI, and child-window behavior.
5. Add distribution metadata for the actual remote repository, choose a release
   version/tag, and publish only when explicitly requested.

## Desktop exercise

The exercise formerly described here as a manual procedure is now
`eng/exercise-demo.py`; the matrix above lists what it asserts. Run it with

```powershell
./eng/build.ps1 -DesktopExercise
```

It launches the Debug demo through the real sidecar, moves the demo window across
the desktop edges, and writes screenshots and `exercise-report.json` under
`artifacts/validation/`. Look at the saved images as well as the pass/fail result:
the checks compare reported geometry and pixel sizes, not image content. The
procedure still has two manual parts: repeat it at other display scales, and
inspect the screenshots when changing capture code.
