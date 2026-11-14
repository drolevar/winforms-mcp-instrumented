# Bridge protocol and tool contract

**Status:** v1 contract used by the implementation draft, with future extensions
identified explicitly. The repository's implementation status records validation;
the presence of code does not establish that Windows UI behavior has been tested.

## Why a private protocol exists

MCP belongs at the agent-facing boundary. The application bridge needs a much
smaller contract: identify the running app, inspect controls, schedule actions,
return images, and report failures. Keeping that contract separate allows the
sidecar to update its MCP SDK without adding that SDK and its dependency graph to
the .NET Framework application. It also leaves the MCP process connected when
the app exits or is rebuilt.

The pipe protocol is deliberately versioned independently of the NuGet package
version. A patch release can fix a control adapter without changing the wire
format. An incompatible wire change must change the protocol major version.

The protocol project targets `netstandard2.0`. It contains messages, error codes,
and framing helpers; it must not reference WinForms or the MCP SDK. JSON values
may use Newtonsoft.Json's `JToken` representation to avoid a second serialization
stack in the bridge. That is a deliberate small dependency, not a claim that
instrumentation has no dependencies.

## Transport and framing

The bridge owns a Windows named-pipe server. The sidecar is its client. The
sidecar normally supplies a unique pipe name and a random session token when it
launches the application; explicit attach configuration is also possible.

Each message consists of a four-byte unsigned little-endian payload length
followed by exactly that many UTF-8 bytes. The payload is one JSON object.

| Rule | Reason |
| --- | --- |
| Maximum frame size: 16 MiB, before allocation | Bounds memory use, including image responses. |
| Length must be positive and within the fixed frame limit | Rejects corrupt or unreasonable frames early. |
| Read until the entire header and payload have arrived | Stream reads are not message boundaries. |
| EOF within a header or payload terminates the connection | A partial message must never become an action. |
| UTF-8 decoding rejects invalid byte sequences | Both endpoints should interpret the same payload. |
| One writer serializes frames per connection | Prevents concurrent responses from interleaving bytes. |
| A new connection performs a new handshake | A reused pipe name does not establish app identity. |

Capture applies a maximum of 8 MiB of combined raw PNG bytes, returning
`capture_limit` when exceeded, so base64 expansion and response metadata remain
within the frame limit. Tree and grid observations have additional response
budgets. An unexpectedly oversized frame still fails at the framing layer and
closes that connection. Chunked image transfer is a later protocol extension,
not an implicit behavior.

The pipe must restrict access to the current Windows user. The session token
adds explicit association with the intended sidecar session; it does not replace
the operating-system access control. Tokens must not appear in tool results,
logs, exception text, or sample configuration committed to source control.

## Message envelope

The v1 request shape is:

```json
{
  "id": "request-42",
  "protocolVersion": 1,
  "token": "<session-secret>",
  "method": "tree",
  "parameters": {
    "maxDepth": 8,
    "maxNodes": 1000
  }
}
```

The v1 response shape is:

```json
{
  "id": "request-42",
  "protocolVersion": 1,
  "generation": "opaque-app-run-id",
  "result": {
    "nodes": [],
    "truncated": false
  }
}
```

A failed request replaces `result` with an error:

```json
{
  "id": "request-42",
  "protocolVersion": 1,
  "generation": "opaque-app-run-id",
  "error": {
    "code": "stale_target",
    "message": "The requested control no longer exists. Inspect the tree again."
  }
}
```

Request IDs correlate replies. They are not control identities or an exactly-once
execution guarantee. Success and error are mutually exclusive. The receiver
ignores unknown optional fields, rejects an incompatible protocol version, and
rejects unknown methods with `unsupported_method`. JSON property names are
camelCase; the reader rejects duplicate property names and nesting beyond 64
levels. Deserialization never activates CLR types from `$type` metadata.

The handshake (`hello`) returns the application generation, process ID, protocol
version, supported method names, registered-thread coverage, and the current
capture mode (`screen_copy`). It is required before UI operations on each new
connection, including when the sidecar opens a fresh pipe for every request.
It also returns `bridgeVersion` and a `limits` object describing the configured
node, depth, text, image, UI-wait, action and transport bounds. These limits are
advertised, not negotiated by the caller. It also returns `elementAdapters`, the
names of the element adapters in the order they are consulted: the application's
adapters in registration order, then the built-in `toolstrip`. The `methods` list
does not change when an adapter is added; adapters change what `tree`, `inspect`
and `invoke` return, not which methods exist.

Every frame read and write has a 30-second deadline. Expiry disposes the pipe,
records a bounded transport diagnostic and allows a new connection. A client
holding an idle connection must reconnect and perform hello again. The sidecar
uses a fresh connection per tool request and never replays an uncertain mutation.

## A small, stable set of operations

