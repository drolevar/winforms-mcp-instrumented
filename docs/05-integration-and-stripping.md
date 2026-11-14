# Application integration and complete build-time exclusion

## Why inclusion and activation are separate

A developer needs two independent decisions. First, does this binary contain
instrumentation at all? Second, should an instrumented process open an endpoint
for this particular run? Build properties answer the first question. The bridge
activation settings answer the second.

An environment variable cannot restore code and dependencies excluded from a
shipping build. Conversely, an optimized build can include instrumentation when
the developer deliberately needs to investigate Release-only behavior.

## Application startup

Keep the normal STA WinForms startup. Call the bridge on that thread before
`Application.Run`, and dispose it after the message loop ends:

```csharp
[STAThread]
private static void Main()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

#if WINFORMS_MCP_INSTRUMENTED
    using (WinFormsMcp.Instrumented.McpBridge.Start())
    {
        Application.Run(new MainForm());
    }
#else
    Application.Run(new MainForm());
#endif
}
```

The bridge reads `WINFORMS_MCP_PIPE` and `WINFORMS_MCP_TOKEN`. The sidecar supplies
them to an application it launches. Without activation settings, the default
startup hook is inert. The bridge uses a current-user pipe ACL and authenticates
requests; the token must not be put in a repository, logged, or embedded in a
published MCP configuration.

Standard control inspection and actions are automatic. Optional builder
configuration belongs inside the guarded startup branch or in a wholly
conditional source file. Do not spread bridge types through production method
signatures. This makes the exclusion boundary easy to understand and verify.

The first draft supports one owning WinForms UI thread per bridge. It must not
read controls on another UI thread. See the implementation status for the exact
boundary; additional UI dispatchers require a deliberate extension.

## Menus, element adapters and exclusion

Menu, toolbar, status-strip and context-menu items are discovered automatically
by the built-in `ToolStripAdapter`; they need no registration. Support for a
third-party control suite is an `ElementAdapter` that the application registers
with `McpBridgeBuilder.AddElementAdapter` in the guarded startup branch (see
[element adapters](09-element-adapters.md)).

- **Exclusion is by an excluded ancestor control only.** The builder's exclusion
  predicate applies to controls. An element disappears from trees, and its old ids
  become `stale_target`, when a control above it is excluded. There is no way to
  hide one menu command or one element while keeping its window instrumented. An
  application that must not expose a command to an agent should not instrument
  that window, or should keep the command in a control that it excludes. An
  element that is itself a control without a parent, such as a
  `ContextMenuStrip`, is hidden together with its items when the predicate
  excludes it; a predicate that throws on such a control hides it too.
- **A source-compiled adapter must be stripped with the rest of the
  instrumentation.** An adapter delivered as source compiles into the application
  assembly, so a stripped build contains whatever part of it is not conditional.
  Put the adapter files and their registration call under the same
  `WINFORMS_MCP_INSTRUMENTED` condition (or a conditional `Compile` item), and keep
  the adapter types `internal`. The stripping verifier checks assembly
  references, dependency assets and configuration; compiled-in source (a helper
  class that reads a suite's internals, for example) does not appear as an
  assembly reference, so the verifier cannot show that it was removed. This
  repository's verifier does not assert that no adapter types are present; an
  application that ships an adapter should add that check for its own types.

## Conditional NuGet reference

The sample consumes a project reference for repository development. A packaged
consumer uses the same condition on a NuGet reference. In a project using Central
Package Management, put the released version in `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Replace the illustrative version with the version you built/published. -->
    <PackageVersion Include="WinFormsMcp.Instrumented" Version="0.1.0-alpha.0" />
  </ItemGroup>
</Project>
```

Then add these groups to the application project after existing configuration
property groups:

```xml
<PropertyGroup>
  <RestoreProjectStyle>PackageReference</RestoreProjectStyle>
  <EnableWinFormsMcpInstrumentation
    Condition="'$(EnableWinFormsMcpInstrumentation)' == '' and '$(Configuration)' == 'Debug'">true</EnableWinFormsMcpInstrumentation>
  <EnableWinFormsMcpInstrumentation
    Condition="'$(EnableWinFormsMcpInstrumentation)' == ''">false</EnableWinFormsMcpInstrumentation>
  <DefineConstants
    Condition="'$(EnableWinFormsMcpInstrumentation)' == 'true'">$(DefineConstants);WINFORMS_MCP_INSTRUMENTED</DefineConstants>
</PropertyGroup>
<ItemGroup Condition="'$(EnableWinFormsMcpInstrumentation)' == 'true'">
  <PackageReference Include="WinFormsMcp.Instrumented" />
</ItemGroup>
```

For a project without Central Package Management, put `Version` on the
`PackageReference` instead. A .NET Framework 4.8 application can use
`PackageReference` without changing its runtime. Existing `packages.config`
projects need a separate migration/conditional-reference assessment; do not mix
the two dependency models casually.

The consuming project owns this condition because it must be evaluated during
restore. A package's imported build targets cannot reliably remove that package
from the dependency graph that brought the targets into the build.

## Expected variants

| Configuration | Property | Result |
| --- | --- | --- |
| Debug | Unset | Bridge included, endpoint still requires activation settings |
| Release | Unset | Instrumentation excluded |
| Any | `false` | Instrumentation excluded |
| Any | `true` | Instrumentation included |

The bridge library project itself remains functional when built in Release.
The property controls the consuming app, not the contents of the bridge NuGet
package.

## Shipping verification

Restore and build with identical property values. Avoid reusing an assets file
restored for the opposite variant. Use separate output/intermediate directories
or a clean checkout and a clean deployment staging directory.

A stripped result means:

- no bridge/protocol reference in the consuming app's assembly metadata;
- no bridge/protocol package in the consuming app's evaluated dependency graph;
- no instrumentation-only binaries, configuration, adapters, or bootstrap code
  in deployed output;
- no pipe listener or control tracking to enable at runtime.

Dependencies independently used by the app remain. The sidecar is a separate
developer tool and is never a reference from the shipping application.

The repository's verification script inspects outputs and metadata. The package
test matrix also needs a clean consumer restore from the packed NuGet files:
project-reference behavior alone cannot prove package dependency behavior.

## Sources

- [PackageReference project support and restore style](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- [Conditional package references by configuration](https://learn.microsoft.com/en-us/nuget/consume-packages/migrate-packages-config-to-package-reference)
- [C# conditional compilation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/preprocessor-directives)
- [NuGet package build-file constraints](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets)
- [Restore and build with one MSBuild command](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets#restoring-and-building-with-one-msbuild-command)
