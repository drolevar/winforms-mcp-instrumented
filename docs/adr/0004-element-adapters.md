# ADR 0004: Non-Control elements through a public adapter seam

- Status: Accepted
- Date: 2026-11-13

## Context

The bridge identifies, walks and mutates `Control` instances only. Menu and
toolbar items, and elements of third-party control suites, are not controls and
are not reachable through `Form.Controls`. Agents cannot open a menu, click a
command or read its enabled state. Third-party suites must be supportable
without giving the open-source packages a compile-time dependency on them.

## Decision

Generalize the identity registry from `Control` to the element object. Add a
public abstract class `ElementAdapter` (namespace
`WinFormsMcp.Instrumented.Elements`) that enumerates children, describes an
element and executes named actions on the UI thread, registered with
`McpBridgeBuilder.AddElementAdapter`. Ship a built-in `ToolStripAdapter`.

Elements appear in the existing tree as `kind: "item"` nodes with the existing
`generation` + `id` and stale-target rules. `open` and `close` become values of
`invoke_control`'s `action`. No new MCP tools and no protocol version change;
all wire additions are optional fields plus the error code `not_realized`.

A third-party placement (not a shared command object) is the node. Support for a
suite is delivered outside this repository, first as application source and then
as a source-only package that compiles inside the application. The public
repository contains no suite-specific code.

Rows, cells and other virtualized items are not tree nodes. Grid support, when
added, uses a separate adapter base class with paged reads and a freshness
token.

Full design, contract and tests: [09-element-adapters.md](../09-element-adapters.md).

## Why

- The registry already uses weak object identity, so extending the key keeps the
  guarantees of ADR 0001 and the controls documentation.
- An abstract class can gain virtual members on .NET Framework 4.8; an interface
  cannot.
- A source-only adapter makes API drift a compile error in the application and
  keeps licensed assemblies out of public CI.
- Reusing `invoke_control`, `control_tree` and `inspect_control` keeps the tool
  vocabulary fixed.

## Rejected

- Menu tools addressed by path: text and position as identity.
- UI Automation from the sidecar: weak data, forces physical input.
- Declarative reflection mapping: becomes an interpreter and replaces compile
  errors with runtime strings.
- Binary adapter package referencing a suite: version-locked and not buildable
  in public CI.
- Interface instead of abstract class; protocol version 2; separate dropdown
  nodes; selectors extended to elements now.

## Consequences

- The registry and the walk, resolve, inspect and mutate paths change; the
  existing net48 suite must pass unchanged after the first step.
- `ElementAdapter` is public API and must remain stable.
- `AddAction` reserves `open` and `close`.
- Element ids go stale more often than control ids; the contract is to fail with
  `stale_target`.
- A dropdown did not remain visible while its form was not the active window in
  the plain-WinForms Windows spike (2026-11-13, measured once per condition, not
  through the bridge), so `open` is best-effort: it always reports the
  observed state, and the bridge does not activate windows. See
  [09-element-adapters.md](../09-element-adapters.md).
- The decision was implemented and validated on Windows on 2026-11-13; see
  [implementation status](../07-implementation-status.md). Control suites beyond
  `ToolStrip` stay outside this repository.
