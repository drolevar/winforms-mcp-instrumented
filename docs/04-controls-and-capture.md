# Controls, dynamic discovery, and screenshots

**Status:** target behavior and implementation guidance. Support must be reported
per adapter and capture mode. The current feature status identifies which parts
of this design the draft actually implements and which still need Windows tests.

## Why the bridge observes managed objects

WinForms applications already have a meaningful object model: buttons know how
to perform their action, text boxes expose editable text, grids know about rows,
and forms own their children. An in-process bridge can use those semantics and
report app-specific state without making accessibility-tree quality a
prerequisite. This is the reason for the word **Instrumented** in the project
name.

The integration should therefore discover standard controls automatically. The
builder exists for exclusions, limits, unusual controls, and optional domain
operations. It must not require registration of every button.

## Discovery follows the live UI

At request time, discover the currently open managed forms on the registered UI
thread and walk their child collections. Materialize plain snapshots before
leaving that thread. Returning a snapshot must not expose a live `Control`, an
iterator that later reads controls on another thread, or a serializer that walks
application objects outside the dispatch boundary.

Bound every traversal by node count, depth, and text length. Return `truncated`
and the applied limits so an agent knows whether an absent control is evidence
of absence or merely outside the returned slice.

For a small first implementation, on-demand traversal is sufficient to discover
controls added after startup. Lifecycle subscriptions and bounded periodic
reconciliation are an optimization for a later cached implementation. A cached
tree must never become the only authority: custom controls can change their
children while the bridge is disconnected or during nested message loops.

If caching is introduced, `ControlAdded`, `ControlRemoved`, `ParentChanged`,
`Disposed`, `HandleCreated`, and `HandleDestroyed` are useful invalidation signals.
Subscribe recursively to newly discovered objects, unsubscribe when tracking is
no longer required, and periodically reconcile against the current roots. Keep
event handlers cheap; do not serialize entire subtrees inside an application
event callback.

### Identity is independent of the native window handle

| UI change | Identity rule |
| --- | --- |
| New control object is added | Issue a new instance ID. |
| Existing control is moved to another parent | Preserve instance ID; update parent/bounds. |
| Existing control recreates its HWND | Preserve instance ID; refresh handle metadata. |
| Control is removed but remains alive | ID remains associated with that object; targeting requires it to be in an exposed root. |
| Control is disposed | Reject its ID as stale. |
| Another object replaces it with the same `Name` | Issue a different ID; never retarget the old one. |
| Application restarts | Issue a new generation; all old IDs are stale. |

Use object-reference identity with weak references, or an equivalent weak-key
registry. Ordinary equality, control names, child indices, and HWND values are
not sufficient primary keys. Registry maintenance must not keep closed forms
alive indefinitely.

Microsoft documents that a control can still be valid during `HandleDestroyed`
and can recreate its handle. This is why destroying a handle must not be treated
as destroying the managed control. [Control.HandleDestroyed](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.handledestroyed)

Reading `Handle` can trigger creation. An inspection should first test
`IsHandleCreated` and omit native-handle metadata when no handle exists. It must
not create a hidden handle merely to assign an identity.

## Thread ownership is part of correctness

All property access and enumeration use the owning UI thread. `InvokeRequired ==
false` is not proof that an arbitrary background thread may access a control:
before an appropriate handle exists it can return false even for the wrong
thread. Microsoft documents that this can accidentally create a handle on a
thread without a message pump. [Control.InvokeRequired](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invokerequired)

Start from the WinForms entry thread and establish a dispatcher deliberately.
Request handling should use that dispatcher, with bounded observation timeouts.
Do not manufacture a control's handle on the pipe thread to make `BeginInvoke`
work. Do not run `Application.DoEvents()` as a synchronization substitute.

A single UI thread is an acceptable explicitly documented starting scope.
Supporting multiple UI threads requires discovery and dispatch per thread; the
bridge must not quietly send every form through the first form's dispatcher.

## Capabilities come from adapters

An adapter matches a supported type and reports its actual supported operations.
Specific adapters take precedence over a generic `Control` adapter. Ordinary
properties such as bounds, name, enabled state, and permitted text are available
without inventing an action for every class.

| Control family | Intended standard behavior |
| --- | --- |
| `Button` / `IButtonControl` | Semantic invocation when actionable. |
| `TextBoxBase` | Read/set editable text; honor read-only state. |
| `CheckBox`, `RadioButton` | Read/set checked state. |
| `ComboBox`, `ListBox` | Bounded item reads and explicit selection. |
| `NumericUpDown`, `DateTimePicker` | Typed value reads/writes with range validation. |
| `TabControl` | Select a page by identity, name, or bounded index. |
| `ScrollableControl` | Scroll the viewport and reveal a descendant. |
| `DataGridView` | Paged visible/model row reads, selection, supported scrolling. |
| `TreeView`, `ListView` | Adapter-defined item identities, expansion/selection/visibility. |
| `ToolStrip` items | Adapter-defined logical children and invocation. |
| Owner-drawn / third-party controls | General inspection plus explicit custom adapter. |