These are operation families implemented in the v1 draft. The sidecar gives some
families a more descriptive MCP tool name, such as `tree` → `control_tree` and
`invoke` → `invoke_control`. Controls are data, not dynamically
created MCP tools: opening a hundred buttons should not add a hundred tools.

| Operation | Inputs | Result and intent |
| --- | --- | --- |
| `hello` | Protocol version and session credentials | Establish identity, compatibility, and capabilities. |
| `windows` | None | Current top-level managed windows on the registered UI thread, including modal windows. |
| `tree` | Optional root, depth and node limits | Bounded live control and element snapshot and truncation metadata. |
| `inspect` | Target, optional `startIndex` and `maxItems` | Current properties, actions and bounded choice items/tree paths; for an element, its description. |
| `invoke` | Target, optional `action` | Schedule the adapter's semantic default action, or a named action (for an element: `click`, `open`, `close`). |
| `set_text` | Target, text | Set supported editable text through its adapter. |
| `set_value` | Target, typed value | Set a supported numeric/date/range value. |
| `select` | Target, item index; grid row and column; or tree index path | Select a supported item, page, cell, or node. |
| `set_checked` | Target, checked state | Set a supported check/radio state. |
| `scroll` | Target, x/y viewport offsets or row/column indexes | Scroll with explicit units; no unspecified wheel semantics. |
| `ensure_visible` | Target | Ask an appropriate parent adapter to reveal the target. |
| `read_grid` | Target, row offset, bounded row/column counts | Read a page of supported grid values without serializing the data source. |
| `capture` | Scope, target/region, optional window frame | Visible-screen PNG data with bounds, dimensions, mode, and warnings. |
| `action_status` | Action receipt ID and generation | Queued, started, completed, or failed action state. |
| `diagnostics` | None | Most recent 100 instrumentation diagnostics and explicitly captured failures. |

Paged grid/list adapters must bound rows, columns, and text lengths rather than
returning an entire application dataset. Domain-specific tools are optional
later extensions; ordinary buttons and text boxes must not require individual
registrations.

## Targets and dynamic controls

An instance target consists of `generation` and `id`. The bridge issues the
generation when it starts and assigns the ID to a managed object instance.

```json
{
  "generation": "opaque-app-run-id",
  "id": "control-17"
}
```

The same control object keeps its identity when reparented or when its Windows
handle is recreated. A replacement object receives a new identity even if its
type, `Name`, text, and location are identical. Restarting the application changes
the generation, invalidating every previous target.

An optional pipe-level selector is a separate request to resolve a target now. A
v1 selector combines an exact `Name` and optional managed type. Ancestor/window
constraints are a future extension. The current MCP tools use concrete instance
targets from tree results. Resolution
must produce one match or an explicit `target_not_found` / `ambiguous_target`
error. Never turn a stale instance ID into a fresh selector silently: a repeated
button name could otherwise cause the agent to act on a different screen.

Tree snapshots contain current name, type, text where permitted, visibility,
enabled state, bounds, parent identity, and supported actions. They are evidence
at a point in time, not persistent handles to a frozen UI. Resolve and validate
the target again on its UI thread immediately before an operation.

### Node kinds: controls and elements

Every node has `kind`. A control node has `kind: "control"` and the fields
above. An element node (a menu, toolbar or status-strip item, a context menu
strip, or an element supplied by an application adapter) has `kind: "item"` and:

| Field | Meaning |
| --- | --- |
| `adapter` | Name of the adapter that owns the element |
| `role` | Adapter-declared role, `^[a-z_]{1,32}\z` (for example `menu_item`, `context_menu`) |
| `name`, `text`, `shortcut` | Bounded strings; `text` is null for a host item, whose hosted control carries its own text |
| `type`, `shortType` | Managed type, filled by the bridge |
| `enabled`, `available`, `visible` | `visible` is realized on screen now; `available` is configured to show |
| `checked`, `opened`, `childCount` | Null when not applicable |
| `childrenOmitted` | True when the children of a closed dropdown were left out of this response |
| `bounds` | Screen coordinates; null when the element has no on-screen layout |
| `hostedControlId` | Id of a control hosted by the element, which stays a control node |
| `childrenOf` | Set when the element's children are listed under another node because several placements share one child collection |
| `properties` | At most 16 bounded name/value strings |
| `capabilities` | Actions accepted by `invoke`: `click`, `open`, `close`, or an adapter-declared name |
| `childrenError` | `adapter_failed` when listing the children failed; that subtree is skipped |

An element target is the same `generation` + `id` pair; the same stale-target
rule applies, checked by walking up through the element's parents until a control
is reached. Trees rooted at a window or control omit the children of a closed
dropdown (`childrenOmitted: true` with `childCount`); a tree rooted at the item
expands its whole subtree within the depth and node limits. Selectors resolve
controls only. See [element adapters](09-element-adapters.md).

