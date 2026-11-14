# MCP tools and interaction model

## Why the tool list stays fixed

Controls are dynamic data. Creating a separate MCP tool for every button would
churn the catalog whenever a window opens or controls are replaced. The sidecar
therefore exposes a small fixed vocabulary; fresh inspection supplies the
control IDs, states and supported actions used with that vocabulary.

The server currently exposes 19 tools. Read operations are annotated read-only.
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
| `select_control` | `generation`, `id`; `index` or `row`+`column` or `path` | Selection appropriate to the observed control type |
| `scroll_control` | `generation`, `id`; `x`/`y` or `row`/`column` | Absolute container offsets or indexed list/grid scrolling |
| `ensure_visible` | `generation`, `id` | Select ancestor tabs, then scroll ancestor containers; see [reveal order](04-controls-and-capture.md) |
| `read_grid` | `generation`, `id`, bounded row/column range | Formatted DataGridView values |
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
application formatting. It does not expose the entire bound data source.

Text assignment raises normal control change behavior but does not generate a
sequence of physical keystrokes. Custom actions are named callbacks registered
by the application; there is no unrestricted reflection or code-evaluation tool.

## Menus, toolbars and other elements

No tool was added for menus: the tool count is still 19. Elements that are not
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
  `ensure_visible`, `read_grid`, `capture`) do not apply to an element and answer
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
