# ADR 0005: Third-party grids through a public grid adapter seam

- Status: Accepted (approved by the maintainer on 2026-11-17; implemented; not yet
  released or published)
- Date: 2026-11-16

## Context

`read_grid`, `select_control` (row/column) and `scroll_control` understand only
`DataGridView`. Applications built on third-party grid controls cannot be read,
selected or edited through the bridge, although the grid controls themselves are
already listed as ordinary controls. Doc 09 reserved this work: rows are never
tree nodes, and row positions need a freshness token.

## Decision

Add a public abstract class `GridAdapter` (namespace
`WinFormsMcp.Instrumented.Grids`), registered with
`McpBridgeBuilder.AddGridAdapter`. The bridge owns paging, bounds, the freshness
token and receipts; the adapter answers questions about one column or cell and
performs select, scroll and edit on the UI thread. Rows are addressed by
position in the current view plus a `viewToken` that changes when the view is
sorted, filtered, re-bound or resized; a stale token fails with `stale_view`.
Editing goes through the editor like a user (one new tool, `set_grid_cell`),
never by a data-level write; a refused edit is a failed receipt
(`edit_rejected`).

`read_grid`, `select_control` and `scroll_control` gain additive fields for
adapter grids; the `DataGridView` path keeps its behavior and gains only a
fingerprint-based `viewToken` and `gridAdapter: "datagridview"`. A second, small addition: an
`AddAction` overload with a predicate, so applications can register a
"show context menu" action only for controls that have one. Vendor adapters
live outside the public packages. The protocol stays at version 1; the tool
count becomes 20.

Details, contract, tests and risks: [10-grid-adapters.md](../10-grid-adapters.md).

## Why

- A seam with bridge-owned loops and bounds keeps vendor code small and the
  limits uniform.
- Position plus token avoids both durable row keys (application-specific) and
  silently acting on a re-sorted row.
- The editor path runs the application's validation, which is what a test of
  real behavior needs.
- An abstract class can gain virtual members on net48; an interface cannot.

## Rejected

Application-only custom actions (no data returned); screen capture or UI
Automation; rows as tree nodes; data-level writes; reflection mappings; an
interface seam; separate grid select/scroll tools; rewriting the `DataGridView`
path as an adapter now.

## Consequences

- The `GridAdapter` seam is public API and must remain stable; the abstract set
  is deliberately small.
- Every grid mutation needs a token from a read; agents re-read after
  `stale_view`.
- `datagridview` becomes a reserved grid-adapter name.
- Edits can be refused for reasons only the application knows; the receipt
  reports the reason and the text now shown.
- Adapter exceptions never leak data: for the members that handle cell, row or
  edit data (`GetCellText`, `IsRowSelected`, `TryGetFocusedCell`, `Select`,
  `Scroll`, `SetCell`) the bridge relays no exception message, on the wire or in
  diagnostics, only the adapter name, the member and the exception type, because
  a data-bound adapter may quote a value in its message. The other members keep a
  bounded (200-character) message. This is stricter than the first draft of this
  decision, which said "type and a bounded message".
- A redacted column cannot surface through a hosted cell editor: every descendant
  of a grid that an adapter handles is shown with `text: null` and `textRedacted:
  true` (and without `value`, `checked`, `selectedIndex` or inspect items) while
  that grid has a redacted column. The existing `textRedacted` field gains these
  cases; nothing new is added to the wire. It fails closed when the grid's view
  or columns cannot be read, and it does not cover an editor shown outside the
  grid's control tree.
- Freshness is fingerprint-based for an adapter that gives no change signal, and
  a plain list changed behind the grid's back may raise no events and not change
  the fingerprint; `expectedText` and a fresh read cover that.
- Behavior in a background window (editor activation) is unmeasured and gates
  the edit promise.
- A concurrent grid mutation on the same grid fails with `grid_busy` while the
  first is still started (for example inside a modal dialog).
- The sidecar tool count is 20 (`set_grid_cell` was added); `select_control` and
  `scroll_control` take an optional `viewToken`; `hello` advertises
  `gridAdapters` and `set_cell`. All of it is additive, so the protocol stays at
  version 1.
