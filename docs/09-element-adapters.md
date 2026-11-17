# Element adapters: menus, toolbars and other non-Control elements

**Status:** implemented (built-in `ToolStripAdapter`, public `ElementAdapter`
seam, tree, inspect and `invoke_control` support), validated on Windows on
2026-11-13. What was executed, what was not, and the known limitations are
recorded in [implementation status](07-implementation-status.md). Decision
record: [ADR 0004](adr/0004-element-adapters.md). Tracking: bead
`WinFormsMcp_Instrumented-am4`. "Measured results and open questions" below
separates what was measured from what is still unverified. Virtual-item and grid
support is not part of this work (see "Grid readiness").

## Problem

The bridge sees `System.Windows.Forms.Control` instances only. Registry, tree
walk, scope check, inspection and mutation all assume that a target is a
`Control` reachable through `Form.Controls`. Menu and toolbar entries
(`ToolStripItem`) are not controls. Third-party suites (ribbon and bar
libraries, grids) add elements that are neither controls nor reachable through
`Controls`. An agent therefore cannot open a menu, click a command, read
whether a command is enabled, or list a menu's subitems.

## Goals

An agent can, for menu and toolbar items:

- list them in the tree with their hierarchy;
- read name, text, type, enabled, available, checked, shortcut, child count and
  on-screen bounds (when shown);
- click one (semantic invocation), and open or close one that has a dropdown;
- read the subitems of a menu **without opening it**.

Other goals:

- Third-party control suites can be supported through a small public seam,
  **without any compile-time dependency of the public packages on that suite**
  and with a one-line registration in the consuming application.
- The seam does not preclude later grid support.

## Non-goals and anti-goals

- No vendor- or product-specific code, names or defaults in the public
  repository.
- No arbitrary reflection, code evaluation or declarative member mapping
  engine (see "Rejected alternatives").
- No grid, row or cell support now; no row identities or freshness-token wire
  fields now.
- No per-element exclusion predicate (only excluded ancestor controls hide
  elements; a control that is itself an element, such as a context menu strip, is
  hidden with its items when the existing predicate excludes it).
- No physical input injection. Item actions stay semantic (see
  [controls](04-controls-and-capture.md)).
- No new MCP tools; no protocol version change.
- Anti-goal: silently retargeting a stale element, or reporting an open that
  did not happen.

## Constraints

All of [CLAUDE.md](../CLAUDE.md) applies. The ones that shape this design: the
bridge targets .NET Framework 4.8 (no default interface members); identity
belongs to the managed instance and bridge generation; all element access is on
the registered UI thread; every list is bounded and says when it is truncated;
a UI action that can open a modal dialog is never waited for synchronously.

## Approaches considered

1. **Items join the existing tree behind an adapter seam (chosen).** Elements
   get the same `generation` + `id`, stale-target rules and tools as controls.
   Cost: the identity registry and tree walk are generalized.
2. **A separate menu tool family addressing items by path** (`File > Export`).
   Rejected: a path is text and position, which this project does not accept as
   identity; a renamed or localized item would silently retarget.
3. **UI Automation from the sidecar.** Rejected: weak data for third-party
   suites, forces physical input, and the project does not depend on
   accessibility quality.

## Core model

### Registry

The registry is keyed on the element object (reference identity, weak):

- `ConditionalWeakTable<object, ElementIdentity>` and a `Dictionary<string,
  ElementEntry>`.
- `ElementEntry` holds a weak `Target`, a weak `ObservedParent` and the
  `ElementAdapter? Owner` (null means a plain control).
- Every observation of an element refreshes its parent and owner (a null value
  never clears them). The adapter that emits an element first in a walk owns it,
  so adapters need no ownership test. The exception is a control the bridge walks
  itself: once an adapter-emitted unparented control gets a parent and the control
  walk registers it, its owner and observed parent are cleared and it is a control
  again.
- Pruning also drops entries whose adapter reports `IsAlive == false`.

### Node identity

