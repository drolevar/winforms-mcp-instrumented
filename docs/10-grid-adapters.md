# Grid adapters: reading, selecting and editing third-party grids

**Status:** implemented; not yet released or published; validation is
recorded in [implementation status](07-implementation-status.md#grid-adapters-2026-11-17),
including what was not run. Decision record:
[ADR 0005](adr/0005-grid-adapters.md). Related:
[element adapters](09-element-adapters.md), whose "Grid readiness" section
reserved this work. Approved by the maintainer on 2026-11-17; refused edits are
failed receipts and the tool is `set_grid_cell` (maintainer's decisions); the
other choices are the author's calls, listed at the end. The decisions below
are kept as designed. Where the implementation turned out stricter or more
precise than the first wording, the text now says what the code does (the
adapter-exception rule, the order of checks, the rejection details).

## Problem

`read_grid` understands only `System.Windows.Forms.DataGridView`.
`select_control` and `scroll_control` switch on the same type. Real
applications use third-party grid controls: they are `Control`s, so the bridge
already lists them, but an agent cannot read a cell, select a row or change a
value. Grids are the main data surface of many line-of-business applications,
so without them the bridge cannot verify or drive most screens.

## Goals

An agent can, on a grid whose control suite the application has an adapter for:

- **read** cell text, column metadata (header, field, editable, value kind,
  band path) and the current focus/selection, in bounded pages;
- **select** a row or a cell and bring it into view;
- **edit** a cell the way a user does (through the editor, with the
  application's own validation), and learn whether the edit was committed or
  why it was refused.

The public package stays vendor-neutral and the vendor adapter lives outside
it, as for element adapters. The `DataGridView` behavior does not regress.

## Non-goals and anti-goals

- No tree lists, vertical/property grids, card or layout views, grouping,
  master-detail, virtual or server modes (an adapter answers `Unsupported`);
  no new-item row, no adding or deleting rows; no multi-range selection; no
  cell appearance or error icons; no `set_cell` on `DataGridView` in this
  work (a later built-in adapter can add it).
- No data-level writes (the maintainer decided: edits go through the editor).
- No reflection, evaluation or member names on the wire (CLAUDE.md).
- Rows are never tree nodes (doc 09). A row number is a position in the
  current view, never a durable key.
- Anti-goal: an edit or selection that lands on a different row than the agent
  read because the view re-sorted in between.

## Constraints

Everything in [CLAUDE.md](../CLAUDE.md) applies. The bridge targets .NET
Framework 4.8; all adapter members run on the UI thread; results are bounded
and say when they are truncated; mutations use receipts and are validated
again when they start; a UI action that can open a modal dialog is never
waited for synchronously; the public seam is permanent API, so the abstract
member set stays small and new members are `virtual`.

## Approaches considered

1. **A grid seam in the bridge plus a vendor adapter in the application
   (chosen).** The bridge owns loops, bounds, tokens and receipts; an abstract
   class answers questions about one column or cell at a time.
2. Application-only custom actions through `AddAction`. Rejected: callbacks
   return nothing, so data cannot come back.
3. Screen capture or UI Automation reading. Rejected: weak data, no editing,
   and the project does not depend on accessibility quality.

## Decisions

- **Seam:** public abstract class `WinFormsMcp.Instrumented.Grids.GridAdapter`,
  registered with `McpBridgeBuilder.AddGridAdapter`. The existing
  `DataGridView` code stays and is advertised as the reserved adapter name
  `datagridview`.
- **Rows** are positions `0..RowCount-1` in the current view, data rows only.
  **Columns** are indexes in the adapter's column collection order (not visual
  order).
- **Freshness:** the bridge owns a per-grid state (salt + counter + last
  fingerprint). A mutation or select must carry the `viewToken` from the read
  it is based on; a mismatch fails with `stale_view`.
- **One new tool, `set_grid_cell`** (bridge method `set_cell`), a queued
  mutation. A completed receipt always means the edit was committed; a refused
  edit is a failed receipt with `edit_rejected`.
- **Context menus:** only an additive `AddAction` overload with a predicate. No
  built-in action and no `open` on `context_menu` elements.
- Protocol stays at version 1. Tool count goes from 19 to 20.

## The public seam (net48)

```csharp
namespace WinFormsMcp.Instrumented.Grids;

public abstract class GridAdapter
{
    protected GridAdapter(string name);              // ^[a-z0-9._-]{1,48}\z; "datagridview" reserved
    public string Name { get; }
    public abstract bool Accepts(Control control);   // called once per instance and cached
    public virtual void Attach(Control grid, GridViewSignal signal) { }
    public abstract GridFeatures GetFeatures(Control grid);
    public abstract GridViewInfo GetViewInfo(Control grid);   // throw Unsupported for grouped/virtual views
    public abstract void DescribeColumn(Control grid, int column, GridColumnDescription d);
    public abstract string? GetCellText(Control grid, int row, int column);
    public virtual bool? IsRowSelected(Control grid, int row) => null;
    public virtual bool TryGetFocusedCell(Control grid, out int row, out int column);
    public virtual void Select(Control grid, int row, int? column);      // default: Unsupported
    public virtual void Scroll(Control grid, int? row, int? column);     // default: Unsupported
    public virtual GridEditResult SetCell(Control grid, GridEditRequest request);  // default: Unsupported
}
[Flags] public enum GridFeatures { None = 0, Select = 1, Scroll = 2, Edit = 4 }  // read is implied
public sealed class GridViewInfo { public GridViewInfo(int rowCount, int columnCount, long fingerprint); }
public sealed class GridViewSignal { public void Changed(); }
public sealed class GridColumnDescription { /* Name, Field, Header, Visible, Editable, Redacted,
                                               ValueKind, AddBand(caption), AddChoice(text) */ }
public enum GridValueKind { Text, Number, Boolean, Date, Time, Duration, Choice, Other }
public sealed class GridEditRequest { /* Row, Column, Text, bool ViewChanged { get; } (live check) */ }
public sealed class GridEditResult { /* Committed(displayText, rowAfter), Rejected(reason, errorText, displayText) */ }
public enum GridEditRejection { NotEditable, EditorNotShown, InvalidInput, AmbiguousInput,
                                ValidationFailed, RowNotCommitted, Other }
public enum GridOperationError { Unsupported, ReadOnly, Hidden, NotRealized, StaleView }
public sealed class GridOperationException : Exception { /* GridOperationError */ }
```

### Contract

- Every member runs on the registered UI thread. Read members must not create
  handles, show UI, pump messages or change focus or selection; `Select`,
  `Scroll` and `SetCell` may.
- The bridge owns the loops: it calls `DescribeColumn` and `GetCellText` only
  for clamped requested indexes, applies `LimitText`, and counts all text
  (cells, bands, choices) against the response budget.
- **Fault isolation:** `Accepts` throwing means "not accepted" (cached, one
  diagnostic). `Attach` or `GetFeatures` throwing gives the node
  `gridError: "adapter_failed"` and no grid capabilities; the tree carries on.
  A read member throwing fails that request with `adapter_failed`; a mutation
  throwing fails the receipt with `adapter_failed`. A `GridOperationException`
  maps to its own code (`Unsupported` to `unsupported_operation`, `ReadOnly` to
  `read_only`, `Hidden` to `hidden_control`, `NotRealized` to `not_realized`,
  `StaleView` to `stale_view` with reason `view_changed`).
  Diagnostics never contain cell or edit text.
- **Exception messages.** A data-bearing member (`GetCellText`, `IsRowSelected`,
  `TryGetFocusedCell`, `Select`, `Scroll`, `SetCell`) may throw a message that
  quotes a row, cell or edit value, because a data-bound adapter often builds
  its message from the data it was handling. The bridge therefore relays **no
  exception message** from these members, on the wire or in diagnostics: the
  error and the diagnostic name the adapter, the member and the exception type
  only, for a refusal (`GridOperationException`) as well. This is stricter than
  the first wording of this design ("exception type and a bounded message").
  The non-data members (`Accepts`, `Attach`, `GetFeatures`, `GetViewInfo`,
  `DescribeColumn`) keep a bounded message (200 characters, line breaks
  replaced) in the error they cause. An adapter author who needs the full
  exception logs it in the adapter. The diagnostic written when `Accepts`,
  `Attach` or `GetFeatures` throws is capped the same way (200 characters).
- Adapters must not keep grids in fields or statics.
- Application adapters are consulted in registration order, before the built-in
  `DataGridView` handling.

### Freshness token

`GridState` (salt, counter, last fingerprint) lives in a
`ConditionalWeakTable<Control, GridState>` owned by the bridge. The token is
`"<random salt>.<counter>"`. `GridViewSignal` points only at `GridState`, so the
chain grid, handler, signal, state is collected together. The counter goes up
when the adapter calls `signal.Changed()` (recommended events: data source, row
count, sort, filter, column collection) or when the fingerprint returned by
`GetViewInfo` differs from the stored one when checked (recommended: row count,
column count, data-source identity, sort/filter state hash). The check runs on
the UI thread at every `read_grid` and every grid mutation. `DataGridView`
gets a fingerprint-only token. Tree nodes carry no token.

## Wire and tool impact

- **`read_grid`** keeps every existing field (`dataKind`, `totalColumns`,
  `startRow`, `startColumn`, `columns[index,name,header,visible]`,
  `rows[index,visible,cells]`, `hasMoreRows`, `hasMoreColumns`) and the limits
  (rows 1..200, columns 1..50, 256K characters). Additive: top level
  `gridAdapter`, `viewToken`, `totalRows`, `focusedRow`, `focusedColumn`;
  per column `field`, `editable`, `valueKind`, `bandPath`, `redacted`,
  `choices`, `choicesTruncated`; per row `selected` (when the adapter can tell)
  and `truncated: [column indexes]` (when a cell was cut). Redacted cells are
  `null`, and a redacted column's cells are never requested from the adapter.
  For an adapter grid `dataKind` is the adapter's name and `rows[].visible` is
  `true`; `totalRowsIncludingNewRow` is still emitted and equals `totalRows`.
  A `DataGridView` read gains only `gridAdapter: "datagridview"` and
  `viewToken`; it has no `totalRows`.
- **`select_control` and `scroll_control`** get an optional `viewToken`. On an
  adapter grid `select` requires `row` and `viewToken` (`invalid_argument`
  otherwise) and `column` is optional; it focuses the cell, makes it the only
  selection and brings it into view. `scroll` means "bring into view" and
  checks the token if supplied. `DataGridView`: the token is optional and
  checked when supplied.
- **`set_grid_cell`** (bridge method `set_cell`, added to the mutation list):
  arguments `generation`, `id`, `row`, `column`, `text`, `viewToken`, optional
  `expectedText`. A completed receipt's result is `{operation:"set_cell",
  handlerReturned:true, committed:true, displayText, displayTextRedacted,
  rowAfter, viewToken}` (`viewToken` after the edit; `rowAfter` null when the
  row left the view). `displayText` is cut at `MaxTextLength`, and then
  `displayTextTruncated: true` is added. If the grid was disposed during the
  edit, or reading the new token failed after the commit, the receipt is still
  `completed` and `committed: true`, with `viewToken: null` (read the grid again).
  A refusal is a failed receipt `edit_rejected` with details `{reason,
  errorText, displayText}` (`reason` is the `GridEditRejection` name in snake
  case, for example `validation_failed`; `errorText` at most 1,024 characters;
  `displayText` cut at `MaxTextLength`). For a redacted column `errorText` and
  `displayText` are `null` and `displayTextRedacted: true` is added to the
  details. The failed receipt's `error` object carries `details` (an additive
  field; the sidecar surfaces it in `action_status`).
- **`text`** is typed as a user would type it: in the application's UI culture;
  for a choice column exactly one choice's display text; `""` clears where the
  editor allows null; for date, time and duration columns ISO 8601 is also
  accepted by the adapter the application writes (the bridge never converts the
  text).
- **Capabilities** on a grid node: `read_grid` always, `select`, `scroll` and
  `set_cell` per `GetFeatures`, plus `gridAdapter: "<name>"` (also
  `"datagridview"` for those). When an adapter handles a control, its features
  alone decide `select` and `scroll`: the built-in ones for a list box, list view,
  combo box, tab control, tree view or auto-scrolling panel are not advertised for
  that control, because select and scroll are routed to the adapter.
- **hello** adds `gridAdapters: [application adapters…, "datagridview"]`; its
  `methods` list gains `set_cell`.
- **Errors (additive):** `stale_view` (details `{reason: "view_changed" |
  "cell_changed"}`; the current token is deliberately not returned),
  `read_only`, `edit_rejected`, `adapter_failed`, `grid_busy`. Reused: `invalid_argument`,
  `unsupported_operation`, `hidden_control`, `disabled_control`,
  `not_realized`, `response_limit`, `stale_target`. `unsupported_operation`
  covers a `DataGridView` (it has no edit), a grid whose adapter lacks the
  feature, a control no adapter handles and an element target; `hidden_control`
  also covers a hidden grid; `adapter_failed` also covers a grid whose `Attach`
  or `GetFeatures` failed (the bridge refuses every operation on it, because
  without `Attach` its token would see fingerprint changes only).
- **Compatibility:** protocol stays 1 (additive). A new sidecar with an old
  bridge sees no `set_cell` in `hello.methods` and answers `set_grid_cell` with
  `unsupported_operation`. An old sidecar with a new bridge has no edit tool and
  cannot select on adapter grids (token required); documented, not tested.
- **Descriptions to update:** `read_grid`, `select_control`, `scroll_control`,
  the new `set_grid_cell`, `control_tree` (the `gridAdapter` field).

## Edit semantics

Checks run when the receipt starts, in this order: resolve the target and the
enabled/visible prechecks (`stale_target`, `disabled_control`, `hidden_control`
for a hidden grid); the grid-busy flag (`grid_busy`); the shape of the arguments
(`row`, `column`, `text`, `viewToken`, `expectedText` present and of the right
type, `invalid_argument`); the adapter has the `Edit` feature
(`unsupported_operation`); `text.Length <= MaxTextLength`
(`invalid_argument`); row and column in range (`invalid_argument`); the token
is current (`stale_view`); the column is visible (`hidden_control`) and
`Editable` (`read_only`; `SetCell` is not called in these two cases); if
`expectedText` is given it equals the current cell text (ordinal, else
`stale_view` / `cell_changed`; refused with `invalid_argument` and reason
`column_redacted` on redacted columns, whose text is never requested, and with
reason `cell_truncated` when the cell text is longer than `MaxTextLength`);
then `SetCell`; then, if the grid is not disposed, the token is read again for
the result. The argument shape is validated before the `Edit` feature check, so
a malformed request on a grid without `Edit` is `invalid_argument`, not
`unsupported_operation`. The bridge never retries, never writes at data level
and never converts text.

**Grid-busy flag.** `select`, `scroll` and `set_cell` on one grid are one at a
time: the flag is set when the mutation starts and cleared in a `finally`. The
flag is also taken for `select` and `scroll` on a `DataGridView` (a test covers
the adapter grid case only). A second mutation while the first is `started`
(for example inside a modal dialog) fails with `grid_busy`, before any
grid-specific check. Other grids are independent.

**Errors that are refusals, not failures of the grid.** `read_only`,
`hidden_control`, `stale_view`, `invalid_argument` and `unsupported_operation`
mean `SetCell` was not called. `edit_rejected` means the adapter was asked and
the application refused.

The adapter, when editing: focuses the cell; checks `request.ViewChanged`
(true: throw `StaleView`; the property reads the grid, so it is UI-thread only,
which is where `SetCell` runs); shows the editor; sets the editor value from the
text; posts and closes the editor; commits the row. Input is the text as a user
would type it, in the application's UI culture (boolean: `true`/`false` plus
the editor's own display strings; choice: exactly one choice's display text,
otherwise `AmbiguousInput` or `InvalidInput`; date, time, duration:
additionally ISO 8601 is recommended; `""` clears where the editor allows
null). For the duration of the call it hooks the grid's invalid-value and
invalid-row events into a no-modal mode, captures the error text and unhooks in
a `finally`. On rejection it closes the editor without posting and reports the
text now shown. `Committed` may be returned only after the row commit succeeded
and the value is confirmed as posted. After any modal it checks `IsDisposed`.

A direct `MessageBox`/`ShowDialog` in application code keeps the receipt
`started`, by design: the agent finds the dialog with `list_windows`, answers it
and polls; the final state is `completed` or `edit_rejected`.

Redacted columns: cells and `displayText` are `null` (`displayTextRedacted:
true`), `expectedText` is refused; edits are allowed (tests may type secrets) but
edit text is never logged or echoed, and `errorText` is `null` too, because the
application's message may quote the value. For other columns `errorText` is
limited to 1,024 characters and is untrusted application text.

**Hosted editors.** Many grids host their cell editor as a child control of the
grid, hidden until an edit starts, and some keep its text after hiding it. So that
a redacted column cannot surface through `control_tree` or `inspect`, the bridge
treats every descendant of a grid that an adapter handles as redacted whenever that
grid has at least one redacted column: the descendant has `text: null` and
`textRedacted: true` (the existing field; before, true only for password boxes),
no `value`, `checked` or `selectedIndex`, and `inspect` lists no items
(`itemsUnavailable: "redacted-grid"`). This also holds while a validation modal
keeps the receipt `started`. It fails closed: if the adapter handles the grid but
its view or a column cannot be read, or `Attach` failed, the grid counts as
redacted. A grid with no redacted column keeps showing its descendants' text. The
check runs per descendant of an adapter grid (a lookup per ancestor, the adapter
asked once per grid per tree walk); it does not cover an editor the application
shows outside the grid's control tree, such as a separate window. An adapter author
should still clear the editor's text when the editor closes.

## Context-menu action

`McpBridgeBuilder.AddAction(string name, Func<Control,bool> accepts,
Action<Control> callback)`, additive. It shares one validation helper with
`AddAction<TControl>`: non-empty; not `click`, `focus`, `open` or `close`; unique
across both overloads; null checks. Storage reuses `CustomControlAction`. In
`Describe` the predicate is wrapped in try/catch (an exception means "not
advertised", one diagnostic per action) and runs for every control in every tree
walk, so it must be cheap. The predicate is evaluated again when the action is
invoked (`unsupported_operation` if it is false now; a throwing predicate counts
as false; the existing invoke path already does this for every custom action).
The application registers,
for example, `show_context_menu` for controls whose `ContextMenuStrip` is not
null and calls `ContextMenuStrip.Show(control, point)`, which sets
`SourceControl` and raises `Opening` so enable/disable logic runs. The point is
application knowledge (for a grid, the focused cell). The demo's
`show_context_menu` closes an open strip (`menu.Close()`) before `Show`, so a
second call re-runs `Opening` instead of finding the strip already visible; an
application that wants the same repeatability does the same. `open` on a
`context_menu` element is not supported: `ToolStripAdapter.Execute` receives no
source control and the observed parent is wrong for shared strips.

## Testing

Public repository, no vendor assemblies: a test-only `FakeGrid : Control` (a
`List<FakeRecord>`, sort column and direction, columns with read-only, hidden,
band and redacted settings, a validation hook that can reject an edit, set error
text or open a modal form, view events) and a `FakeGridAdapter` that counts
calls. Pin: registration (name rules, duplicates, the reserved name, hello
order); capabilities per feature and none for unaccepted controls; a throwing
`Accepts`; `Accepts` called once; the token (stable across reads, selects and
scrolls; stale after an event-driven sort; stale after a change seen only by the
fingerprint; per-grid salt; changes after an edit that re-sorts with `rowAfter`
reported); bounds (row/column caps, per-cell `truncated`, the response budget
including bands and choices, band depth 8, 50 choices); `read_only` without a
`SetCell` call; `hidden_control`; `edit_rejected` with reason and error text; a
view change during the edit; `expectedText` mismatch; a modal edit leaving the
receipt `started`; redaction (the secret appears in no read, receipt or
diagnostic); excluded grid unresolvable; element targets `unsupported_operation`;
a disposed grid `stale_target`; no leak (a `WeakReference` to the grid is
collected after the form closes even with `Attach`); adapter exceptions
(`adapter_failed`, `gridError`); the `DataGridView` regression (existing tests
unchanged plus `viewToken`); the `AddAction` overload (advertisement, throwing
predicate, reserved and duplicate names, re-evaluation at invoke, `Opening` fires
with `SourceControl` set — this one needs an interactive Windows desktop);
sidecar tests (20 tools, the `set_grid_cell` schema, `viewToken` passed through,
`unsupported_operation` when hello lacks `set_cell`). The demo gains an
owner-drawn `DemoGrid` with a `DemoGridAdapter` as the consumer example (an
editable column, a read-only column, a banded header, validation rejecting
negative values, a confirm dialog, a context menu with an `Opening` handler) and
exercise steps for read, select, edit and stale. A vendor adapter and its tests
live in the consuming application's repository.

## Risks

- A programmatic editor value may not mark the editor modified, so the post does
  nothing: the adapter must compare the posted display text before returning
  `Committed`.
- Editor activation can fail in a background window or without a handle: the
  result is `editor_not_shown`; the bridge does not activate windows.
- Direct `MessageBox` calls cannot be suppressed (receipt stays `started`).
- Changes to a plain `List<T>` made outside the grid raise no events and do not
  change the fingerprint; `expectedText` and re-reading cover this.
- Text parsing depends on the application culture; lookup display texts can be
  ambiguous. A rejected row edit on a plain list may already have changed the
  object: `displayText` and a re-read are the source of truth.
- `GetFeatures` runs for every grid in every tree walk, so it must be cheap.
- API permanence: adding abstract members later is breaking, so the abstract set
  stays at the five above plus the virtuals.

## Rejected alternatives

Rows as tree nodes (unstable identity, too many nodes); a data-level write
(skips validation); a declarative reflection mapping (violates the no-reflection
rule); an interface instead of an abstract class (net48 interfaces cannot gain
members); separate grid select/scroll tools (tool churn); record keys
(application-specific; the token plus `expectedText` is enough); fingerprint-only
freshness (misses a sort that keeps the row count); events-only freshness
(misses a replaced list); completed receipts with `committed: false` (read as
success); rewriting the `DataGridView` path as an adapter now (churn and risk to
the existing response shape); `open` on `context_menu` elements.

## Calls made during the design review (author's calls)

- **Concurrent grid mutations.** While a grid mutation is `started` (for example
  a `SetCell` waiting inside a modal dialog), a second grid mutation
  (`select`, `scroll`, `set_cell`) on the same grid is rejected, not queued
  re-entrantly: new additive error code `grid_busy`, backed by an in-progress
  flag in the per-grid state that is cleared in a `finally`. Different grids are
  independent. Test: a second `set_grid_cell` during a modal edit fails with
  `grid_busy`; the first still completes.
- **`expectedText` and truncation.** `expectedText` is compared with the full
  current cell text (ordinal). If the cell text is longer than `MaxTextLength`
  the agent could not have read it whole, so `expectedText` is refused with
  `invalid_argument` (reason `cell_truncated`); the plain `viewToken` check
  still applies.
- **Receipt details.** `QueueAction` currently stores only `code` and `message`
  on a failed receipt. `edit_rejected` and `stale_view` carry structured
  `details`, so the receipt error object gains an optional `details` field
  (additive) and the sidecar surfaces it in `action_status`.
- **Order and visibility of select/scroll** mirror the `DataGridView` path
  (range checks, then hidden-column check); `rows[].visible` is `true` for every
  data row of an adapter grid (rows scrolled out of view are still readable) and
  `dataKind` is the adapter's name. In the code the adapter `select` checks, in
  order: the `Select` feature; `viewToken` present; `row` (and `column`, if
  given) in range; the token current; the column visible; then the adapter.
  `scroll` needs `row` or `column`, range-checks them, compares the token only
  when one was supplied, and brings the target into view without moving focus
  or selection.

## Decisions for the maintainer

1. Refused edits: a failed receipt `edit_rejected`. **Decided by the maintainer.**
2. Token requirement: required for select and `set_grid_cell` on adapter grids,
   optional for scroll and for `DataGridView`. (Author's call; not contested.)
3. Tool name: `set_grid_cell`. **Decided by the maintainer.**
4. Choice lists: column-level `choices` (at most 50) in `read_grid` in this
   version; it lets an agent fill lookup cells. (Author's call.)
5. No built-in `context_menu` control action now; only the predicate overload.
   (Author's call.)

## Work order

Carried out in this order. Not done yet: the real desktop
exercise, a run of the new sidecar tests on Linux CI, and a release (see
[implementation status](07-implementation-status.md#grid-adapters-2026-11-17)).

1. This document, ADR 0005, and updates to docs 07, 08, 09 and the README.
2. New public types in `src/WinFormsMcp.Instrumented/Grids/`.
3. An internal `GridHost` (adapter cache, `GridState` table, token refresh and
   check, routing) so the `UiRuntime` diff stays small.
4. `UiRuntime.cs`: `Methods` and `Mutations`; capabilities in `Describe`;
   `Mutate` routing for select, scroll and `set_cell` before the static type
   switch; `ReadGrid` dispatch plus the additive `DataGridView` fields; hello
   `gridAdapters`.
5. `McpBridgeBuilder.cs`: `AddGridAdapter` and the `AddAction` overload.
6. The sidecar: `viewToken` on select and scroll, descriptions, `SetGridCell`,
   the hello method check.
7. Tests and the demo; a Windows run of the desktop exercise; release notes
   (new public API, 20 tools, `datagridview` reserved).
