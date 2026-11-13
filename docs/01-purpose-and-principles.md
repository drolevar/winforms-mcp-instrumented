# Purpose and principles

## What we are building

**WinForms MCP Instrumented** lets an MCP client inspect and operate a Windows
Forms application through instrumentation that the application deliberately
includes. The first supported application runtime is .NET Framework 4.8.

The product has two runtime components: a small bridge inside the application,
and an independently running MCP server. The server presents tools to an agent;
the bridge discovers controls and performs supported operations on their owning
UI thread. A private named pipe connects the two processes.

This document describes the design contract. It is not a claim that every
capability below has already been implemented. The repository README and
implementation status document must identify the implemented subset and the
checks actually performed.

## Why this exists

An agent helping maintain a desktop application needs a feedback loop. It must
observe the current interface, identify the intended control, perform an action,
and inspect the result. Source code alone cannot show a dialog that appears only
after a particular sequence, whether a data grid contains the expected values,
or whether a layout is usable at a particular size.

An application already owns useful information about these questions. Its
managed controls have types, names, values, containment relationships, and
domain behavior. Instrumentation gives tools deliberate access to that
information, with application-specific extensions where useful. The word
**Instrumented** in the name communicates this integration model: an application
opts in by including a bridge. Windows UI Automation is not the primary source
of the managed control model.

The aim is to make this useful in an existing application without redesigning
the UI, replacing controls, annotating every form, or migrating the application
to a newer runtime. The bridge should discover standard controls automatically.
Applications use a builder only to change policy, register a custom adapter, or
expose a useful domain operation.

## Why there are two processes

The application under investigation is expected to restart. An agent may rebuild
it, launch another version, encounter a crash, or need to reproduce startup
behavior. Putting the MCP session inside that process would tie the session to
the lifetime of the object under investigation.

The external server owns the MCP connection and reports whether the application
is connected. Losing the application must invalidate its control references,
but must not terminate the MCP server. An agent can reconnect to a new
application generation and continue the investigation.

The split also gives dependencies a clear home. Modern MCP hosting belongs in
the modern .NET server. The .NET Framework bridge contains the application-facing
functionality and its private transport. Compatibility of a newer MCP package
with .NET Standard does not remove the lifecycle and dependency reasons for
keeping this boundary.

See [ADR 0001](adr/0001-sidecar-and-in-process-bridge.md) for the decision and its
costs.

## What should work with minimal integration

The standard tool surface should cover these tasks:

| Task | Useful result |
| --- | --- |
| Inspect the application | Current windows and a bounded control tree with names, types, state, values, and supported operations. |
| Locate a control | Find current matching controls, with explicit handling of zero or multiple matches. |
| Operate standard controls | Invoke a button, set editable text, change a selection or checked value, and focus a control. |
| Navigate content | Scroll supported containers and bring a target into view. |
| Inspect structured data | Read bounded rows and cells through a control-specific adapter. |
| See the interface | Capture a window, control, or region, and capture the application's windows as a collection. |
| Follow change | Discover controls and windows created after startup, and reject references to objects that no longer exist. |

Support is a capability of an adapter, not a promise inferred from a control's
appearance. A custom-drawn surface might support capture but have no individual
managed child controls. A native file dialog is not automatically part of a
managed `Control.Controls` tree. Such cases must be documented and surfaced
honestly.

## Principles that guide implementation

### Observe the live application

The control tree is a changing view of live objects. Loading a screen, expanding
a region, rebinding a grid, or opening a dialog can change it. Cached observations
must carry scope and freshness information; they cannot establish that a target
still exists when a later action executes.

### Preserve identity without preserving objects

Control IDs identify managed object instances within one application generation.
They are not control names, tree positions, or native window handles. This lets a
control survive reparenting or handle recreation without becoming a different
target. A newly created replacement receives a new identity, even if its name
and position match the old one. Tracking must not keep disposed or abandoned
forms alive.

### Prefer meaningful operations

An operation such as setting text or invoking a button should have documented
semantics. Changing a property is not automatically equivalent to typing with
the keyboard, and a semantic button invocation is not evidence that mouse hit
testing works. If physical input simulation is added, it should be a separately
identified capability with its own preconditions.

### Keep application work on the correct thread

WinForms controls have thread affinity. The bridge must marshal observation and
mutation to the owning UI thread, including error paths and identity checks.
It must not make arbitrary controls appear thread-safe by suppressing checks.
Microsoft documents this threading requirement in its
[WinForms guidance](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-make-thread-safe-calls).

### Keep failures interpretable

An unavailable application, stale target, ambiguous selector, unsupported
operation, and busy UI are different outcomes. A tool should return a specific
result that helps the agent decide what to do next. It must not silently apply an
operation to a replacement object or automatically repeat a mutation whose
outcome is uncertain.

### Make instrumentation removable

A shipping build must be able to exclude instrumentation code, references, and
instrumentation-only assets. Runtime activation is a separate concern. A
stripped build has no bridge that an environment variable could activate.
[ADR 0002](adr/0002-build-time-exclusion.md) defines this contract.

## Scope and deliberate limits

The first deliverable is a reusable foundation for WinForms applications. It is
not a universal Windows automation service, an arbitrary .NET object browser, or
a remote shell. It should not expose unrestricted property setters, method
invocation by reflection, or unrestricted filesystem/process commands as a
shortcut to control support.

Application-specific commands can be valuable, but they are explicit extensions.
They should have a stable input schema, bounded results, and clear effects.
Likewise, process launch and restart support belongs to an explicitly configured
application target rather than an arbitrary executable supplied in a tool call.

The standard tools and the named-pipe protocol should be developed together with
a sample application that deliberately creates, replaces, reparents, and disposes
controls. That sample is part of explaining the product: it makes the reasons
for identity, lifecycle handling, and asynchronous operations visible.
