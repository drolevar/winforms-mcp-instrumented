# DevExpress adapter examples

Two adapters that teach the bridge about DevExpress WinForms controls. They are
**source examples to copy into your application**, not part of this repository's
solution or packages: nothing here is built by CI, there is no DevExpress package
reference in this repository, and no library targeting DevExpress is published. A
prebuilt adapter would be tied to one DevExpress build and licence, and there are
many; compiling the source against the version you own avoids that.

The sources call DevExpress's public API and contain no DevExpress code or
binaries. You need your own DevExpress licence and references. They are MIT
licensed like the rest of the repository.

| File | Adapter | Seam | Needs bridge |
| --- | --- | --- | --- |
| `DevExpressGridAdapter.cs`, `DevExpressGridEditors.cs` | `devexpress.grid`: read, select, scroll and edit cells of `GridControl` | [`GridAdapter`](../../docs/10-grid-adapters.md) | `0.1.0-alpha.4` or later |
| `DevExpressBarsAdapter.cs` | `devexpress.bars`: bars and their item links (menus, toolbars, ribbon) as element nodes | [`ElementAdapter`](../../docs/09-element-adapters.md) | `0.1.0-alpha.3` or later |

## Use them

1. Copy the files into your application project and change the namespace
   (`WinFormsMcp.Examples.Adapters`) to your own. The types are `internal`.
2. Reference the DevExpress assemblies the adapters use (`DevExpress.XtraGrid`,
   `DevExpress.XtraEditors`, `DevExpress.XtraBars`, ...) and `WinFormsMcp.Instrumented`
   through the conditional `PackageReference` in the
   [Getting started](../../README.md#getting-started).
3. Register them where the bridge starts:

```csharp
#if WINFORMS_MCP_INSTRUMENTED
using (WinFormsMcp.Instrumented.McpBridge.Start(builder =>
{
    builder.AddElementAdapter(new DevExpressBarsAdapter());
    builder.AddGridAdapter(new DevExpressGridAdapter());
}))
{
    Application.Run(new MainForm());
}
#else
Application.Run(new MainForm());
#endif
```

Every file is wrapped in `#if WINFORMS_MCP_INSTRUMENTED`, so it compiles to
nothing in a build without the symbol. Prefer also making the `Compile` items
conditional on the same property, as described in the
[integration guide](../../docs/05-integration-and-stripping.md), so a stripped
build does not compile them at all.

## What the grid adapter does

- **Scope:** a `GridControl` whose main view is a `GridView` or `BandedGridView`,
  including wrapper classes that derive from `GridControl`. A detail view, a grouped
  view, a server-mode view, a master-detail grid and other view types (card, layout)
  are refused as unsupported; vertical grids and tree lists are not covered.
- **Reading:** cell text is the display text a user sees. Rows are the data rows in
  the current sort and filter; columns are indexes in `view.Columns`, hidden columns
  included and reported with `visible: false`. Column metadata comes from the
  column's editor (value kind, combo choices, band paths for banded views). A column
  whose editor is a password editor is reported as redacted.
- **Freshness:** the adapter signals the bridge on data-source, sort, filter,
  grouping, column and cell changes, and fingerprints the view. A plain `List<T>`
  raises no list events, so a same-count change made in code is not detected.
- **Selecting and scrolling:** focus and selection follow the grid's own selection
  mode; a vetoed focus change or a pending edit refuses the call.
- **Editing:** through the in-place editor, as a user does, including the
  application's validation. A completed receipt means the value reached the data
  object; a refused edit is a failed receipt and is rolled back. A read-only editor,
  a pending edit in the grid and an unsupported editor type are refused. Text is
  typed as a user would in the current culture; dates and durations also take
  ISO 8601; check editors accept `true/false/1/0/checked/unchecked`; combo and look-up
  editors take the display text; empty text is refused unless the editor allows null.

## Limits of these examples

- They were exercised by unit tests against DevExpress WinForms **25.2** controls
  (`net48`). Other DevExpress versions are untested, and so is every real application
  that uses these grids with custom handlers, so treat them as a starting point you
  read and adapt, not a drop-in.
- A modal dialog that the application itself shows from a validation handler cannot be
  suppressed by the adapter: the receipt stays `started` until someone answers it.
- The class comments explain each decision and the measured behaviour of DevExpress
  that led to it; read them before changing the edit path.
