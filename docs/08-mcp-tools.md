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
| `control_tree` | optional `generation`, `id`; `maxDepth=8`, `maxNodes=1000` | Flat current tree, bounded and explicitly truncated when necessary |
| `inspect_control` | `generation`, `id`, `startIndex=0`, `maxItems=100` | State, capabilities and bounded current choice items/tree paths |
| `invoke_control` | `generation`, `id`, `action="click"` | Semantic click/focus or a configured custom action |
| `set_text` | `generation`, `id`, `text` | Editable text assignment |
| `set_value` | `generation`, `id`, `value` | Numeric value or ISO date string for a supported control |
| `set_checked` | `generation`, `id`, `value` | Boolean state for check/radio controls |
| `select_control` | `generation`, `id`; `index` or `row`+`column` or `path` | Selection appropriate to the observed control type |
| `scroll_control` | `generation`, `id`; `x`/`y` or `row`/`column` | Absolute container offsets or indexed list/grid scrolling |
| `ensure_visible` | `generation`, `id` | Scroll ancestors and select ancestor tabs as needed |
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
error code/message. Reading a receipt is itself an observation: `action_status`
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
