# Implementation status and next work

## Why this document exists

Architecture documents describe the intended system; source code and executed
checks establish what exists today. Keeping those distinct prevents a coding
agent or package consumer from assuming a draft feature has been verified on a
Windows desktop merely because it compiles.

This snapshot was updated on **2026-11-13** after the first execution on
Windows, and again the same day after element adapters (menus, toolbars and a
public adapter seam) were implemented and run. It is an implementation draft;
the adapter seam has not been published yet (version `0.1.0-alpha.2` is on nuget.org without it; a `v0.1.0-alpha.1` tag exists, but nuget.org lists only
`0.1.0-alpha.2`, and `ElementAdapter` becomes
public API from the next published version). In a Git
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
| Builder | Optional limits, subtree exclusions and named actions for application-defined control types; `AddElementAdapter` registers an element adapter (name `^[a-z0-9._-]{1,48}\z`, `toolstrip` reserved) |
| Sidecar | Official MCP SDK stdio host, 19 fixed tools, startup-independent process management and bounded retained output; uniform camelCase `error.code/message/details` for sidecar and bridge errors |
| Elements | Identity registry keyed on the object, with adapter ownership; public `ElementAdapter` seam (namespace `WinFormsMcp.Instrumented.Elements`); built-in `ToolStripAdapter` for menus, toolbars, status strips and context menus reached through `Control.ContextMenuStrip`; `kind: "item"` nodes in `tree` and `inspect`; targeting by `generation` + `id` with a four-step stale check; item-rooted trees expand fully while window- or control-rooted trees collapse closed dropdowns (`childrenOmitted`, `childCount`); `invoke_control` `click`, `open`, `close` with prechecks, `not_realized`, `handlerDeferred` and the observed `opened`; `hello.elementAdapters`. See [element adapters](09-element-adapters.md) and [ADR 0004](adr/0004-element-adapters.md) |
| Shipping exclusion | Conditional consumer dependency and source symbol; graph, PE metadata, output and config checks |
| Sample | Standard controls, modal dialog, replacement/reparenting/HWND recreation, grid/tree, scrolling, a `MenuStrip` (nested submenu, a disabled item), a context menu and DPI-aware manifest |

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

The element-adapter work (below) was validated the same day on the same host by
a second complete `./eng/build.ps1 -DesktopExercise` run, which exited 0 with
`0 Warning(s)` and `0 Error(s)`. The counts in the table are from that second
run; the first run's counts were 8 server tests, 19 integration tests and 57
exercise checks. Rows without a count passed in both runs.

| Check | Result |
| --- | --- |
| Debug, Release/stripped and instrumented Release solution builds | Passed, zero warnings/errors |
| Protocol tests (net10.0) | 20 passed |
| Server contract tests (net10.0) | 12 passed (was 8; 4 new tests pin the 19-tool surface and the element wording of the `control_tree`, `inspect_control` and `invoke_control` descriptions) |
| **Windows integration tests (net48)** | **72 passed** (was 19): 7 supplied, 1 for `ensure_visible` ordering, 11 for per-control validation, paging bounds, redaction, exclusion, detachment and UI-thread races, and 53 for element adapters, the built-in `ToolStripAdapter` and the registry rules (see below) |
| Desktop exercise through the real sidecar and demo (`eng/exercise-demo.py`) | 72 of 72 checks passed (57 previous plus 15 for menus and context menus; matrix below) |
| Runtime endpoint check (`eng/verify-no-endpoint.ps1`) | Passed: with activation variables set, the instrumented build opened its pipe and the stripped build opened none |
| Stripped sample: dependency graph, PE metadata, output files, config | Passed |
| Clean instrumented-to-stripped rebuild | Passed |
| Stripping verifier negative control | Passed: rejected an instrumented build |
| NuGet packing (bridge, protocol, server) | Passed, version `0.1.0-alpha.0.2` in the first run (MinVer: no tag yet, two commits of height); the element run's consumer verification reported `0.1.0-alpha.2` |
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

### Element adapters, 2026-11-13

Executed on the same host (Windows 11, .NET SDK 10, interactive desktop):

