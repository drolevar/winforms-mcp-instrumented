# Architecture

## Status and reading order

Read [purpose and principles](01-purpose-and-principles.md) first. This document
specifies the intended boundaries and invariants. Capability availability and
validation results belong in the repository's implementation status; an
architectural requirement does not imply that its implementation has passed
Windows integration tests.

## Components and dependency boundaries

| Component | Responsibility | Reason for the boundary |
| --- | --- | --- |
| `WinFormsMcp.Instrumented` | Application integration, UI dispatch, control identity, discovery, adapters, capture, and the pipe endpoint. Targets `net48`. | It is the only component that needs direct access to managed WinForms objects. |
| `WinFormsMcp.Instrumented.Protocol` | Private wire contracts, protocol versioning, limits, and transport framing shared by both sides. Targets `netstandard2.0`. | Both runtimes need a small shared vocabulary without importing the MCP SDK or WinForms types. |
| `WinFormsMcp.Instrumented.Server` | MCP transport and tools, connection management, result conversion, and configured application lifecycle. Runs on modern .NET. | It owns the agent session and survives the application's lifetime. |
| Sample application | Demonstrates default integration and deliberately exercises dynamic UI behavior. | It provides a reproducible target without requiring a particular customer application. |

The application references the bridge. It does not reference the server. The
server can be delivered as a .NET tool; distributing it through NuGet does not
turn it into a library loaded by the application.

Protocol contracts describe portable data, not live objects. A serialized
control description never contains a `Control`, delegate, stream, or MCP SDK
object. The private protocol can evolve independently of MCP, with an explicit
compatibility check when a connection is established.

## Connection and lifetime

The application bridge hosts the named-pipe endpoint. The external server
connects to it and owns the MCP transport. Endpoint configuration is supplied
deliberately, normally by the development launcher. The runtime activation
policy must avoid creating a listener merely because a package is referenced.

Pipe names identify connections; they are not proof of authorization. The
implementation should restrict access to the intended local account/session
and authenticate the configured peer. It must not advertise a broadly accessible
network listener as a convenience fallback. These are local integration
boundaries, not a claim that instrumentation makes an untrusted application
safe.

Each bridge lifetime has an application generation identifier. All control and
operation references are scoped to that generation. A new process or a restarted
bridge gets a new generation. Process IDs alone are insufficient because an
operating system can reuse them.

When the pipe closes, the server reports disconnection and invalidates live
references. It can preserve already received diagnostics with their original
generation and timestamp. It cannot promise to recover exceptions or UI state
that were never transmitted before a crash.

Reconnect must perform a new compatibility handshake. It must not silently
replay incomplete mutations. A request may have reached the application even if
the caller did not receive its response.

## A stable tool surface over changing controls

Standard tools operate on targets: inspect, find, invoke, set text or values,
select, scroll, and capture. Controls are data returned by these tools. Creating
one MCP tool per control would make tool descriptions churn whenever the UI
changes and would give every dialog a potentially large catalog.

An observed element should include its identity, parent, managed type, optional
name, supported operations, relevant state, and bounded descriptive values.
Control names are useful selectors but are not guaranteed to be unique. Exposed
values must follow the configured inspection policy; password text should not
be returned by default.

The tool surface must distinguish a concrete reference from a selector:

| Input | Meaning |
| --- | --- |
| Generation plus element ID | Operate on this exact previously observed object if it is still valid. |
| Selector | Search current state for matching objects, then explicitly resolve the result. |

An exact reference must fail if the object was disposed, replaced, removed from
the instrumented scope, or belongs to an old generation. A selector with multiple
matches must report ambiguity unless its documented operation deliberately
returns a set. This prevents a repeated control name from redirecting a mutation
to an unrelated screen.

## Dynamic discovery and identity

Discovery starts with live traversal. The first implementation can reconcile on
demand without maintaining an event-driven cache. If caching is added, combine
lifecycle observation with bounded reconciliation: an event-maintained cache
alone is not sufficient evidence of completeness. Listeners then need to cover
newly attached subtrees as well as controls present at startup. Closing a form
removes its roots from the visible instrumented scope.

Use weak references for target lookup and remove subscriptions when a tracked
object leaves its useful lifetime. Keep observation bounded by depth, node
count, and response size. Traversal must not expand arbitrary data sources or
realize every virtual item as a side effect of inspection.

Identity rules are explicit:

| Change | Identity behavior |
| --- | --- |
| Same object moves to another parent | Keep its ID; update containment. |
| Same object recreates its native handle | Keep its ID; refresh handle-related state. |
| A new object replaces an old object with the same `Name` | Allocate a new ID; reject the old reference. |
| Object is disposed | Remove it from live scope and reject subsequent operations. |
| Application generation changes | Invalidate every old element and operation reference. |

