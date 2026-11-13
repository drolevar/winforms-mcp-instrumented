# Working on WinForms MCP Instrumented

## Read before changing behavior

Read `docs/00-overview.md`, `docs/02-architecture.md`, and
`docs/07-implementation-status.md` first. Consult the protocol and control
behavior documents before changing either side of the pipe. Explain the problem,
the reason for a decision, and its consequences in `docs/` before implementing a
change to architecture or public behavior. Keep the README a useful entry point.

This is an application instrumentation project. Preserve its defining boundaries:

- The application bridge targets .NET Framework 4.8. The MCP sidecar targets
  .NET 10. Modern MCP and hosting packages belong only to the sidecar.
- The protocol project targets .NET Standard 2.0 and has no WinForms/MCP SDK
  dependency. Its JSON dependency is intentional and centrally versioned.
- Standard controls work without app-specific registration. Builder extensions
  are optional. State an unsupported control capability explicitly.
- Access managed controls only on their owning UI thread. Keep pipe work off
  that thread. Never synchronously wait on a UI action that can open a modal
  dialog; return an action receipt and allow further requests.
- Identity belongs to the managed control instance and bridge generation, not
  its HWND, text, Name, or position. Never silently retarget a stale ID.
- Do not add arbitrary code evaluation or unrestricted property/method
  reflection as an automation shortcut.
- Limits, errors, capture modes, and implemented scope must remain explicit.

## Build and dependency rules

Use SDK-style projects and `WinFormsMcp.Instrumented.slnx`. Put dependency
versions only in `Directory.Packages.props`. Use MinVer for release versions;
the wire protocol version is a separate compatibility decision. Keep build
dependencies private. Do not invent a package publication or repository URL.

`EnableWinFormsMcpInstrumentation` controls the consuming application's source
and dependency graph. It must never turn the bridge library's own Release
package into a no-op. A disabled consumer build must have no instrumentation
assembly references or instrumentation-only deployment files. Apply the same
properties to restore and build; isolate build outputs between variants.

Typical Windows development commands (PowerShell 7, .NET 10 SDK, .NET 4.8):

```powershell
./eng/build.ps1
```

Use the exact commands in `docs/06-build-and-distribution.md` when building an
individual project or a source archive. Windows execution is required for the
WinForms tests; visible-screen capture additionally needs an interactive desktop.

## Validation and reporting

Test meaningful behavior: malformed framing, bounds, stale identities,
replacement versus handle recreation, modal progress, and build stripping.
Keep tests deterministic and explain Windows-only requirements. A successful
cross-compile does not establish that UI behavior worked on Windows.

When adding a capability, update the implementation status and relevant tests.
When a verification cannot run, record the command and concrete reason. Do not
report a planned or skipped test as passed. Avoid editing unrelated files.

Keep stdout reserved for MCP in the sidecar. Diagnostics go to stderr or bounded
tool results. Never log the pipe token. Preserve current-user pipe access and
explicit authentication; deserialize with type-name activation disabled.

Use small cohesive types, explicit error handling, and cancellation-aware IO.
Prefer the existing style and bounded APIs over new dependency chains. Format
C# with four spaces and use nullable annotations. Keep the bridge compatible
with its runtime even though it is compiled by a modern SDK.

The project is MIT licensed. Preserve license notices. Creating tags, publishing
packages, and changing external repositories are separate delivery actions and
should happen only when included in the user's requested work.


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:1105d646 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/core-concepts/sync-concepts.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->