| Source | Node |
| --- | --- |
| `ToolStripItem` | one node; its `DropDownItems` are its children whether the dropdown is open or closed |
| Owned dropdown of an item | never its own node; "open" is a state of the owner item |
| `ContextMenuStrip` | a node (role `context_menu`) found through the `Control.ContextMenuStrip` of any visited control; its `Parent` is null, so the control scope check cannot cover it |
| `ToolStripControlHost` | a node with `hostedControlId`; the hosted control stays a control child of the strip |
| Third-party command placement | the placement (one node per place the command appears), not the shared command object: only the placement carries bounds, open state and effective enabled state |
| Collection with no owner object | may be keyed by the collection object |

A command shown in several places yields several nodes. If several placements
share one child collection, children are emitted under the first placement the
walk reaches, and the others carry `childrenOf: <id of the first>`. Each
response keeps a reference-equality visited set that removes duplicates and
cuts cycles.

### Stale detection (replaces `IsInScope` for elements)

1. `Owner.IsAlive(element)` must be true (for example `!IsDisposed`).
2. `parent = Owner.GetParent(element) ?? ObservedParent`. Adapters return a live
   parent when the framework exposes one; the observed parent is the fallback
   when the holder is internal to the framework.
3. Re-enumerate the parent's children, bounded by `MaxNodes`, and require this
   exact object among them.
4. Repeat from the parent, depth at most 256. When the chain reaches a
   `Control`, apply the existing `IsInScope` (disposal, exclusions, form scope).

Consequences: a removed, disposed or re-created element, or one whose strip or
form is gone, fails with `stale_target`; a moved element keeps its id; a
replacement with the same `Name` gets a different id. Re-adding the same object
restores its id, as for controls.

## Public seam

Namespace `WinFormsMcp.Instrumented.Elements`, in the bridge assembly (net48).
An abstract class, not an interface: net48 interfaces cannot gain members
without breaking every adapter already written.

```csharp
public abstract class ElementAdapter
{
    protected ElementAdapter(string name);      // ^[a-z0-9._-]{1,48}\z; advertised in hello; "toolstrip" is reserved
    public string Name { get; }

    // parent is any visited Control (return quickly if not applicable) or an
    // element this adapter emitted.
    public abstract void GetChildren(object parent, ElementSink sink);

    // Elements not reachable from any Control; their tree parent is the window.
    public virtual void GetWindowRoots(Form window, ElementSink sink) { }

    public abstract bool IsAlive(object element);
    public virtual object? GetParent(object element) => null;   // null = observed parent
    public abstract void Describe(object element, ElementDescription description);
    public virtual ElementActionOutcome Execute(object element, string action)
        => throw new ElementActionException(ElementActionError.Unsupported, "...");
}

public sealed class ElementSink { public bool Add(object element); public int Remaining { get; } }

public sealed class ElementDescription
{
    // Role (^[a-z_]{1,32}\z), Name, Text, Shortcut, Enabled, Available, Visible,
    // Checked?, Opened?, ChildCount?, ScreenBounds?, HostedControl?
    public void AddCapability(string action);          // ^[a-z_]{1,32}\z; click, open and close are such names
    public void AddProperty(string name, string? value); // at most 16; the 17th call throws, so Describe fails (adapter_failed node)
}

public enum ElementActionOutcome { HandlerReturned, HandlerDeferred }
public enum ElementActionError { Unsupported, Disabled, Hidden, NotRealized }
public sealed class ElementActionException : Exception { /* ElementActionError */ }
```

Registration: `McpBridgeBuilder.AddElementAdapter(ElementAdapter)`. Application
adapters are consulted in registration order; the built-in `ToolStripAdapter`
(name `toolstrip`) is always last. A duplicate name, an invalid name, or the
reserved name `toolstrip` is rejected with an `ArgumentException`. The `hello`
result lists the application adapters in registration order followed by
`"toolstrip"`, so the built-in adapter is advertised like any other.

