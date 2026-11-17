# MCP tools and interaction model

## Why the tool list stays fixed

Controls are dynamic data. Creating a separate MCP tool for every button would
churn the catalog whenever a window opens or controls are replaced. The sidecar
therefore exposes a small fixed vocabulary; fresh inspection supplies the
control IDs, states and supported actions used with that vocabulary.

The server currently exposes 20 tools (`set_grid_cell` was added for grid
adapters). Read operations are annotated read-only.
Mutation annotations remain conservative because an application's button handler
can have effects beyond its visual state.

## Catalog

| Tool | Main arguments | Purpose |
| --- | --- | --- |
| `app_status` | — | Owned process state and current bridge availability/generation |
| `app_launch` | — | Launch the executable configured when starting the server |
| `app_stop` | `force=false` | Request a normal close, or explicitly terminate the owned process |
| `app_restart` | `force=false` | Stop and relaunch without restarting MCP |
| `app_logs` | `limit=50` | Last 1–200 retained lifecycle/stdout/stderr entries |
| `list_windows` | — | Current managed windows and IDs |
| `control_tree` | optional `generation`, `id`; `maxDepth=8`, `maxNodes=1000` | Flat current tree of controls and elements (`kind`), bounded and explicitly truncated when necessary; see [menus and other elements](#menus-toolbars-and-other-elements) |
| `inspect_control` | `generation`, `id`, `startIndex=0`, `maxItems=100` | State, capabilities and bounded current choice items/tree paths; for an element node, its description |
| `invoke_control` | `generation`, `id`, `action="click"` | Semantic click/focus or a configured custom action; on an element, `click`, `open` or `close` |
| `set_text` | `generation`, `id`, `text` | Editable text assignment |
| `set_value` | `generation`, `id`, `value` | Numeric value or ISO date string for a supported control |
| `set_checked` | `generation`, `id`, `value` | Boolean state for check/radio controls |
| `select_control` | `generation`, `id`; `index` or `row`+`column` or `path`; optional `viewToken` | Selection appropriate to the observed control type; on an adapter grid `row` and `viewToken` are required, see [grids](#grids) |
| `scroll_control` | `generation`, `id`; `x`/`y` or `row`/`column`; optional `viewToken` | Absolute container offsets or indexed list/grid scrolling |
| `ensure_visible` | `generation`, `id` | Select ancestor tabs, then scroll ancestor containers; see [reveal order](04-controls-and-capture.md) |
| `set_grid_cell` | `generation`, `id`, `row`, `column`, `text`, `viewToken`; optional `expectedText` | Edit one cell of an adapter grid through its editor; a queued mutation, see [grids](#grids) |
| `read_grid` | `generation`, `id`, bounded row/column range | Formatted values of a DataGridView or of a grid with a registered grid adapter, plus a `viewToken` |
| `capture` | `scope="application"`, optional target, `includeFrame=false`, optional `region` | PNG image content and capture metadata |
| `action_status` | `generation`, `actionId` | Queued action progress/result/error |
| `bridge_diagnostics` | — | Bounded bridge-specific diagnostics |

Control references are always the pair `generation` + `id`. Names help the agent
locate a control in an observation; they are not identity. A replacement may use
the same Name and receive a different ID. After an application restart, get a
new tree and use the new generation.

## Observe, act, verify

An ordinary text edit uses these tool arguments, substituting observed IDs:

```json
{"generation":"<observed-generation>","id":"<txtName-id>","text":"Ada"}
```

The tool returns a receipt inside its JSON result:

```json
{"generation":"<observed-generation>","actionId":"<action-id>","state":"queued"}
```

Poll `action_status` with that generation and action ID. `completed` means the
semantic handler returned. Inspect the changed control or application state to
verify the application-level result; handler return does not prove that a later
asynchronous background operation finished.

For a button that opens a modal dialog, the receipt can remain `started` while
the dialog runs. Call `list_windows`/`control_tree`, act on the dialog, and then
check the opening action again. Waiting for completion before inspecting the
dialog would prevent the agent from doing the work required to complete it.

## Selection and values

Inspect list/combo/tab/tree controls before selecting. Item indices and tree
paths describe the current view and can change if the app edits or sorts items.
They do not carry the persistent identity guarantees of managed control IDs.

Grid arguments use row and column collection indices. Columns can be visually
reordered independently; `DisplayIndex` is not the index expected by these
tools. `read_grid` returns formatted strings, which may reflect locale and
application formatting. It does not expose the entire bound data source. See
[grids](#grids) for third-party grids.

Text assignment raises normal control change behavior but does not generate a
sequence of physical keystrokes. Custom actions are named callbacks registered
by the application; there is no unrestricted reflection or code-evaluation tool.

## Grids

`read_grid` reads a `DataGridView`, and a third-party grid when the application
registered a grid adapter for it ([grid adapters](10-grid-adapters.md)). A grid
node in `control_tree` and `inspect_control` carries `gridAdapter` (the adapter's
name, or `datagridview`) and the capabilities `read_grid` (always), `select`,
`scroll` and `set_cell`, according to what its adapter supports. A grid whose
adapter failed carries `gridError: "adapter_failed"` and no grid capabilities.

- **Read, then act on that read.** `read_grid` returns a `viewToken`. Rows are
  positions in the current sorted and filtered view, so a row number is only
  valid with the token of the read that produced it. `select_control` (adapter
  grids: `row` and `viewToken` required, `column` optional; the cell is focused,
  becomes the only selection and is brought into view), `scroll_control`
  (`viewToken` optional) and `set_grid_cell` (`viewToken` required) fail with
  `stale_view` when the view changed after that read: read the grid again. The
  current token is deliberately not in the error. On a `DataGridView` the token
  is a fingerprint (row count, column count, data-source identity, sort) and is
  optional for `select_control` and `scroll_control`.
- **`read_grid` fields.** Existing fields are unchanged. For an adapter grid it
  adds `gridAdapter`, `viewToken`, `totalRows`, `focusedRow`, `focusedColumn`;
  per column `field`, `editable`, `valueKind`, `bandPath`, `redacted`, `choices`
  (at most 50, with `choicesTruncated`); per row `selected` and `truncated`
  (the column indexes whose text was cut). Redacted cells are `null`. A
  `DataGridView` read adds `gridAdapter: "datagridview"` and `viewToken` only.
  Limits are the same as for a `DataGridView`: 200 rows, 50 columns, 256K
  characters, with `response_limit` beyond.
- **Text inside a grid with a redacted column.** In `control_tree` and
  `inspect_control`, a control that is a descendant of such a grid (a hosted cell
  editor, for example) has `text: null` and `textRedacted: true`, and no `value`,
  `checked`, `selectedIndex` or listed items, because the editor can hold the
  redacted column's text. If the grid's view cannot be read, the grid counts as
  redacted.
- **`set_grid_cell`** edits one cell the way a user does: the adapter focuses the
  cell, opens the editor, sets the text, posts it and commits the row, so the
  application's own validation runs. It is a queued mutation: it returns a
  receipt and you poll `action_status`. **`completed` means the edit was
  committed.** The result is `{operation: "set_cell", handlerReturned: true,
  committed: true, displayText, displayTextRedacted, rowAfter, viewToken}`:
  `displayText` is the text now shown, `rowAfter` the row's new position (null
  when it left the view), `viewToken` the token after the edit (null if the grid
  was disposed or could not be read afterwards; then read the grid again).
  `displayTextTruncated: true` is added when the text was cut at the configured
  text limit.
- **A refused edit is a failed receipt**, never a completed one: `state:
  "failed"`, `error.code: "edit_rejected"` and `error.details` of `{reason,
  errorText, displayText}`. `reason` is one of `not_editable`,
  `editor_not_shown`, `invalid_input`, `ambiguous_input`, `validation_failed`,
  `row_not_committed`, `other`. `errorText` (at most 1,024 characters) is the
  application's own message and is untrusted text; `displayText` is what the
  cell shows now. For a redacted column both are `null` and the details carry
  `displayTextRedacted: true`.
- **`text`** is typed as a user would type it: in the application's UI culture,
  exactly one choice's display text for a choice column (`read_grid` lists
  them), `""` to clear where the editor allows it. What is accepted for a date,
  time or duration column depends on the adapter: ISO 8601 is only what adapter
  authors are recommended to accept, not something the bridge guarantees. The
  bridge converts nothing.
- **`expectedText`** (optional) must equal the cell's current full text, compared
  ordinally; otherwise `stale_view` with `details.reason: "cell_changed"`. It is
  refused with `invalid_argument` and `details.reason: "cell_truncated"` when the
  cell text is longer than the text limit (the agent could not have read it
  whole), and with `column_redacted` for a redacted column.
- **A modal dialog keeps the receipt `started`.** If the application opens a
  `MessageBox` or `ShowDialog` while the edit is posted, the receipt stays
  `started`: find the dialog with `list_windows`, answer it, then poll
  `action_status`. It ends `completed` or `edit_rejected`.
- **Grid errors.** `stale_view` (view or cell changed), `read_only` (the column
  cannot be edited), `hidden_control` (a hidden column, or a hidden grid),
  `edit_rejected`, `not_realized` (the adapter reported `GridOperationError.NotRealized`:
  the grid has no on-screen layout yet), `adapter_failed` (the adapter threw, or failed to attach to
  the grid), `grid_busy` (another select, scroll or edit on the same grid is
  still running; it is reported before the other grid checks, and other grids
  are independent) and `unsupported_operation` (a `DataGridView`, which cannot be
  edited, a grid whose adapter lacks the feature, a control no adapter handles).
  Malformed arguments are `invalid_argument`, and the argument shape is checked
  before the feature check, so a bad request to a grid without `set_cell` is
  `invalid_argument`. An adapter's exception message is never relayed for
  members that handle cell data (adapter name, member and exception type only).
- **Older bridges.** `set_grid_cell` asks the bridge for its `hello` first; a
  bridge whose `methods` lacks `set_cell` gets `unsupported_operation` and no
  request. `select_control` on an adapter grid needs the `viewToken` argument,
  which an older sidecar cannot send.

## Menus, toolbars and other elements

No tool was added for menus (the count stayed at 19 for that work; the later
grid work added `set_grid_cell`, making it 20). Elements that are not
controls appear in `control_tree` as nodes with `kind: "item"` and the same
`generation` + `id` pair. The built-in adapter covers menu strips, tool strips,
status strips and context menus reached through a control's `ContextMenuStrip`;
an application can register adapters for other suites (see
[element adapters](09-element-adapters.md)).

- **Reading a menu without opening it.** The tree lists a menu's subitems with
  `visible: false` (not realized on screen) and `available: true` (configured to
  show). A tree rooted at a window or control **omits the children of a closed
  dropdown**: the item reports `childrenOmitted: true` and its `childCount`. Call
  `control_tree` with the item's `generation` and `id` to read its subtree; a tree
  rooted at an item expands all nested dropdowns, within `maxDepth` and `maxNodes`
  counted from that item. Reading does not open the menu.
- **`inspect_control`** accepts an element id and returns its description (role,
  text, enabled, available, checked, shortcut, `opened`, `childCount`, bounds when
  shown, capabilities). Selectors and the control-only tools (`set_text`,
  `set_value`, `set_checked`, `select_control`, `scroll_control`,
  `ensure_visible`, `set_grid_cell`, `read_grid`, `capture`) do not apply to an element and answer
  `unsupported_operation` after the stale-target check.
- **`invoke_control` with `action="click"`** runs the item's click semantically
  even when its menu is closed. Prechecks: an action the element does not declare
  is `unsupported_operation`, a disabled item `disabled_control`, an item whose
  `available` is false `hidden_control`. A click handler can open a modal dialog,
  so the receipt follows the usual queued/started/completed lifecycle. The result
  is `{operation, action, handlerReturned, handlerDeferred}`; `handlerDeferred`
  is true only when an adapter says the framework postponed the handler (the
  built-in adapter never does). A click on an item that has a dropdown also
  carries `opened`, read after the call, like `open` and `close`. A handler may
  assume the menu is open, which a semantic click does not guarantee. Items in a
  context menu are clicked with `Control.SourceControl` unset and without the
  `Opening` event (the strip was never shown), so handlers that remember a target
  in `Opening` or `MouseDown` can act on stale state.
- **`invoke_control` with `action="open"` or `"close"`** shows or hides a dropdown.
  On an element with no on-screen layout (`visible: false`, for example a nested
  item whose parent menu is closed) it fails with `not_realized`: open the parent
  menu or show the window first. The result also carries `opened`, read right after
  the call returns. It is the observed state, not the requested one, and it does
  not promise the menu is still open later. **`open` is best-effort**: in a
  plain-WinForms spike (measured once per condition, not through the bridge) a
  dropdown did not remain visible while its window was not the active window, so
  in a background application `opened` can be `false` although the
  call succeeded. The bridge does not activate or focus the window; check
  `opened` instead of assuming the menu is open.
- **Element ids go stale more often than control ids.** Removing, disposing or
  replacing an item, its strip or its form gives `stale_target`; read the tree
  again.
- **Not discovered or not supported:** popups shown from code and never assigned
  to a control (for example `ContextMenuStrip.Show(point)` on an unassigned
  instance); per-element exclusion (a control that is itself an element, such as a
  context menu strip, is hidden with its items when the exclusion predicate matches
  it); rows, cells and other virtualized items. See
  [implementation status](07-implementation-status.md#current-limitations).

## Capture

`scope="application"` returns one image per eligible window. `scope="window"`
uses the containing window of the target; `scope="control"` uses its client
area. A region is relative to the selected target and requires `x`, `y`, `width`
and `height`. Application scope cannot be combined with a region.

The sidecar converts the bridge's PNG bytes into MCP image blocks and returns
the bounds, target and mode as accompanying text. Base64 image data is not
repeated in the text response. The implemented mode is `screen_copy`: visible
pixels, clipping, and possible overlap are reported. It is not an offscreen
render. The coordinates use the application's DPI context; validate mixed-DPI
behavior on the target desktop.

## Errors, lifecycle and retries

Expected failures are MCP tool results with `isError=true` and a structured
error object. Every error, whether the sidecar or the application bridge
produced it, has the same camelCase shape, so an agent can branch on `code`
without checking where the failure originated:

```json
{"generation":"<generation>","result":null,
 "error":{"code":"stale_target","message":"The observed control no longer exists.","details":null}}
```

`details` is `null` unless the error carries structure, for example
`{"delivery":"not_sent","automaticRetry":false}` on `app_unavailable`. (An early
draft serialized bridge errors with the CLR property names `Code`/`Message`/
`Details`; a test now pins the camelCase contract.) Reading a receipt is itself an observation: `action_status`
can succeed while returning `state="failed"` and the action's `error`. Check that
state before treating a mutation as successful. `app_status` itself succeeds while reporting
`bridgeAvailable=false` when the app is offline. Attach mode has no owned
process, so its owned-process `running` field is null; bridge availability is
reported separately.

Never automatically replay a mutation after an IO failure or timeout. The action
may already have been queued or started before its response was lost. Observe
current UI state, or check a known action receipt. New sidecar requests open a
fresh pipe connection and perform a new hello handshake.

The `--app` launch path is supplied by the developer's server configuration, not
by arbitrary tool arguments. The sidecar only stops a process it launched. It
redirects the child's stdout/stderr so that application output cannot corrupt
MCP stdout. Closing the MCP server does not deliberately terminate the app.
