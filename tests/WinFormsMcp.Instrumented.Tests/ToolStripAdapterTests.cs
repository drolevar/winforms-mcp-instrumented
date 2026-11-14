using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Elements;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>
    /// The built-in ToolStrip adapter against real MenuStrip, ToolStrip, StatusStrip and ContextMenuStrip fixtures.
    /// Shares the fixture of <see cref="BridgeIntegrationTests"/>; requires Windows and .NET Framework 4.8.
    /// </summary>
    public sealed partial class BridgeIntegrationTests
    {
        private sealed class StripFixture
        {
            internal int CsvClicks;
            internal int DisabledClicks;
            internal int HiddenClicks;
            internal int RunClicks;
            internal int CtxClicks;
            internal int ModalClicks;
            internal int UnnamedFirstClicks;
            internal int UnnamedSecondClicks;
            internal int SplitButtonClicks;
            internal int SplitClicks;

            internal Form Window = null!;
            internal MenuStrip Menu = null!;
            internal ToolStrip Tools = null!;
            internal StatusStrip Status = null!;
            internal ToolStripMenuItem File = null!;
            internal ToolStripMenuItem Export = null!;
            internal ToolStripMenuItem Csv = null!;
            internal ToolStripButton Run = null!;
            internal ToolStripTextBox SecretBox = null!;
            internal ContextMenuStrip HostMenu = null!;
            internal ContextMenuStrip SharedMenu = null!;

            internal void Build(Form window)
            {
                Window = window;
                Menu = new MenuStrip { Name = "MainMenu" };
                File = new ToolStripMenuItem("&File") { Name = "FileMenu" };
                Export = new ToolStripMenuItem("Export") { Name = "ExportMenu" };
                Csv = new ToolStripMenuItem("CSV") { Name = "CsvItem" };
                Csv.Click += (_, __) => Interlocked.Increment(ref CsvClicks);
                Export.DropDownItems.Add(Csv);
                Export.DropDownItems.Add(new ToolStripMenuItem("JSON") { Name = "JsonItem" });
                var disabled = new ToolStripMenuItem("Disabled") { Name = "DisabledItem", Enabled = false };
                disabled.Click += (_, __) => Interlocked.Increment(ref DisabledClicks);
                var hidden = new ToolStripMenuItem("Hidden") { Name = "HiddenItem", Available = false };
                hidden.Click += (_, __) => Interlocked.Increment(ref HiddenClicks);
                var save = new ToolStripMenuItem("Save") { Name = "SaveItem", ShortcutKeys = Keys.Control | Keys.S };
                var modal = new ToolStripMenuItem("Modal") { Name = "ModalItem" };
                modal.Click += (_, __) =>
                {
                    Interlocked.Increment(ref ModalClicks);
                    using (var dialog = new Form { Name = "ItemModal", Text = "Item modal", Width = 240, Height = 150 })
                    {
                        var close = new Button { Name = "ItemModalClose", Text = "Close", Top = 20 };
                        close.Click += (sender, eventArgs) => dialog.Close();
                        dialog.Controls.Add(close);
                        dialog.ShowDialog(Window);
                    }
                };
                File.DropDownItems.AddRange(new ToolStripItem[]
                {
                    Export, disabled, hidden, save, new ToolStripSeparator { Name = "FileSeparator" }, modal
                });
                var edit = new ToolStripMenuItem("Edit") { Name = "EditMenu" };
                edit.DropDownItems.Add(new ToolStripMenuItem("Word wrap")
                    { Name = "WrapItem", Checked = true, ShortcutKeyDisplayString = "Alt+Z" });
                Menu.Items.Add(File);
                Menu.Items.Add(edit);
                window.Controls.Add(Menu);
                window.MainMenuStrip = Menu;

                Tools = new ToolStrip { Name = "MainTools" };
                Run = new ToolStripButton("Run") { Name = "RunButton" };
                Run.Click += (_, __) => Interlocked.Increment(ref RunClicks);
                var view = new ToolStripDropDownButton("View") { Name = "ViewDropDown" };
                view.DropDownItems.Add(new ToolStripMenuItem("Zoom") { Name = "ZoomItem" });
                var split = new ToolStripSplitButton("Split") { Name = "SplitButton" };
                split.ButtonClick += (_, __) => Interlocked.Increment(ref SplitButtonClicks);
                split.Click += (_, __) => Interlocked.Increment(ref SplitClicks);
                split.DropDownItems.Add(new ToolStripMenuItem("Split entry") { Name = "SplitEntry" });
                SecretBox = new ToolStripTextBox { Name = "SecretBox" };
                SecretBox.TextBox.UseSystemPasswordChar = true;
                SecretBox.Text = "secret";
                var unnamedFirst = new ToolStripButton("Unnamed one");
                unnamedFirst.Click += (_, __) => Interlocked.Increment(ref UnnamedFirstClicks);
                var unnamedSecond = new ToolStripButton("Unnamed two");
                unnamedSecond.Click += (_, __) => Interlocked.Increment(ref UnnamedSecondClicks);
                Tools.Items.AddRange(new ToolStripItem[]
                {
                    Run, view, split, new ToolStripSeparator { Name = "ToolSeparator" },
                    new ToolStripLabel("Label") { Name = "ToolLabel" }, SecretBox, unnamedFirst, unnamedSecond
                });
                window.Controls.Add(Tools);

                Status = new StatusStrip { Name = "MainStatus" };
                Status.Items.Add(new ToolStripStatusLabel("Ready") { Name = "StatusLabel" });
                window.Controls.Add(Status);

                HostMenu = new ContextMenuStrip { Name = "HostMenu" };
                var ctx = new ToolStripMenuItem("Context action") { Name = "CtxItem" };
                ctx.Click += (_, __) => Interlocked.Increment(ref CtxClicks);
                var ctxSub = new ToolStripMenuItem("Context sub") { Name = "CtxSub" };
                ctxSub.DropDownItems.Add(new ToolStripMenuItem("Context leaf") { Name = "CtxLeaf" });
                HostMenu.Items.Add(ctx);
                HostMenu.Items.Add(ctxSub);
                window.Controls.Add(new Panel { Name = "CtxHost", ContextMenuStrip = HostMenu, Top = 250, Left = 100, Width = 50, Height = 30 });

                SharedMenu = new ContextMenuStrip { Name = "SharedMenu" };
                SharedMenu.Items.Add(new ToolStripMenuItem("Shared entry") { Name = "SharedEntry" });
                window.Controls.Add(new Panel { Name = "SharedHostA", ContextMenuStrip = SharedMenu, Top = 250, Left = 200, Width = 50, Height = 30 });
                window.Controls.Add(new Panel { Name = "SharedHostB", ContextMenuStrip = SharedMenu, Top = 250, Left = 300, Width = 50, Height = 30 });
            }
        }

        private static TestApplication StartWithStrips(out StripFixture fixture, Action<McpBridgeBuilder>? configure = null)
        {
            var built = new StripFixture();
            fixture = built;
            return new TestApplication(configure, built.Build);
        }

        private static JObject ControlNamed(JArray nodes, string name)
            => nodes.OfType<JObject>().Single(node => (string?)node["kind"] == "control" && (string?)node["name"] == name);

        private static string[] Capabilities(JToken node)
            => ((JArray)node["capabilities"]!).Select(c => (string)c!).ToArray();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static async Task<bool> ActivateWindowAsync(TestApplication app)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool active = false;
                await app.OnUiAsync(() =>
                {
                    app.Window.Activate();
                    if (Form.ActiveForm != app.Window)
                    {
                        // Windows only lets the foreground thread hand out activation: join its input queue first.
                        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                        uint current = GetCurrentThreadId();
                        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
                        SetForegroundWindow(app.Window.Handle);
                        if (attached) AttachThreadInput(current, foreground, false);
                    }
                    active = Form.ActiveForm == app.Window;
                });
                if (active) return true;
                await Task.Delay(100);
            }
            return false;
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public void TheToolStripAdapterNameIsReserved()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new McpBridgeBuilder().AddElementAdapter(new NamedAdapter("toolstrip")));
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task MenuToolAndStatusStripItemsAppearWithTheParentChain()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject menu = ControlNamed(tree, "MainMenu");
                JObject tools = ControlNamed(tree, "MainTools");
                JObject status = ControlNamed(tree, "MainStatus");

                JObject file = ItemNamed(tree, "FileMenu");
                Assert.AreEqual("toolstrip", (string?)file["adapter"]);
                Assert.AreEqual("menu_item", (string?)file["role"]);
                Assert.AreEqual((string?)menu["id"], (string?)file["parentId"]);
                Assert.AreEqual("&File", (string?)file["text"]);
                Assert.AreEqual(false, (bool?)file["opened"]);
                Assert.AreEqual(6, (int)file["childCount"]!);
                Assert.AreEqual(true, (bool?)file["childrenOmitted"], "A closed dropdown collapses in a window tree.");
                Assert.AreEqual(true, (bool?)file["visible"]);
                Assert.AreEqual(true, (bool?)file["available"]);
                CollectionAssert.AreEquivalent(new[] { "click", "open", "close" }, Capabilities(file));
                Assert.AreNotEqual(JTokenType.Null, file["bounds"]!.Type, "A realized item has screen bounds.");
                Assert.IsFalse(Items(tree).Any(item => (string?)item["name"] == "ExportMenu" || (string?)item["name"] == "CsvItem"),
                    "Closed subitems are not listed in a window tree.");
                Assert.AreEqual("menu_item", (string?)ItemNamed(tree, "EditMenu")["role"]);
                Assert.AreEqual(1, (int)ItemNamed(tree, "EditMenu")["childCount"]!);

                JObject run = ItemNamed(tree, "RunButton");
                Assert.AreEqual("button", (string?)run["role"]);
                Assert.AreEqual((string?)tools["id"], (string?)run["parentId"]);
                CollectionAssert.AreEqual(new[] { "click" }, Capabilities(run));
                JObject view = ItemNamed(tree, "ViewDropDown");
                Assert.AreEqual("button", (string?)view["role"]);
                CollectionAssert.AreEquivalent(new[] { "click", "open", "close" }, Capabilities(view));
                Assert.AreEqual("separator", (string?)ItemNamed(tree, "ToolSeparator")["role"]);
                Assert.AreEqual("label", (string?)ItemNamed(tree, "ToolLabel")["role"]);
                JObject statusLabel = ItemNamed(tree, "StatusLabel");
                Assert.AreEqual("label", (string?)statusLabel["role"]);
                Assert.AreEqual((string?)status["id"], (string?)statusLabel["parentId"]);
                Assert.AreEqual("Ready", (string?)statusLabel["text"]);

                // A tree rooted at an item expands its closed dropdowns: File > Export > CSV.
                JToken subtree = Success(await app.RequestAsync("tree", TargetParameters(file)));
                JArray nodes = (JArray)subtree["nodes"]!;
                Assert.AreEqual((string?)file["id"], (string?)nodes[0]["id"]);
                Assert.AreEqual((string?)menu["id"], (string?)nodes[0]["parentId"]);
                JObject export = ItemNamed(nodes, "ExportMenu");
                JObject csv = ItemNamed(nodes, "CsvItem");
                Assert.AreEqual((string?)file["id"], (string?)export["parentId"]);
                Assert.AreEqual((string?)export["id"], (string?)csv["parentId"]);
                Assert.AreEqual(false, (bool?)export["childrenOmitted"]);
                Assert.AreEqual(false, (bool?)csv["visible"], "A subitem of a closed menu is not realized.");
                Assert.AreEqual(true, (bool?)csv["available"]);
                Assert.AreEqual(JTokenType.Null, csv["bounds"]!.Type);
                Assert.AreEqual(JTokenType.Null, csv["opened"]!.Type, "An item without a dropdown has no opened state.");
                CollectionAssert.AreEqual(new[] { "click" }, Capabilities(csv));
                CollectionAssert.AreEquivalent(new[] { "click", "open", "close" }, Capabilities(export));
                Assert.AreEqual("Ctrl+S", (string?)ItemNamed(nodes, "SaveItem")["shortcut"]);
                Assert.AreEqual("separator", (string?)ItemNamed(nodes, "FileSeparator")["role"]);

                JToken edit = Success(await app.RequestAsync("tree", TargetParameters(ItemNamed(tree, "EditMenu"))));
                JObject wrap = ItemNamed((JArray)edit["nodes"]!, "WrapItem");
                Assert.AreEqual(true, (bool?)wrap["checked"]);
                Assert.AreEqual("Alt+Z", (string?)wrap["shortcut"]);
                Assert.AreEqual(false, (bool?)run["checked"], "A toolbar button reports its checked state.");
                Assert.AreEqual(JTokenType.Null, ItemNamed(tree, "ToolLabel")["checked"]!.Type, "A label has no checked state.");
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task DisabledMenuItemIsRefusedAndAvailableFalseIsHidden()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JObject file = ItemNamed(await app.TreeAsync(), "FileMenu");
                JObject disabled = await ItemUnderAsync(app, file, "DisabledItem");
                JObject hidden = await ItemUnderAsync(app, file, "HiddenItem");
                Assert.AreEqual(false, (bool?)disabled["enabled"]);
                Assert.AreEqual(false, (bool?)hidden["available"]);

                AssertFailed(await app.ActAsync("invoke", disabled), "disabled_control");
                AssertFailed(await app.ActAsync("invoke", hidden), "hidden_control");
                Assert.AreEqual(0, Volatile.Read(ref fixture.DisabledClicks));
                Assert.AreEqual(0, Volatile.Read(ref fixture.HiddenClicks));
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task NestedClosedItemClickRunsItsHandler()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JObject file = ItemNamed(await app.TreeAsync(), "FileMenu");
                JObject csv = await ItemUnderAsync(app, file, "CsvItem");

                JToken result = ActionResult(await app.ActAsync("invoke", csv));
                Assert.AreEqual(1, Volatile.Read(ref fixture.CsvClicks));
                Assert.AreEqual("click", (string?)result["action"]);
                Assert.AreEqual(true, (bool?)result["handlerReturned"]);
                Assert.AreEqual(false, (bool?)result["handlerDeferred"]);
                Assert.IsNull(result["opened"]);

                JObject run = ItemNamed(await app.TreeAsync(), "RunButton");
                ActionResult(await app.ActAsync("invoke", run, p => p["action"] = "click"));
                Assert.AreEqual(1, Volatile.Read(ref fixture.RunClicks));
                AssertFailed(await app.ActAsync("invoke", run, p => p["action"] = "open"), "unsupported_operation");

                // A split button's normal handler is ButtonClick (what a click on its button part raises).
                JObject split = ItemNamed(await app.TreeAsync(), "SplitButton");
                CollectionAssert.AreEquivalent(new[] { "click", "open", "close" }, Capabilities(split));
                ActionResult(await app.ActAsync("invoke", split));
                Assert.AreEqual(1, Volatile.Read(ref fixture.SplitButtonClicks), "ButtonClick must run.");
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        [TestCategory("Foreground")]
        public async Task OpenAndCloseATopLevelMenuAreObservedInTheTree()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                // In the plain-WinForms spike a dropdown opened by ShowDropDown did not remain visible while its
                // form was not the active window (docs/09-element-adapters.md), so this test runs with the form active.
                if (!await ActivateWindowAsync(app))
                    Assert.Inconclusive("The test window could not be activated, so an opened menu cannot stay open.");
                JObject file = ItemNamed(await app.TreeAsync(), "FileMenu");
                Assert.AreEqual(false, (bool?)file["opened"]);

                JToken opened = ActionResult(await app.ActAsync("invoke", file, p => p["action"] = "open"));
                Assert.AreEqual("open", (string?)opened["action"]);
                Assert.IsNotNull(opened["opened"], "open reports the observed state.");
                if ((bool?)opened["opened"] == false)
                {
                    // The dropdown did not stay open. If the form lost activation meanwhile, that is the environment, not the adapter.
                    bool stillActive = false;
                    await app.OnUiAsync(() => stillActive = Form.ActiveForm == app.Window);
                    if (!stillActive)
                        Assert.Inconclusive("The test window lost activation and the dropdown did not remain visible, so this run cannot judge the open action.");
                }
                Assert.AreEqual(true, (bool?)opened["opened"], "The form is active, so the dropdown opens and stays open.");
                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(file)));
                Assert.AreEqual((bool?)opened["opened"], (bool?)inspected["opened"], "A fresh inspect agrees with the receipt.");
                JArray tree = await app.TreeAsync();
                Assert.AreEqual(true, (bool?)ItemNamed(tree, "FileMenu")["opened"]);
                Assert.AreEqual(false, (bool?)ItemNamed(tree, "FileMenu")["childrenOmitted"]);
                Assert.IsTrue(Items(tree).Any(item => (string?)item["name"] == "ExportMenu"), "The children of an open menu are listed.");
                Assert.AreEqual(true, (bool?)ItemNamed(tree, "ExportMenu")["visible"]);

                JToken closed = ActionResult(await app.ActAsync("invoke", file, p => p["action"] = "close"));
                Assert.AreEqual(false, (bool?)closed["opened"]);
                Assert.AreEqual(false, (bool?)Success(await app.RequestAsync("inspect", TargetParameters(file)))["opened"]);
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task OpenOfANestedItemWhileItsParentIsClosedGivesNotRealized()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JObject file = ItemNamed(await app.TreeAsync(), "FileMenu");
                JObject export = await ItemUnderAsync(app, file, "ExportMenu");
                Assert.AreEqual(false, (bool?)export["visible"]);
                AssertFailed(await app.ActAsync("invoke", export, p => p["action"] = "open"), "not_realized");
                AssertFailed(await app.ActAsync("invoke", export, p => p["action"] = "close"), "not_realized");
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task RemovedItemBecomesStaleAndReAddingRestoresTheId()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject run = ItemNamed(tree, "RunButton");
                JObject file = ItemNamed(tree, "FileMenu");
                JObject csv = await ItemUnderAsync(app, file, "CsvItem");
                Success(await app.RequestAsync("inspect", TargetParameters(run)));
                Success(await app.RequestAsync("inspect", TargetParameters(csv)));

                await app.OnUiAsync(() =>
                {
                    fixture.Tools.Items.Remove(fixture.Run);
                    fixture.Export.DropDownItems.Remove(fixture.Csv);
                });
                await AssertStaleAsync(app, "inspect", run);
                await AssertStaleAsync(app, "inspect", csv);
                Assert.IsFalse(Items(await app.TreeAsync()).Any(item => (string?)item["name"] == "RunButton"));
                AssertFailed(await app.ActAsync("invoke", run), "stale_target");
                Assert.AreEqual(0, Volatile.Read(ref fixture.RunClicks));

                await app.OnUiAsync(() =>
                {
                    fixture.Tools.Items.Insert(0, fixture.Run);
                    fixture.Export.DropDownItems.Insert(0, fixture.Csv);
                });
                Assert.AreEqual("Run", (string?)Success(await app.RequestAsync("inspect", TargetParameters(run)))["text"]);
                Assert.AreEqual("CSV", (string?)Success(await app.RequestAsync("inspect", TargetParameters(csv)))["text"]);
                Assert.AreEqual((string?)run["id"], (string?)ItemNamed(await app.TreeAsync(), "RunButton")["id"]);
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task ItemDisposedWithItsStripIsStale()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject run = ItemNamed(tree, "RunButton");
                JObject file = ItemNamed(tree, "FileMenu");
                JObject csv = await ItemUnderAsync(app, file, "CsvItem");

                await app.OnUiAsync(() =>
                {
                    fixture.Window.Controls.Remove(fixture.Tools);
                    fixture.Tools.Dispose();
                    fixture.Window.Controls.Remove(fixture.Menu);
                    fixture.Menu.Dispose();
                });
                await AssertStaleAsync(app, "inspect", run);
                await AssertStaleAsync(app, "inspect", file);
                await AssertStaleAsync(app, "inspect", csv);
                await AssertStaleAsync(app, "tree", csv);
                Assert.IsFalse(Items(await app.TreeAsync()).Any(item => (string?)item["name"] == "RunButton" || (string?)item["name"] == "FileMenu"));
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task AContextMenuStripReachedThroughAControlAppearsOnceAndItsItemsAreClickable()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject host = ControlNamed(tree, "CtxHost");
                Assert.AreEqual(1, Items(tree).Count(item => (string?)item["name"] == "HostMenu"), "The strip appears once.");
                JObject strip = ItemNamed(tree, "HostMenu");
                Assert.AreEqual("toolstrip", (string?)strip["adapter"]);
                Assert.AreEqual("context_menu", (string?)strip["role"]);
                Assert.AreEqual((string?)host["id"], (string?)strip["parentId"]);
                CollectionAssert.AreEqual(new[] { "close" }, Capabilities(strip));
                Assert.AreEqual(false, (bool?)strip["opened"]);
                Assert.AreEqual(2, (int)strip["childCount"]!);
                Assert.AreEqual(true, (bool?)strip["childrenOmitted"], "A closed context menu collapses in a window tree.");
                Assert.IsFalse(Items(tree).Any(candidate => (string?)candidate["name"] == "CtxItem" || (string?)candidate["name"] == "CtxSub"));
                JObject item = await ItemUnderAsync(app, strip, "CtxItem");
                Assert.AreEqual((string?)strip["id"], (string?)item["parentId"]);
                Assert.AreEqual((string?)strip["id"], (string?)(await ItemUnderAsync(app, strip, "CtxSub"))["parentId"]);

                // The strip is resolvable by id; the control-only stale check does not apply to it.
                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(strip)));
                Assert.AreEqual("item", (string?)inspected["kind"]);
                Assert.AreEqual("HostMenu", (string?)inspected["name"]);
                Assert.IsNull(inspected["items"], "A strip is an element: it is described only.");
                Success(await app.RequestAsync("inspect", TargetParameters(item)));
                JToken rooted = Success(await app.RequestAsync("tree", TargetParameters(strip)));
                JArray rootedNodes = (JArray)rooted["nodes"]!;
                Assert.AreEqual((string?)strip["id"], (string?)rootedNodes[0]["id"]);
                Assert.AreEqual("item", (string?)rootedNodes[0]["kind"]);
                Assert.IsTrue(rootedNodes.OfType<JObject>().Any(node => (string?)node["name"] == "CtxLeaf"), "A tree rooted at the strip expands its subtree.");

                // Control-only operations refuse it as an element.
                JToken receipt = await app.ActAsync("set_text", strip, p => p["text"] = "x");
                AssertFailed(receipt, "unsupported_operation", "This operation applies to controls, not elements.");
                AssertFailed(await app.ActAsync("invoke", strip, p => p["action"] = "close"), "not_realized");

                ActionResult(await app.ActAsync("invoke", item));
                Assert.AreEqual(1, Volatile.Read(ref fixture.CtxClicks));

                // Clearing the assignment makes the strip and its items stale.
                await app.OnUiAsync(() => app.Window.Controls["CtxHost"]!.ContextMenuStrip = null);
                await AssertStaleAsync(app, "inspect", strip);
                await AssertStaleAsync(app, "inspect", item);
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task ASharedContextMenuStripAssignedToTwoControlsAppearsOnce()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                Assert.AreEqual(1, Items(tree).Count(item => (string?)item["name"] == "SharedMenu"));
                JObject shared = ItemNamed(tree, "SharedMenu");
                Assert.AreEqual(true, (bool?)shared["childrenOmitted"]);
                Assert.AreEqual(1, (int)shared["childCount"]!);
                JToken rooted = Success(await app.RequestAsync("tree", TargetParameters(shared)));
                Assert.AreEqual(1, Items((JArray)rooted["nodes"]!).Count(item => (string?)item["name"] == "SharedEntry"));
                Assert.AreEqual((string?)ControlNamed(tree, "SharedHostA")["id"], (string?)ItemNamed(tree, "SharedMenu")["parentId"],
                    "The first control that references the strip owns its node.");
                string[] ids = tree.OfType<JObject>().Select(node => (string)node["id"]!).ToArray();
                Assert.AreEqual(ids.Length, ids.Distinct().Count());
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task AHostedControlStaysAControlNodeAndAHostedPasswordBoxDoesNotLeak()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject host = ItemNamed(tree, "SecretBox");
                Assert.AreEqual("host", (string?)host["role"]);
                Assert.AreEqual(JTokenType.Null, host["text"]!.Type, "A host item never reports its hosted control's text.");
                string? hostedId = (string?)host["hostedControlId"];
                Assert.IsNotNull(hostedId);
                JObject hosted = tree.OfType<JObject>().Single(node => (string?)node["id"] == hostedId);
                Assert.AreEqual("control", (string?)hosted["kind"]);
                Assert.AreEqual(true, (bool?)hosted["textRedacted"]);

                var seen = new System.Text.StringBuilder(tree.ToString());
                foreach (JObject node in tree.OfType<JObject>())
                    seen.Append(Success(await app.RequestAsync("inspect", TargetParameters(node))).ToString());
                seen.Append(Success(await app.RequestAsync("tree", TargetParameters(ControlNamed(tree, "MainTools")))).ToString());
                seen.Append(Success(await app.RequestAsync("tree", TargetParameters(host))).ToString());
                Assert.IsFalse(seen.ToString().Contains("secret"), "No tree or inspect output may contain the hosted password.");
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task ItemsWithAnEmptyNameAreEmittedAndTargetableById()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JArray tree = await app.TreeAsync();
                JObject[] unnamed = Items(tree).Where(item => ((string?)item["text"])?.StartsWith("Unnamed", StringComparison.Ordinal) == true).ToArray();
                Assert.AreEqual(2, unnamed.Length);
                Assert.AreNotEqual((string?)unnamed[0]["id"], (string?)unnamed[1]["id"]);
                JObject second = unnamed.Single(item => (string?)item["text"] == "Unnamed two");
                Assert.AreEqual("Unnamed two", (string?)Success(await app.RequestAsync("inspect", TargetParameters(second)))["text"]);
                ActionResult(await app.ActAsync("invoke", second));
                Assert.AreEqual(0, Volatile.Read(ref fixture.UnnamedFirstClicks));
                Assert.AreEqual(1, Volatile.Read(ref fixture.UnnamedSecondClicks));
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task ABigMenuIsTruncatedWithTheTruncatedFlag()
        {
            var app = new TestApplication(null, window =>
            {
                var strip = new ToolStrip { Name = "BigStrip" };
                var buttons = new ToolStripItem[2_500];
                for (int index = 0; index < buttons.Length; index++)
                    buttons[index] = new ToolStripButton("Big " + index) { Name = "Big" + index };
                strip.SuspendLayout();
                strip.Items.AddRange(buttons);
                strip.ResumeLayout(false);
                window.Controls.Add(strip);
            });
            using (app)
            {
                JToken result = Success(await app.RequestAsync("tree"));
                Assert.AreEqual(true, (bool)result["truncated"]!);
                Assert.IsTrue((int)result["nodeCount"]! <= 2000, "nodeCount was " + result["nodeCount"]);
                JArray nodes = (JArray)result["nodes"]!;
                string stripId = (string)ControlNamed(nodes, "BigStrip")["id"]!;
                Assert.AreEqual(1_000, nodes.OfType<JObject>().Count(node => (string?)node["parentId"] == stripId),
                    "A strip with 2,500 items is cut at 1,000 children per parent.");
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task AnExcludedContextMenuStripAndItsItemsDisappearAndTheirOldIdsAreStale()
        {
            bool hideMenu = false;
            bool throwOnUnparented = false;
            using (var app = StartWithStrips(out StripFixture fixture, options => options.Exclude(control =>
            {
                if (throwOnUnparented && control.Parent == null && !(control is Form))
                    throw new InvalidOperationException("Predicate written for parented controls.");
                return hideMenu && control.Name == "HostMenu";
            })))
            {
                JArray tree = await app.TreeAsync();
                JObject strip = ItemNamed(tree, "HostMenu");
                JObject item = await ItemUnderAsync(app, strip, "CtxItem");
                Success(await app.RequestAsync("inspect", TargetParameters(item)));

                await app.OnUiAsync(() => hideMenu = true);
                JArray hidden = await app.TreeAsync();
                Assert.IsFalse(Items(hidden).Any(candidate => (string?)candidate["name"] == "HostMenu"), "The excluded strip is not listed.");
                Assert.IsTrue(Items(hidden).Any(candidate => (string?)candidate["name"] == "SharedMenu"), "Other strips are unaffected.");
                await AssertStaleAsync(app, "inspect", strip);
                await AssertStaleAsync(app, "inspect", item, "The items of an excluded strip are stale too.");
                await AssertStaleAsync(app, "tree", strip);
                AssertFailed(await app.ActAsync("invoke", item), "stale_target");
                Assert.AreEqual(0, Volatile.Read(ref fixture.CtxClicks));

                await app.OnUiAsync(() => hideMenu = false);
                Assert.AreEqual((string?)strip["id"], (string?)ItemNamed(await app.TreeAsync(), "HostMenu")["id"]);

                // A predicate that throws on an unparented control fails closed: every strip is hidden.
                await app.OnUiAsync(() => throwOnUnparented = true);
                JArray failedClosed = await app.TreeAsync();
                Assert.AreEqual(0, Items(failedClosed).Count(candidate => (string?)candidate["role"] == "context_menu"));
                await AssertStaleAsync(app, "inspect", strip);
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task ModalDialogOpenedByAMenuItemKeepsTheReceiptStarted()
        {
            using (var app = StartWithStrips(out StripFixture fixture))
            {
                JObject file = ItemNamed(await app.TreeAsync(), "FileMenu");
                JObject modal = await ItemUnderAsync(app, file, "ModalItem");
                JToken opening = Success(await app.RequestAsync("invoke", TargetParameters(modal)));
                JObject close = await app.WaitForNodeAsync("ItemModalClose");
                JToken pending = Success(await app.RequestAsync("action_status", ReceiptParameters(opening)));
                Assert.AreEqual("started", (string?)pending["state"]);
                Assert.AreEqual(1, Volatile.Read(ref fixture.ModalClicks));

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(close)));
                await app.WaitForCompletionAsync(closing);
                await app.WaitForCompletionAsync(opening);
                Assert.IsFalse((await app.TreeAsync()).Any(n => (string?)n["name"] == "ItemModal"));
            }
        }

        [TestMethod]
        [TestCategory("ToolStripAdapter")]
        public async Task AnApplicationAdapterIsConsultedBeforeTheBuiltInOne()
        {
            using (var app = StartWithStrips(out StripFixture fixture, options => options.AddElementAdapter(new ClaimingAdapter())))
            {
                JArray tree = await app.TreeAsync();
                JObject run = ItemNamed(tree, "RunButton");
                Assert.AreEqual("claimer", (string?)run["adapter"], "The first adapter to emit an element owns it.");
                Assert.AreEqual(1, Items(tree).Count(item => (string?)item["name"] == "RunButton"));
                Assert.AreEqual("toolstrip", (string?)ItemNamed(tree, "FileMenu")["adapter"]);
            }
        }

        private sealed class ClaimingAdapter : ElementAdapter
        {
            internal ClaimingAdapter() : base("claimer") { }

            public override void GetChildren(object parent, ElementSink sink)
            {
                if (parent is ToolStrip strip && strip.Name == "MainTools" && strip.Items["RunButton"] != null)
                    sink.Add(strip.Items["RunButton"]!);
            }

            public override bool IsAlive(object element) => !((ToolStripItem)element).IsDisposed;

            public override void Describe(object element, ElementDescription description)
            {
                description.Role = "claimed";
                description.Name = ((ToolStripItem)element).Name;
            }
        }
    }
}