**Compatibility.** These are additive optional fields plus one error code
(`not_realized`), so the protocol stays at version 1 and receivers keep ignoring
unknown optional fields. Nothing was removed or renamed. A consumer that reads
only control nodes can ignore `kind: "item"` nodes.

## Actions must not block the next observation

A click handler can call `ShowDialog()`. Waiting for that handler to return
before acknowledging the click would prevent a sequential caller from inspecting
or closing the dialog. The bridge therefore schedules mutating operations and
returns an action receipt promptly.

```json
{
  "actionId": "action-9",
  "state": "queued"
}
```

The operation is recorded as `started` when its UI delegate begins. It becomes
`completed` after that delegate returns or `failed` if it throws. A modal dialog
can leave the parent action started while the modal message loop processes later
inspection and action requests. The pipe receiver remains independent of the UI
delegate and does not hold a global request lock until a handler completes.

An action receipt means accepted for scheduling, not successful completion.
Clients should inspect the result or poll `action_status` when completion matters.
A bounded journal retains at most 256 receipts and permits at most 32 active
actions. Completed receipts are evicted first; an expired receipt returns an
explicit `unknown_action` error.

Timeouts require care. V1 has no action-cancellation API. A caller timeout does
not establish that a queued operation was removed, and an operation that started
cannot safely be undone by interrupting arbitrary application code. Report the
outcome as unknown or still pending, and never automatically retry a mutation
after reconnecting. A lost response is not evidence that a click did not happen.

## Threading and lifecycle boundaries

The bridge establishes a private dispatcher on the calling STA thread before
`Application.Run`. `hello` can work before the loop starts; a UI request that
cannot run before its deadline returns `ui_timeout`. The caller must start the
normal WinForms message loop promptly. All control reads,
tree traversal, adapter actions, and capture geometry collection run on the
owning UI thread. Pipe I/O, JSON encoding, and MCP handling run separately.

The draft supports one registered WinForms UI thread. It omits foreign-thread
forms and reports their count instead of reading them through the primary
dispatcher. Multiple independent UI threads need a dispatcher registry and
per-thread window discovery before they can be advertised as supported.

Disconnection clears transport state, not the application's UI. The bridge can
accept a new client while the application continues running. Process exit makes
outstanding actions indeterminate; the sidecar remains alive and reports the
disconnection until another app instance becomes available.

## Error contract

| Code | Meaning / caller action |
| --- | --- |
| `invalid_request`, `invalid_argument` | Correct malformed or out-of-range inputs. |
| `unauthorized` | Credentials or pipe association are invalid. |
| `protocol_mismatch` | Use a compatible bridge and sidecar. |
| `handshake_required` | Send `hello` before application operations on a new connection. |
| `app_unavailable` | Sidecar currently has no app connection. |
| `ui_unavailable`, `bridge_stopped` | Dispatcher or bridge lifetime has ended. |
| `ui_timeout` | UI observation could not complete; do not infer it is unchanged. |
| `stale_generation` | Re-inspect; the reference belongs to a different bridge lifetime. |
| `stale_target` | Re-inspect; the instance was disposed, detached, or replaced. |
| `target_not_found` | Resolve a new selector or inspect the tree. |
| `ambiguous_target` | Add a supported type constraint, or inspect the tree and choose a concrete instance ID. |
| `unsupported_method`, `unsupported_operation` | Use an implemented method and an advertised control capability. |
| `disabled_control`, `hidden_control`, `read_only`, `focus_failed` | The requested action cannot be applied in the current state. |
| `not_realized` | An element has no on-screen layout, so `open` or `close` cannot apply; open the parent menu or show the window first. |
| `capture_unavailable`, `no_windows` | No suitable visible capture target is available. |
| `capture_limit`, `response_limit`, `scope_limit` | Reduce capture area/page size or adjust documented observation limits. |
| `ui_changed` | The window collection changed during observation; obtain a fresh snapshot. |
| `action_limit` | Too many actions are active; inspect and finish outstanding actions. |
| `unknown_action` | Receipt expired or was never issued by this bridge. |
| `operation_failed` | Application or framework code threw an unexpected exception. |

Errors should contain actionable context without serializing arbitrary exception
objects or object graphs. The SDK-facing layer maps them to MCP tool errors while
keeping the MCP server process alive.

## Acceptance checks

Protocol tests should exercise fragmented headers/payloads, early EOF, invalid
UTF-8/JSON, oversized lengths, incompatible versions, authentication failure,
disconnect during a response, and reattachment to a new generation. Windows
integration tests must include a button that opens a modal dialog and prove that
the next request can inspect and close it.

These tests verify boundaries that are easy to break while refactoring. They
should not assert implementation details such as an exact number of event
subscriptions or internal collection entries.