### What an adapter may emit as an element

- **Control-emission rule.** An adapter may not emit a `Form`, and may not emit a
  `Control` whose `Parent` is not null: such controls are already control nodes
  reached through `Controls`, and a second identity for the same object would
  break the guarantee that one object has one id. The bridge drops them, counts
  them against the parent's budget and records an `adapter_failed` diagnostic. A
  hosted control is reported through `ElementDescription.HostedControl`, never as
  an element. An unparented control, such as a `ContextMenuStrip`, may be an
  element.
- **The registry classifies by owner, not by type.** An object is an element when
  the registry entry created for it has an owning adapter, whatever its CLR type.
  So the same `ContextMenuStrip` object that is a `Control` in the framework is an
  `item` node here, and control-only operations on it answer
  `unsupported_operation`.
- **Context menu strips.** The built-in adapter reports `Opened` (the strip's
  `Visible`) and `ChildCount` (its item count) for a `ContextMenuStrip`. A closed
  context menu therefore collapses like a closed dropdown in a window- or
  control-rooted tree (`childrenOmitted: true`, `childCount`), and its items are
  read with a tree rooted at the strip.
- **Selector resolution stays control-only** and `Windows()` lists forms only;
  neither returns elements.
- **Role and capability strings** are validated by the bridge with
  `^[a-z_]{1,32}\z` (no digits; trailing newline rejected). A string that does not
  match is treated as an adapter failure for that element, not silently fixed.

### Contract

- Every member runs only on the registered UI thread.
- `Visible` means realized on screen now; `Available` means configured to show.
  `Enabled` and `Visible` should be effective values (include the holder's
  state).
- Adapters never create window handles (check `IsHandleCreated`) and never
  create dropdowns lazily.
- The bridge fills `type` and `shortType` itself, validates role and capability
  strings, and truncates all adapter text with `LimitText`.
- **Budget.** `ElementSink.Add` returns false once the budget is spent
  (`MaxNodes`, depth, 1,000 children per parent). Later adds are dropped and the
  response is `truncated`, so an adapter that ignores the return value cannot
  exceed limits. A membership check uses a sink that stops when the target is
  found.
- **Fault isolation.** An adapter exception in `Describe` or `GetChildren`
  skips that subtree, sets `childrenError: "adapter_failed"` on the node and
  records a diagnostic. It does not fail the response.
- **Prechecks before `Execute`**, from a fresh `Describe`: action not declared
  → `unsupported_operation`; `!Enabled` → `disabled_control`; `!Available` →
  `hidden_control`; `open` or `close` while `!Visible` → `not_realized`.
- `AddAction` reserves the names `open` and `close`. This is a breaking builder
  change for consumers of `0.1.0-alpha.x` (it arrived with `0.1.0-alpha.3`, which is published)
  who registered custom actions named `open` or `close`. That is acceptable for a
  0.x alpha line, but it must be called out in the release notes and the README.

### Discovering elements outside the control tree

- A `ContextMenuStrip` is reached through `Control.ContextMenuStrip` of any
  visited control, open or closed. A shared one appears once, under the first
  control that references it.
- Owned dropdowns appear as `opened: true` on their owner item.
- Adapters may add component-owned roots (for example popup menus created by a
  designer) in `GetWindowRoots`.
- **Not discovered in the first version:** a popup shown from code and never
  assigned to anything, such as `ContextMenuStrip.Show(point)` on an unassigned
  instance. A later bridge-level thread-window scan offered to adapters can
  cover it. This limitation must be documented where tools are described.

## Wire and tool impact

- Control nodes gain `kind: "control"`. Element nodes carry `kind: "item"`,
  `adapter`, `role`, `name`, `text`, `type`, `shortType`, `enabled`,
  `available`, `visible`, `checked`, `shortcut`, `opened`, `childCount`,
  `childrenOmitted`, `bounds` (screen coordinates, null when not realized),
  `hostedControlId`, `childrenOf`, `properties`, `capabilities`,
  `childrenError`.