This table is a target matrix, not a promise that all entries are implemented in
the first draft. An unsupported operation returns an explicit error. Reflection
must not be used to call arbitrary private methods, set arbitrary properties, or
expose entire data-binding objects as a generic escape hatch.

Not every UI element is a `Control`. A `ToolStripItem`, grid cell, list item, or
tree node needs an adapter-owned logical identity. A row index is a temporary
position in a potentially sorted or virtualized view, not a durable row key.
Start with honest bounded support; add adapter-specific freshness tokens or
stable keys before promising robust targeting of virtual items.

### Semantic actions and physical input are different capabilities

Calling `Button.PerformClick()` or changing `TextBox.Text` uses WinForms behavior
directly. It does not reproduce the complete sequence of mouse, keyboard, focus,
and input-method events that a person would generate. Tools should describe that
behavior explicitly.

Physical input injection, if later provided, must be a separately named
capability with desktop/focus requirements. The implementation must not silently
switch from semantic invocation to coordinate clicking when a control adapter
is missing. The agent needs to know which behavior was exercised.

All mutations use the receipt mechanism in [the protocol](03-bridge-protocol.md).
Even assigning text can run application event handlers, so a nominally simple
setter can open a dialog or throw. Validation happens again on the UI thread
when the scheduled action starts.

## Screenshot scopes

The API should let an agent request:

- A whole top-level window, with an explicit client-area versus window-frame
  choice where supported.
- A control's current bounds.
- A rectangle relative to a specified window or control.
- The application as a set of separately labeled window captures.

An application can have several windows on different monitors. Returning one
image per window preserves their identities and avoids implying that a large
desktop bounding rectangle contains only the app. If a later compositor creates
one collage, it must label that output as a collage and retain the source
geometry.

For regions, use a declared coordinate system and validate positive dimensions
and limits before allocating an image. Re-resolve bounds immediately before
capture. Return the requested rectangle, actual captured rectangle, image size,
mode, capture time, and available DPI/coordinate metadata. Do not convert an old
tree snapshot's bounds into a new screenshot without checking the target again.

## Capture modes must describe their limitations

### `screen_copy`: visible desktop pixels (implemented)

`Graphics.CopyFromScreen` copies a screen rectangle to a bitmap. Consequently it
captures what is currently displayed in that rectangle, which can include an
overlapping window. It does not by itself recover hidden content or a minimized
window. [Graphics.CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen)

Use this mode when the caller wants evidence of visible pixels. Reject minimized
or otherwise unsuitable targets; report clipping and capture-mode limitations.
Do not change foreground focus merely to satisfy a screenshot request unless
the request explicitly asks for that behavior.

The interactive desktop, monitor geometry, DPI awareness, and screen-capture
permissions are part of the test environment. Geometry must remain internally
consistent; otherwise a correct control ID can still produce the wrong crop.

### Control rendering to a bitmap (future extension)

`Control.DrawToBitmap` requests rendering through the control's print path. It
can be useful for standard controls, but it is not a universal screenshot
implementation. Microsoft lists limitations including ActiveX support,
RichTextBox content, invisible child text boxes, rendering order, and large
bitmap behavior. [Control.DrawToBitmap](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.drawtobitmap)

If this backend is added, return an explicit render-mode label and document its
limitations. Rendering may differ from
the pixels currently visible on screen. Do not retry a failed screen capture
with this mode and label the result as an equivalent screen capture.

### Future capture backends

Windows Graphics Capture or an explicit native-window capture backend can be
added after implementation and testing on supported Windows versions. The
sidecar boundary makes that possible without forcing modern capture APIs into
the .NET Framework bridge. No backend should advertise universal capture of
every minimized, protected, GPU-rendered, embedded, or off-desktop surface.

## Exclusions and data handling

The builder can exclude controls or redact text. Excluded controls should not be
targetable through a stale ID, selector, or a parent's inspection result.
Password-like text should be omitted from normal inspection by default.

Screenshots require separate handling: suppressing a text property does not
remove its pixels. A later masking feature must mask the captured geometry, not
merely redact the JSON tree. Until implemented, documentation must state that
capture can include visible sensitive content. Neither screenshot bytes nor
session tokens should be written to an unbounded diagnostic log.

## Required Windows scenarios

The sample and integration tests should cover dynamic creation, replacement with
the same `Name`, reparenting, disposal, handle recreation, delayed first-window
creation, multiple forms, and modal dialogs. Capture checks should include a
control region, multiple windows, a partially obscured window, minimized-window
rejection, and DPI changes where supported.

Tests should verify observable behavior: a fresh tree finds new controls, stale
targets fail, replacement targets differ, a modal dialog remains inspectable,
and an image's metadata describes what was captured. A successful compile on
Linux cannot establish any of those Windows UI properties.
