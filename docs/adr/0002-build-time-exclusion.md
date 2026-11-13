# ADR 0002: Instrumentation can be removed from a build completely

- Status: accepted design
- Date: 2026-11-13
- Product: WinForms MCP Instrumented

## Context

Instrumentation is useful during development and investigation. A shipping
application must also be buildable without the bridge, its integration code,
or its deployment assets. Merely deciding not to start a listener would leave
those dependencies and code paths present.

The integration must remain small enough to fit an existing WinForms
application. It must also allow instrumentation of optimized Release code when
diagnosing an optimization-dependent problem.

## Decision

Use one consuming-project MSBuild property:
`EnableWinFormsMcpInstrumentation`.

| Setting | Behavior |
| --- | --- |
| Unset in Debug | Include instrumentation. |
| Unset in any other configuration | Exclude instrumentation. |
| Explicit `true` | Include instrumentation regardless of optimization/configuration. |
| Explicit `false` | Exclude instrumentation regardless of configuration. |

The property conditions the bridge package/project reference and any optional
instrumentation-only references. It also derives the compiler symbol
`WINFORMS_MCP_INSTRUMENTED`. The symbol is not an independent user setting.

All application-owned startup, builder, adapter, and domain-extension code that
depends on instrumentation must be excluded from compilation when the property
is false. A dedicated integration file makes this manageable. The package must
not require instrumentation attributes on production form or business types.

The supported simple integration uses `PackageReference` in SDK-style projects.
A traditional .NET Framework project can also use PackageReference; a
`packages.config` integration needs separate conditional-reference and asset
handling and is not automatically covered by the same recipe.

## Why

Controlling both dependencies and source compilation makes the requirement
observable in the produced binaries. It avoids relying on runtime branches,
optimization, or dead-code removal to infer what will be shipped.

Keeping the switch separate from `DEBUG` allows an instrumented optimized build
without changing optimization or tracing policy. Defaulting unknown
configurations to off also gives custom shipping configurations predictable
behavior.

The consuming project must own the conditional reference. A package's own build
assets cannot reliably decide that the package should disappear from the
restore graph. NuGet's
[package build guidance](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets)
specifically excludes restore-affecting items such as `PackageReference` from
the changes package build files should make.

## Consequences

A stripped build must have no instrumentation bootstrap or type references in
its compiled app assemblies, no instrumentation packages in its evaluated
dependency graph, and no instrumentation-owned assets in its deployment.
Dependencies independently needed by the application remain.

Runtime activation cannot enable a stripped build. Including instrumentation
does not itself require activating its endpoint; build inclusion and runtime
activation are two separate decisions.

Changing the flag changes the package graph. Restore and build must use the
same configuration and property values. Use MSBuild's `/restore` option for a
combined invocation, following Microsoft's
[restore/build guidance](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets#restoring-and-building-with-one-msbuild-command).
Separate or clean intermediate/output locations prevent a previous
instrumented build from contaminating a shipping build.

## Validation criteria

The package's sample and build checks cover Debug/enabled, Release/disabled,
and Release/enabled. A stripped-output check runs outside the excluded bridge
package so that it still executes when the package is absent.

Checks inspect the evaluated references, compiled assembly references, and
actual deployment files. The matrix includes an enabled-to-disabled transition
to detect stale artifacts. Starting a stripped sample with instrumentation
environment variables must not create a bridge endpoint.

These are acceptance criteria. A successful build of one configuration alone
does not establish that the stripping contract has been validated.