- `hello` adds `elementAdapters: [names]` (application adapters, then
  `"toolstrip"`); its `methods` list is unchanged.
- The protocol stays at version 1: receivers ignore unknown optional fields and
  the sidecar passes results through. By design, an old sidecar with a new bridge
  works, and a new sidecar with an old bridge sees no element nodes and gets
  `unsupported_operation` for `open`; neither mixed pairing was exercised, only
  matching versions.
- **Tools.** The count stays 19. `invoke_control` accepts `open` and `close` in
  `action`; only descriptions of `control_tree`, `inspect_control` and
  `invoke_control` change.
- **Targeting elements.** `inspect_control` returns the description. Subitems are
  read with `control_tree` rooted at the element. Selectors stay control-only:
  extending them would make some unique selectors ambiguous. Control-only
  operations (`set_text`, `set_value`, `set_checked`, `select`, `scroll`,
  `ensure_visible`, `read_grid`, `capture`) go through `Resolve`, which runs the stale check first and then returns `unsupported_operation` for
  an element target. Capture by element bounds is a later phase.
- **Receipts.** Every element action goes through `QueueAction`, because a
  `DropDownOpening` or click handler can open a modal dialog. A click result is
  `{operation, action, handlerReturned: true, handlerDeferred}`;
  `handlerDeferred` is true when the framework postponed the handler. An open or
  close result also reports the post-state `opened`, read again after the call,
  and does not claim it. A click on an item that has a dropdown reports `opened`
  the same way.
- **Semantics of click.** A click on an item inside a closed menu is a semantic
  `PerformClick` that requires only `enabled` and `available`, like
  `Button.PerformClick`. The tool description must say that a handler can
  assume an open menu. Items in a context menu are clicked with
  `Control.SourceControl` unset and without the `Opening` event (the strip was never
  shown), so handlers that remember a target in `Opening` or `MouseDown` can act on
  stale state; the tool description says so too.