| Command | Result |
| --- | --- |
| `dotnet test tests/WinFormsMcp.Instrumented.Tests/WinFormsMcp.Instrumented.Tests.csproj -p:MinVerVersionOverride=0.1.0-alpha.0` | 72 passed, 0 failed, 0 skipped (net48; final run on an unlocked interactive desktop after the final review fixes). The menu-open test reports Inconclusive when the test window cannot be activated, which happened in two earlier runs on a locked or disconnected session (71 passed, 1 skipped); 66 passed before the review fixes |
| `dotnet test tests/WinFormsMcp.Instrumented.Server.Tests -p:MinVerVersionOverride=0.1.0-alpha.0` | 12 passed (3 of the new tests failed before the descriptions were changed) |
| `dotnet test tests/WinFormsMcp.Instrumented.Protocol.Tests -p:MinVerVersionOverride=0.1.0-alpha.0` | 20 passed |
| `./eng/build.ps1 -DesktopExercise` | Exit 0, 0 warnings, 0 errors; 20 + 12 + 72 tests (final run after the review fixes, on an unlocked interactive desktop; two earlier runs on a locked or disconnected session exited 1 with failing capture checks); exercise 72 passed, 0 failed (57 previous, 15 new); stripped output, no-endpoint and enabled/stripped package-consumer verifications completed |

Most groups of element tests were written first and seen to fail before the
implementation (for example 9 of 9 new identity tests failed with a cast error,
then passed; 9 of 11 emission tests and 9 of 10 action tests failed first, the
remaining action test already passed and is a regression guard). The
`ToolStripAdapter` tests were not run red first: they were shown to fail (16 of
16) by temporarily disabling the implementation after it was written.

