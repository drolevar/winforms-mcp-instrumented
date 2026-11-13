# ADR 0001: External MCP server and an in-process WinForms bridge

- Status: accepted design
- Date: 2026-11-13
- Product: WinForms MCP Instrumented

## Context

We need reusable MCP access to an existing .NET Framework 4.8 WinForms
application. Standard inspection and control operations should work with one
startup integration. The agent must also be able to continue working while the
application exits, crashes, or is rebuilt.

Some C# MCP packages provide .NET Standard targets. That makes an in-process
MCP implementation technically plausible, but package compatibility is only
one requirement. Hosting the agent-facing session inside the application would
still bind that session to the application's process and dependency graph.
Package target frameworks can be checked on the official NuGet pages for
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
and [`ModelContextProtocol.Core`](https://www.nuget.org/packages/ModelContextProtocol.Core).

## Decision

Run MCP hosting in an independent modern .NET console process. Deliver it as a
separately installed tool. The WinForms application references only its bridge
and the bridge's required dependencies.

The bridge owns access to managed controls and executes operations on their
owning UI thread. A private, versioned named-pipe protocol connects the server
to the bridge. The server owns MCP transport, stable standard tool descriptions,
and conversion of bridge results to MCP responses.

The shared protocol project targets .NET Standard 2.0 and must not depend on
WinForms or MCP SDK types. Application-specific behavior is registered inside
the application through explicit extension points.

## Why

An external server can remain connected to the agent while the application is
unavailable. A new bridge connection gets a new generation, so stale controls
cannot be mistaken for controls in the replacement process.

The split isolates the rapidly changing protocol host from a mature application
runtime. It also keeps deployment understandable: a production application
does not need to load development tooling simply because the two packages share
a product name.

A named pipe fits the local Windows process boundary without requiring a
loopback HTTP server, HTTP lifecycle handling, or a custom Streamable HTTP
adapter inside the application. Connection access and peer authentication remain
explicit responsibilities; the name of a pipe is not an access policy.

## Consequences

There are two processes and an additional protocol to maintain. Compatibility,
disconnects, bounded framing, authentication, timeouts, and uncertain mutation
outcomes must be designed and tested. The bridge cannot report information it
did not transmit before a crash.

The server must not accidentally recreate in-process coupling. It cannot retain
live control objects, use control handles as permanent IDs, or depend on a
particular application's assemblies. Standard tools stay stable while control
descriptions change as data.

This decision does not require every future application target to use named
pipes, but another transport requires a concrete use case and an explicit
compatibility decision. It must not enlarge the default network exposure.

## Alternatives considered

- **In-process MCP host:** fewer processes, but application failure also removes
  the agent-facing server and adds MCP hosting dependencies to the application.
- **UI Automation as the primary model:** avoids an application reference, but
  does not provide the deliberate managed-object integration and domain
  extensions that define this product.
- **Application-specific MCP server:** can deliver a quick integration, but
  duplicates transport, lifecycle, discovery, and safety behavior across apps.

## Validation criteria

A sample application can disconnect and reconnect without ending the MCP
server. References from the previous generation fail explicitly. The app has
no reference to the server project or MCP SDK. The private protocol can reject
an incompatible peer before executing application operations.