- **Errors.** Reused: `stale_target`, `unsupported_operation`,
  `disabled_control`, `hidden_control`, `operation_failed`. New, additive:
  `not_realized` ("no on-screen layout; open the parent menu or show the window
  first").
- **Limits.** Elements count toward `MaxNodes`, `MaxDepth` and the response
  budget. Other caps: 1,000 children per parent, membership scans bounded by
  `MaxNodes`, scope recursion at most 256, 16 properties per node.
- **Default tree size.** Children of a closed dropdown are omitted in trees
  rooted at a window or control; the item reports `childrenOmitted: true` and its
  `childCount`. A tree rooted at the item expands its subtree.
- **Sensitive text.** A `ToolStripControlHost` item's `Text` returns the hosted
  control's text. The built-in adapter reports `text = null` for host items. A
  test with a password text box hosted in a tool strip asserts that the secret
  appears in no tree or inspect output.

## Resolved details

These were settled during design review and are binding on the plan.

- **Selectors are resolved against a control-only walk.** `Resolve` must not walk
  elements: they are neither emitted nor counted toward `MaxNodes` for selector
  resolution. Otherwise an application with large menus could hit `scope_limit`
  for every selector, a regression for the existing control workflow.
- **Tree rooted at an element.** The subtree is fully expanded, including nested
  closed dropdowns, within `MaxDepth`/`MaxNodes`. `maxDepth` counts from the
  root element. The root node's `parentId` is the element's real parent id (null
  only when it has none), so the agent can navigate upward.
- **`handlerDeferred`.** The built-in `ToolStripAdapter` always returns
  `HandlerReturned`. `HandlerDeferred` exists for third-party adapters whose
  framework can postpone a handler.
- **Element `bounds`.** For a `ToolStripItem`, only when its owner has a handle
  and the item is `Visible`: item bounds converted to screen coordinates through
  the owner. Otherwise null. No handle is created to compute them.
- **Post-state of `open`/`close`.** Read synchronously right after the call
  returns, inside the same queued action. It is the state at that moment; it does
  not promise the menu is still open afterwards.
- **`open` in the background is best-effort.** A Windows spike (see "Measured
  results and open questions") showed that a dropdown does not remain visible
  while its form is not the active window. `open` and `close` therefore ship and
  report the observed state, never the requested one. The bridge does not
  activate or focus the window to make a menu stay open, because this project
  does not change foreground state implicitly (see
  [controls](04-controls-and-capture.md)); activation would be a separately named
  capability.
- **Excluding individual elements: not supported (decided).** The existing
  control exclusion predicate is unchanged. An element is hidden only when an
  ancestor control is excluded (the stale chain reaches that control and applies
  `IsInScope`); a control that is itself an element, such as a context menu strip,
  is hidden with its items when the predicate excludes it, and a predicate that
  throws on it hides it. An application cannot hide one menu command from the agent; this
  must be stated in the integration and tool documentation, and an application
  that must not expose a command should not instrument that window. A per-element
  predicate can be added later without breaking this design.

## Third-party control suites

The public repository ships the seam, the `ToolStripAdapter`, documentation and
tests, including a fake third-party adapter. It contains no code, names or
defaults for any specific suite.

How an application adds support for a suite whose assemblies it already
references:

| Option | Verdict |
| --- | --- |
| Binary package referencing the suite | Rejected: the suite's assembly names embed its version, the package needs rebuilding for each release, and public CI cannot build against licensed assemblies |
| Application writes its own adapter against the seam | Supported; nothing to package, but each application repeats subtle code |
| **Source-only package** whose `.cs` files compile inside the application | Recommended delivery once an adapter is stable |
| Declarative reflection mapping (type name → member names) | Rejected, see below |

A source-only adapter package (NuGet `contentFiles`, `buildAction=Compile`)
compiles against whatever suite version the application already references, so
API drift is a compile error rather than a runtime `MissingMethodException`.
Requirements: all types `internal` in a distinct namespace; an
`// <auto-generated/>` header so analyzers and warnings-as-errors do not fire in
the application; C# 7.3-compatible syntax; a dependency on the bridge version
that introduced the seam. The application enables it with one builder call in
its existing guarded startup, and its conditional package reference is stripped
with the rest of the instrumentation.

**Sequencing.** Incubate each suite adapter inside the consuming application
first, as a normal instrumentation-only source file. Extract it to a source-only
package once it is stable. The extracted package and its tests live outside this
repository.

### Rejected alternative: declarative reflection mapping

Named members declared by the application developer have the same trust level as
`AddAction` and could be bounded (parameterless getters returning primitives,
named parameterless methods, no setters, no member paths, resolved and validated
at startup). It still fails cost/benefit: real suite semantics need logic a
mapping cannot express (choosing a method overload, mapping an empty rectangle to
null, translating a configured visibility into `Available`, tracking an internal
holder). Expressing that pushes the mapping toward an interpreter, the
evaluation surface CLAUDE.md forbids, and replaces compile-time errors with
runtime strings.

## Grid readiness

> **Update.** The grid work reserved below has since been designed and
> implemented as a separate seam: see [grid adapters](10-grid-adapters.md) and
> [ADR 0005](adr/0005-grid-adapters.md). The text of this section is the
> readiness reasoning as written for the element work, kept for the record; the
> rule that rows are never tree nodes still stands, and the `GridAdapter` class,
> `viewToken`, and the wire fields it needed now exist.

Needed now, and already part of this design: the object-keyed registry with
adapter ownership; container nodes that are not controls (a view or column may
later be a node with role `view` or `column` under the grid control); adapter-
declared capabilities; and `Resolve` as the single routing point a grid
handler can hook.

**Rule to write down now: rows are never tree nodes and never `GetChildren`
results.** A row handle is a position in a sorted, filtered, grouped view, not a
stable key.

Later, not now: a separate `GridAdapter` base class with a paged
`ReadPage(control, rows, columns)` returning a `viewToken` (a freshness counter
driven by data-source, row-count, filter, sort and group events), and
`select`/`scroll` taking `(row, column, viewToken)` and failing with a stale-view
error when the token changed. Do not build row or cell identities, token
plumbing, that class, or any wire fields for them in this work.

## Testing

Public repository (`tests/WinFormsMcp.Instrumented.Tests`, net48, Windows, real
message loop):

- **Tree shape:** `MenuStrip`, `ToolStrip` and `StatusStrip` items with their
  `parentId` chain; closed submenu items listed (`visible: false`,
  `available: true`) and collapsed by default.
- **Click:** a nested closed item runs its handler; a disabled item gives
  `disabled_control`; `Available = false` gives `hidden_control`.
- **Open/close:** observed through the tree; open of a nested item while its
  parent is closed gives `not_realized`.
- **Modal:** an item that opens a modal dialog leaves its receipt `started`
  while the dialog is inspectable and closable, then `completed`.
- **Identity:** removed → `stale_target`; re-added or moved → same id; disposed
  item, strip or form → `stale_target`; same-`Name` replacement → new id; a
  queued click whose item is removed before the action starts →
  `stale_target` and the handler never runs.
- **Context menus and hosts:** a `ContextMenuStrip` reached through a control
  appears once and its items are clickable; a hosted control stays a control
  node; a password text box hosted in a strip does not leak.
- **Limits and exclusion:** truncation with 2,500 items, with exactly 1,000
  children under the strip; an excluded parent makes its items absent and their
  ids stale; an excluded context menu strip (and one hidden by a throwing
  predicate) disappears with its items; control-only operations on an element give
  `unsupported_operation`.
- **Fake third-party adapter, public API only (no `InternalsVisibleTo`):** a host
  control, `Component` commands, non-`Component` placements whose command
  becomes null on dispose, one command placed twice, and `GetParent` returning
  null. It proves registration order, duplicate removal, `childrenOf`, budget
  enforcement against an adapter that ignores `Add() == false`, fault isolation,
  rejection of invalid role or capability strings and the `HandlerDeferred`
  flag.
- Server tests: still 19 tools, description text pinned. Demo and
  `eng/exercise-demo.py`: open a menu, click a nested item, a disabled item is
  refused, open a context-menu item.

Third-party adapter tests live with the adapter, against licensed assemblies,
mirroring the fake-adapter contract list. This repository's stripped-build verifier
does not assert that no adapter types are present (see
[integration](05-integration-and-stripping.md)); an application that ships an
adapter should add this check for its own types, because compiled-in source does
not appear as an assembly reference.

## Measured results and open questions

Written on 2026-11-13, after implementation. The first group was measured or
established by execution; the second group is still unverified and is not
promised by the tools.

### Measured

- **Opening a menu while the window is not active.** Plain WinForms, no bridge,
  Windows 11 interactive desktop, .NET Framework 4.8. For each condition the
  spike called `ShowDropDown()`, pumped messages for 500 ms and then read
  `DropDown.Visible` and `DropDownItems[0].Visible`. Each condition was measured
  once.

  ```text
  a-form-active: dropDownVisible=True firstSubitemVisible=True ownProcessForeground=True mainFormActive=True
  b-second-form-active: dropDownVisible=False firstSubitemVisible=False ownProcessForeground=True mainFormActive=False
  c-other-process-foreground: dropDownVisible=False firstSubitemVisible=False ownProcessForeground=False mainFormActive=False
  ```

  Result: after `ShowDropDown()` and 500 ms of pumping, the dropdown was visible
  when the form was the active window (a) and was not visible when another form
  of the same process was active (b) or when another process owned the
  foreground (c). The spike cannot distinguish a dropdown that never opened from
  one that opened and was dismissed, so it does not say which of the two
  happened; the framework behavior behind it is not established. Therefore
  **`open` is best-effort**: it can only be expected to hold while the target
  window is active, it always reports the observed post-state `opened` (possibly
  `false` although the call did not fail), and the sidecar tool text says so. The
  bridge does not activate the window.

  Not measured: `close` in the same conditions; a menu opened while active and
  deactivated afterwards; `ToolStripDropDownButton`; context menus; more than one
  run per condition.

  Reproduction: show a `Form` with a `MenuStrip` holding one `ToolStripMenuItem`
  with one subitem, call `item.ShowDropDown()`, pump messages for 500 ms and read
  `item.DropDown.Visible`. For (b), call `Activate()` on a second `Form` of the
  same process first. For (c), bring a separately started classic Win32 process
  (for example `charmap.exe`, killed afterwards) to the foreground. A plain
  `SetForegroundWindow` is not enough for (c): Windows' foreground lock refuses it
  from a background process, and the measurement silently turns into condition (b)
  again. The spike sent a synthetic Alt key press and then called
  `SetForegroundWindow` on the foreign window. Verify that (c) was reached by
  comparing the process id of the foreground window (`GetForegroundWindow`,
  `GetWindowThreadProcessId`) with the test process id, as the
  `ownProcessForeground` field above does; a `False` there is the evidence that
  the condition was real. `notepad.exe` is not usable for (c) on current Windows
  11: the packaged Notepad launcher exits at once and hands over to an existing
  instance.
- **`open` with the window active, through the bridge.** An integration test
  activates the form first (taking the foreground if needed; it reports
  inconclusive, not passed, when activation fails), then asserts that the `open`
  receipt and a fresh `inspect` agree on `opened: true`, that the children of the
  open menu are listed, and the same for `close` with `opened: false`. The desktop
  exercise records what `open` observed (`opened: true` in the run of
  2026-11-13) and asserts only that the receipt equals a fresh `inspect`. The
  inactive-window `open` case has not been executed through the bridge or in the
  desktop exercise.
- **`ToolStripDropDownItem.HasDropDown` is public on .NET Framework 4.8.** It
  compiles against the net48 reference assemblies, so enumeration of children does
  not need to create a dropdown lazily (the adapter reads `DropDownItems` only
  when `HasDropDown` is true).
- **Disabled and hidden items are refused by the bridge itself.** The bridge
  prechecks `Enabled` and `Available` from a fresh `Describe` before calling the
  adapter, and tests assert `disabled_control` and `hidden_control`. This does not
  depend on what `ToolStripItem.PerformClick` does with a disabled item, which was
  not measured.

### Still unverified

- Whether `ToolStripItem.PerformClick` itself checks `Enabled` and `Available`
  (not needed for the bridge's refusals, see above).
- For a given third-party suite: whether its open call is non-blocking, which
  members report checked or pressed state, whether layout restore re-creates
  elements, and whether components can be reached from a designer container. The
  public repository contains no suite adapter; this is checked by the application
  that writes one.
- The behaviors listed under "not measured" in the spike above.

## Risks

- Element ids go stale more often than control ids. Failing explicitly is
  correct.
- A semantic click inside a closed menu can run handler code that assumes the
  menu is open.
- Third-party suites can show their internal controls as ordinary control nodes
  next to the element nodes; documentation must say so.
- `ElementAdapter` is public API and must stay source- and binary-stable.

The known limitations of the implemented version are listed in
[implementation status](07-implementation-status.md#current-limitations).

## Work order

All steps are done; the result and what was executed are in
[implementation status](07-implementation-status.md).

1. This document, ADR 0004, and updates to docs 02, 03, 04, 05, 07 and 08.
2. Generalize the registry with no behavior change; the existing 19 net48 tests
   must still pass.
3. Add the seam and `AddElementAdapter`.
4. Generalize `Walk`, `Resolve`, `IsInScope`, `Inspect` and `Mutate`.
5. Write `ToolStripAdapter`.
6. Update sidecar tool descriptions.
7. Add demo content and tests.
8. Run `./eng/build.ps1 -DesktopExercise`; update `07-implementation-status.md`.