**Menu `open` spike.** Measured before the `open` action was built: with plain
WinForms a dropdown opened by `ShowDropDown()` was visible after 500 ms when its
form was the active window and not visible when another form of the process or
another process was in the foreground. This is why `open` is best-effort; the
numbers, what the spike cannot distinguish, and the reproduction are in
[element adapters](09-element-adapters.md#measured-results-and-open-questions).

**Observations from running the element work:**

| Observation | Cause | Resolution |
| --- | --- | --- |
| The first desktop-exercise run failed at the new context-menu check | The check expected the closed context menu to list its item in the window tree; by design a closed dropdown is collapsed (`childrenOmitted`, `childCount`) | Not a product defect. The check was split: collapsed in the window tree, item listed in a tree rooted at the strip |
| A `Form.Activate()` call did not make the form the active window in the test process | Windows' foreground rules; the bridge deliberately does not activate windows | The open/close integration test takes the foreground through a helper and reports inconclusive, not passed, if it cannot |
| The first run of the `ToolStripAdapter` tests hung | A test created a `Panel` on the test thread instead of the UI thread | Test bug, fixed in the test |

**Defects found by review and fixed, each with a failing-then-passing test:**
adapter, role and capability patterns used `$`, which accepts a trailing newline
(now `\z`); an adapter could emit a `Control` or `Form` as a second element
identity (now dropped and logged; see the Control-emission rule in [element adapters](09-element-adapters.md)); a split button clicked through
`PerformClick` and never raised its `ButtonClick` (now `PerformButtonClick`); a
closed context menu was not collapsed because it reported no `ChildCount`; and
`hello` did not list the built-in adapter.

### What the Windows suite pins

Beyond the supplied discovery/identity/modal/authentication tests, the suite
asserts these contracts against a real pipe and message loop:

| Contract | Test approach |
| --- | --- |
| 17 refused actions fail their receipt with a specific code (`disabled_control`, `hidden_control`, `read_only`, `unsupported_operation`, `invalid_argument`) | Table of requests; validation is observed on the UI thread, after the receipt was accepted |
| Valid `set_value`/`set_text` change the control and report `handlerReturned` | Read back through the tree |
| List paging: 100-item cap, last page, empty page at `startIndex == count`, `invalid_argument` past it or for `maxItems` 0/101 | 250-item list |
| Grid reads: 200-row cap, `hasMoreRows`, tail page, 50-column cap, non-grid target refused | 250-row grid |
| Text beyond `MaxTextLength` is cut and flagged `textTruncated`; password text never appears in tree or inspect output | 5,000-character text; secret-string search |
| Excluded subtrees are absent; renaming a live control into an exclusion makes its ID `stale_target` | Builder exclusion predicate |
| A control removed but alive is untargetable and regains the *same* ID on re-adding | Remove, inspect, re-add |
| A busy UI thread yields `ui_timeout`, and the bridge recovers afterwards | UI thread held on a gate past the 3 s test timeout |
| An action accepted while the UI thread is held, whose target is then disposed, fails with `stale_target` and never runs its handler | Gate releases a step that disposes the target before the queued action starts; click counter stays 0 |
| The 33rd pending action is refused with `action_limit`; the journal accepts work again after draining | 32 receipts queued behind a held UI thread |
| Menu, tool strip and status strip items appear with the right `parentId` chain; a closed dropdown is collapsed (`childrenOmitted`, `childCount`) in a window tree and expanded in a tree rooted at the item | Real `MenuStrip`, `ToolStrip`, `StatusStrip` fixture |
| Element identity: removed gives `stale_target`, re-added restores the same ID, moved keeps its ID, a disposed item, strip or form gives `stale_target`, a same-`Name` replacement gets a new ID, an excluded ancestor control hides elements and stales their IDs | Remove, re-add, move, dispose, replace and exclude through a real pipe, with a fake adapter that uses only the public API and with the built-in adapter |
| `click` on an item inside a closed menu runs its handler (a split button through `ButtonClick`); a disabled item gives `disabled_control`, `Available = false` gives `hidden_control`, an undeclared action gives `unsupported_operation`; a queued click whose item is removed first gives `stale_target` and never runs the handler | Handler counters; UI thread held on a gate |
| An item whose handler opens a modal dialog leaves its receipt `started` while the dialog is inspectable, then `completed` | Modal `ShowDialog` from a menu item |
| `open`/`close` report the observed `opened`; `open` of a nested item whose parent is closed gives `not_realized` | The window is activated first; the test reports inconclusive if it cannot be. It never asserts that a menu stays open in the background |
| A `ContextMenuStrip` reached through a control (or shared by two controls) appears once and is clickable; set-text on it gives `unsupported_operation`; assigning `ContextMenuStrip = null` stales it | Context-menu fixture |
| A password text box hosted in a strip does not leak into any tree or inspect output; a hosted control stays a control node | Secret-string search |
| Adapter budget, fault isolation and validation: an adapter that ignores `Add() == false` cannot exceed the node budget (`truncated`); a throwing adapter gives `childrenError: "adapter_failed"` and a diagnostic; invalid role or capability strings are rejected; a `Control` with a parent or a `Form` is dropped; duplicates are removed and `childrenOf` is set for a shared child collection; `handlerDeferred` is reported | Fake adapter, public API only; 2,500-item menu truncation test |
| Adapter registration: names match `^[a-z0-9._-]{1,48}\z` (trailing newline rejected), `toolstrip` is reserved, application adapters are consulted before the built-in one, `hello.elementAdapters` lists them then `toolstrip`; `AddAction` rejects `open` and `close` | Builder and hello tests |
| Control-only operations on an element give `unsupported_operation` after the stale check (a removed element gives `stale_target`) | Eight operations against an element target |

The three behaviors most likely to regress silently (the pending-action limit,
password redaction and the disabled-control check) were each broken on purpose
once; exactly the corresponding tests failed.

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
| Menus (15 checks) | The menu strip is a control node and `mnuFile` an `item` whose parent is the strip; the closed dropdown reports `opened: false`, `childCount: 2`, `childrenOmitted: true` and its children are absent from the window tree; a tree rooted at `mnuFile` lists `mnuExport`, `mnuCsv` and the disabled item with the parent chain; capabilities, text and `enabled` are read; reading did not open the menu; `invoke_control` click on `mnuCsv` completes and `lblStatus` reads `CSV exported`; a click on the disabled item fails with `disabled_control` and leaves the status unchanged; `open` on `mnuFile` completes and its `opened` equals a fresh `inspect_control`; subitems stay readable; `close` reports `opened: false`; the panel's context menu is found through the panel (`kind: "item"`, role `context_menu`, collapsed), a tree rooted at it lists the item, and clicking it sets `lblStatus` to `About clicked` |

The 100% scale of both displays is a limit of this evidence; see below. The
menu checks assert only that the `open` receipt equals a fresh `inspect_control`.
In the run of 2026-11-13 the observed `opened` was `true` (the demo window was
active); the inactive-window case, where `opened` can be `false`, was **not
executed** in the desktop exercise or through the bridge, only measured in the
plain-WinForms spike.

## Current limitations

- **One registered UI thread.** Other UI-thread windows are skipped and counted;
  their controls are not read through the wrong dispatcher. Supporting several
  UI threads needs an explicit dispatcher registry.
- **Managed Controls and ToolStrip elements.** ToolStrip items (menus, toolbars,
  status strips, context menus) are supported by the built-in adapter. Native
  common dialogs, hosted WPF/browser/native surfaces, virtual grid item
  identities (rows and cells are deliberately not tree nodes), third-party
  control suites (an application supplies its own `ElementAdapter`; none is
  shipped or was run), and custom owner-drawn hit targets still need dedicated
  adapters.
- **Menu `open` is best-effort.** In the plain-WinForms spike a dropdown was not
  visible after 500 ms when its form was not the active window (another form of
  the process active, or another process in the foreground). `open` always
  reports the observed state and the bridge does not activate windows, so in a
  background application the result can be `opened: false` although the call
  succeeded. This case was not executed through the bridge or in the desktop
  exercise. The spike does not show whether the menu never opened or opened and
  was dismissed, and it did not measure `close`, a menu that is deactivated after
  opening, `ToolStripDropDownButton` or context menus.
- **Element discovery limits.** Popups shown from code and never assigned to a
  control (for example `ContextMenuStrip.Show(point)` on an unassigned instance)
  are not discovered. There is no per-element exclusion: only an excluded
  ancestor control hides elements, so an application that must not expose a
  command should not instrument that window. Hosted controls inside a
  `ContextMenuStrip` or a never-shown dropdown are not reachable as control
  nodes, so a `hostedControlId` may refer to a node that no tree emits.
  `ScreenBounds` of items in an open overflow are computed through the owner strip
  and may be wrong. Element ids go stale more often than control ids. Selector
  resolution and `list_windows` stay control-only and form-only.
- **Element tree size.** Window- and control-rooted trees omit the children of
  closed dropdowns; read a menu with a tree rooted at the item. Membership checks
  for the stale rule are bounded by `MaxNodes`, so an item beyond that index of
  one strip reads as stale.
- **Semantic input.** `PerformClick`, text assignment, and control APIs do not
  simulate the full physical keyboard/mouse event sequence. There is no generic
  `SendInput` fallback in this draft.
- **Visible pixels only.** Capture may include an overlapping window and fails
  for unavailable or minimized targets. No offscreen, hidden-window, or
  occlusion-free capture is claimed.
- **Validated in a remote session.** The desktop exercise ran in a Windows
  remote-display session (the video adapters included "Microsoft Remote Display
  Adapter"). Screen capture worked there, but a remote session's display
  topology and scaling are controlled by the client, and a disconnected session
  has no capturable desktop. Per-monitor-DPI work is deferred until it can run
  on a local interactive desktop.
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

1. On a **local** interactive desktop (deferred: the first run was remote), run
   the desktop exercise at 125%/150% and with two monitors at different scales;
   add minimize-then-capture to it. Record results here.
2. Extend the suite to ListView, TreeView, TabControl and DateTimePicker
   validation, and to a timeout race for *mutations* (the suite covers read
   timeouts and queued-action disposal). The first group of standard-control
   tests is done.
3. ToolStrip/menu support (the public `ElementAdapter` seam, the built-in
   `ToolStripAdapter` and the object-keyed registry) is **done** (bead
   `WinFormsMcp_Instrumented-am4`). Still to do: a deliberate virtual-element
   identity model and grid adapter (a separate base class with paged reads and a
   freshness token, never row nodes), an inactive-window `open` check through the
   bridge, and the DPI work of item 1 (element bounds at other display scales
   were not run either). Extend multi-UI-thread coverage through registered
   dispatchers.
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
