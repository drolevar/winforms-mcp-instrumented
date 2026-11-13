# WinForms MCP Instrumented: purpose and scope

## The problem we are solving

A coding agent can edit a Windows Forms application and run its build, but it
cannot reliably understand or operate the running application from source code
alone. It needs to inspect the current windows and controls, act on those
controls, and inspect the result. A screenshot helps explain appearance; a live
control tree explains identity, state, and the operations a control supports.

This project gives an application that cooperation explicitly. A small library
inside the WinForms process exposes a limited, structured automation surface.
A separate Model Context Protocol (MCP) server translates agent requests into
that surface. The first supported application runtime is .NET Framework 4.8.

The project is named **WinForms MCP Instrumented** because instrumentation is
the defining choice: the application participates and exposes its managed
controls. Existing applications require a small source change and a package
reference. This is not a promise to automate every unrelated Windows process.

## Why two processes

The MCP server belongs to the development session. The WinForms application
belongs to the experiment the agent is conducting. Their lifetimes differ:
the agent must be able to close or restart the app while keeping its MCP
connection. The sidecar also keeps modern MCP SDK dependencies out of the
.NET Framework application's dependency graph.

The bridge and sidecar therefore communicate through a versioned, local named
pipe protocol. The protocol is an internal contract; it is not another MCP
transport. The sidecar uses the official C# MCP SDK and stdio toward the client.

## The experience we are aiming for

1. Add the bridge package under a build condition and add one guarded startup
   hook. Run the application on its normal WinForms UI thread.
2. Configure the external server once in an MCP client, with an application
   launch command or a matching local pipe endpoint.
3. Ask the agent to inspect windows, find controls, invoke actions, edit values,
   scroll, and capture screenshots. Standard controls work without individual
   registrations.
4. Use an optional builder to exclude controls, adjust limits, or register
   application-specific behavior. Customization must not be a prerequisite for
   ordinary WinForms controls.
5. Build a shipping configuration with instrumentation excluded. The resulting
   application has no bridge calls, bridge assembly references, or
   instrumentation-only deployment assets.

## Non-negotiable design requirements

- **Explain why before changing how.** Architectural choices and changes to
  public behavior belong in `docs/` before their implementation.
- **Keep the bridge generic.** Product-specific knowledge belongs in optional
  app configuration and adapters.
- **Observe the live UI.** Newly added, removed, replaced, and reparented controls
  must not require another application start or manual registration.
- **Separate identity from appearance and native handles.** A control instance
  has an opaque identity. Recreating its HWND does not create a new managed
  control; replacing the managed object does. A stale identity fails explicitly.
- **Respect UI thread ownership.** Discovery and control access must run on the
  thread that owns those controls. Pipe IO must not block the message loop.
- **Keep actions and screenshots honest.** A semantic button invocation is not
  a physical mouse gesture; visible desktop pixels are not an offscreen render.
  Results identify what was actually done.
- **Remove instrumentation at build time.** A runtime off switch is useful in
  instrumented builds but is not the shipping-build exclusion mechanism.
- **Keep behavior bounded and diagnosable.** Requests, traversal, screenshots,
  action retention, and protocol frames have limits and explicit errors.

## Repository map

| Location | Purpose |
| --- | --- |
| `docs/` | Rationale, architecture, protocol, behavior, build and implementation status |
| `docs/adr/` | Short records of significant decisions and their consequences |
| `src/WinFormsMcp.Instrumented.Protocol/` | Shared wire contract and framing; no WinForms or MCP SDK dependency |
| `src/WinFormsMcp.Instrumented/` | .NET Framework 4.8 in-process bridge and standard control support |
| `src/WinFormsMcp.Instrumented.Server/` | Independent .NET 10 MCP stdio server and application session management |
| `samples/InstrumentedDemo/` | SDK-style .NET Framework 4.8 application demonstrating ordinary and dynamic controls |
| `tests/` | Protocol, behavior, and build-exclusion verification |
| `eng/` | Repeatable build and verification commands |

## Reading order

Read [purpose and principles](01-purpose-and-principles.md), then
[architecture](02-architecture.md). The [protocol](03-bridge-protocol.md) and
[control behavior](04-controls-and-capture.md) make those decisions concrete.
[Integration](05-integration-and-stripping.md) explains the application hook and
shipping builds; [build and distribution](06-build-and-distribution.md) explains
the repository toolchain. Consult [implementation status](07-implementation-status.md)
for the exact boundary between the delivered draft and future work.

This repository begins as an implementation draft. Package names are proposed;
no package publication, remote repository, or production-readiness claim is
implied by the source tree.