Microsoft documents that accessing
[`Control.Handle`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.handle?view=netframework-4.8.1)
can create a handle, and WinForms supports handle recreation. Discovery therefore
must not use `Handle` as a harmless identity getter. Check
[`IsHandleCreated`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.ishandlecreated?view=netframework-4.8)
when handle-related information is actually needed.

Some UI elements are not `Control` instances. `ToolStripItem` objects, tree
nodes, list items, and grid cells need adapter-specific descriptions and
identity rules. Reusing a display index as a permanent identity is particularly
unsafe after sorting or rebinding. The initial implementation must report its
actual supported element types rather than pretending that recursive
`Control.Controls` traversal covers them all.

## Threading and operation completion

Transport I/O runs independently of UI work. Reads, setters, child enumeration,
and adapter execution are marshaled to the owning UI thread. The bridge must
not assume all windows are owned by the application's first form.

A first implementation may deliberately support one registered UI thread, but
that limitation must be explicit. It must reject cross-thread targets instead
of reading them unsafely. Additional dispatchers can later extend coverage to
multiple UI threads.

Do not rely on `InvokeRequired == false` before a control has a handle as proof
that the calling thread owns it; Microsoft documents that ambiguity in
[`InvokeRequired`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invokerequired).
Startup must establish the dispatcher on the intended UI thread. Reads performed
across multiple dispatchers are separate observations, not an atomic snapshot
of the entire process.

Potentially modal actions need asynchronous operation semantics. A button
handler may open `ShowDialog` and remain on the stack until the dialog closes.
If the only request loop waits for that handler to return, the agent cannot send
the next action that closes the dialog. The bridge should acknowledge an
accepted operation with an ID, keep receiving requests, and expose its later
completion or failure separately.

Queued, started, completed, failed, and unknown outcomes must be distinguishable.
Cancellation can prevent an action that has not started; it cannot undo an
already executed UI change. A timeout or lost connection after dispatch must
not be presented as proof that nothing happened. Disposal and target validation
must be checked again at execution time, not only when a request is received.

## Adapters and semantics

Built-in adapters cover a documented set of standard controls. A text adapter
knows whether a value is editable; a selection adapter knows the available
items; a grid adapter exposes bounded ranges. A custom adapter is an explicit
extension point for application-specific controls.

Avoid an unrestricted `set_property` or `invoke_method` facility. Reflection
does not provide a useful behavioral contract and makes accidental application
damage easy. Tool descriptions should explain supported effects, validation,
and relevant preconditions. Standard operations should respect disabled and
read-only state unless a separately named, explicitly configured domain command
has different semantics.

Semantic operations and physical input are different capabilities. For example,
setting `Text` does not reproduce a sequence of keyboard events. A future input
adapter must report its foreground/focus requirements instead of silently
substituting direct setters when physical input fails.

## Screenshots and geometry

The capture contract identifies the source window, generation, timestamp, pixel
dimensions, coordinate space, and capture method. A target's bounds must be
resolved near capture time; old coordinates can point to unrelated content
after layout changes. DPI and client-versus-screen coordinates must be explicit.

Whole-application capture means a collection of application windows, with an
optional explicitly described composite. A main-window bitmap does not imply
that owned dialogs, separate windows, or popups were included.

Capture backends have different limits. A screen-region capture can contain
occluding windows. A rendered control bitmap can differ from what the desktop
shows. Microsoft's
[`DrawToBitmap` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.drawtobitmap?view=netframework-4.8.1)
lists limitations, so using that method does not establish universal capture
support. Hidden or minimized windows and native/accelerated surfaces must return
an explicit unsupported or best-effort result when appropriate.

Capture failure must never produce a successful response with a blank image
presented as faithful evidence. New capture backends should be added behind the
same contract and tested on the affected control and window types.

## Build-time exclusion and observability

The consuming project owns `EnableWinFormsMcpInstrumentation`. That property
conditions both its package/project references and all app-owned integration
code. It defaults on for Debug and off for other configurations, with an
explicit override for investigating optimized Release builds.

The server is separately installed development tooling and is not part of the
application deployment. The stripped configuration must not contain bridge
assemblies, instrumentation-only dependency assets, or a runtime activation
path. See [ADR 0002](adr/0002-build-time-exclusion.md).

Diagnostic buffers should be bounded and generation-scoped. Recording an
exception must not silently replace the application's existing exception policy.
Keep MCP protocol output separate from server diagnostic logs, and keep
transport framing independent of arbitrary application console output.
