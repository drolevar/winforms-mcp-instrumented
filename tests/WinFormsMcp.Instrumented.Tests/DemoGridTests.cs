using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using InstrumentedDemo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>
    /// The sample application's owner-drawn <c>DemoGrid</c> and its <c>DemoGridAdapter</c> (compiled into this assembly
    /// from samples/InstrumentedDemo), driven through the bridge's wire exactly as the desktop exercise drives the demo.
    /// Shares the fixture of <see cref="BridgeIntegrationTests"/>; requires Windows and .NET Framework 4.8.
    /// </summary>
    public sealed partial class BridgeIntegrationTests
    {
        // The demo's own registration (the grid adapter and the show_context_menu action), so that a drifting
        // Program.cs cannot hide a broken adapter.
        private static TestApplication StartWithDemoGrid()
            => new TestApplication(
                DemoGridAdapter.RegisterWith,
                window => window.Controls.Add(new DemoGrid { Name = "demoGrid", Left = 0, Top = 0, Width = 330, Height = 220 }));

        private static Task OnDemoGridAsync(TestApplication app, Action<DemoGrid> action)
            => app.OnUiAsync(() => action((DemoGrid)app.Window.Controls["demoGrid"]!));

        private static async Task<T> FromDemoGridAsync<T>(TestApplication app, Func<DemoGrid, T> read)
        {
            T value = default!;
            await OnDemoGridAsync(app, grid => value = read(grid));
            return value;
        }

        private static async Task<JToken> StartSetCellAsync(TestApplication app, JObject grid, int row, int column, string text, string token)
        {
            JObject parameters = TargetParameters(grid);
            Edit(row, column, text, token)(parameters);
            return Success(await app.RequestAsync("set_cell", parameters));
        }

        private static void AssertInvokeCompleted(JToken status, string because)
            => Assert.AreEqual("completed", (string?)status["state"], because + ": " + status);

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task TheDemoGridIsAdvertisedAndReadThroughTheDemoAdapter()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject node = await app.FindAsync("demoGrid");
                Assert.AreEqual("demo-grid", (string?)node["gridAdapter"]);
                CollectionAssert.IsSubsetOf(new[] { "read_grid", "select", "scroll", "set_cell", "show_context_menu" }, Capabilities(node));
                JArray adapters = (JArray)Success(await app.RequestAsync("hello"))["gridAdapters"]!;
                CollectionAssert.AreEqual(new[] { "demo-grid", "datagridview" }, adapters.Select(name => (string)name!).ToArray());

                JObject page = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("demo-grid", (string?)page["gridAdapter"]);
                StringAssert.Matches((string)page["viewToken"]!, new Regex(@"^[0-9a-f]{16}\.[0-9]+$"));
                Assert.AreEqual(12, (int)page["totalRows"]!);
                Assert.AreEqual(3, (int)page["totalColumns"]!);
                Assert.AreEqual(JTokenType.Null, page["focusedRow"]!.Type, "Nothing is focused yet.");

                JArray columns = (JArray)page["columns"]!;
                CollectionAssert.AreEqual(new[] { "Item", "Quantity", "Reserved" }, columns.Select(c => (string)c["header"]!).ToArray());
                CollectionAssert.AreEqual(new[] { false, true, false }, columns.Select(c => (bool)c["editable"]!).ToArray());
                CollectionAssert.AreEqual(new[] { "text", "number", "number" }, columns.Select(c => (string)c["valueKind"]!).ToArray());
                Assert.AreEqual(JTokenType.Null, columns[0]["bandPath"]!.Type, "The first column has no band.");
                CollectionAssert.AreEqual(new[] { "Stock", "On hand" }, ((JArray)columns[1]["bandPath"]!).Select(b => (string)b!).ToArray());
                CollectionAssert.AreEqual(new[] { "Stock", "Held" }, ((JArray)columns[2]["bandPath"]!).Select(b => (string)b!).ToArray());
                CollectionAssert.AreEqual(new[] { "Anvil", "30", "5" }, ((JArray)page["rows"]![0]!["cells"]!).Select(c => (string)c!).ToArray());
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task SelectingACellOfTheDemoGridFocusesItAndMarksOnlyThatRowSelected()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");

                AssertGridCompleted(await app.ActAsync("select", grid, Cell(3, 1, token)), "a select with the current token");

                JObject page = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual(3, (int)page["focusedRow"]!);
                Assert.AreEqual(1, (int)page["focusedColumn"]!);
                bool[] selected = page["rows"]!.Select(row => (bool)row["selected"]!).ToArray();
                CollectionAssert.AreEqual(Enumerable.Range(0, 12).Select(row => row == 3).ToArray(), selected);
                Assert.AreEqual(token, (string)page["viewToken"]!, "A selection does not change the view.");

                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(11, null, token)), "scrolling keeps the token and the selection");
                Assert.AreEqual(3, (int)(await ReadGridAsync(app, "demoGrid"))["focusedRow"]!);
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task SetGridCellOnTheDemoGridCommitsThroughTheEditorAndChangesTheToken()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "11", token));

                AssertGridCompleted(status, "an edit of the quantity column");
                Assert.AreEqual(true, (bool?)status["result"]!["committed"]);
                Assert.AreEqual("11", (string?)status["result"]!["displayText"]);
                Assert.AreEqual(1, (int?)status["result"]!["rowAfter"]);
                string after = (string)status["result"]!["viewToken"]!;
                Assert.AreNotEqual(token, after, "The edit changed the data the grid shows.");

                JObject page = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("11", CellOf(page, 1, 1));
                Assert.AreEqual(after, (string)page["viewToken"]!);
                Assert.AreEqual(1, (int)page["focusedRow"]!, "The edit focused the cell as a user would.");
                Assert.IsFalse(await FromDemoGridAsync(app, demo => demo.IsEditing), "The editor was closed.");
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task AnEditOnTheSortedColumnReportsWhereTheRowWent()
        {
            using (var app = StartWithDemoGrid())
            {
                await OnDemoGridAsync(app, demo => demo.Sort(DemoGrid.QuantityColumn, descending: false));
                JObject grid = await app.FindAsync("demoGrid");
                JObject before = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("Lathe", CellOf(before, 0, 0));

                JToken status = await app.ActAsync("set_cell", grid, Edit(0, 1, "100", (string)before["viewToken"]!));

                AssertGridCompleted(status, "an edit that re-sorts the view");
                Assert.AreEqual(11, (int?)status["result"]!["rowAfter"], "Lathe is now the largest quantity, so it moved to the end.");
                Assert.AreEqual("100", (string?)status["result"]!["displayText"]);
                JObject after = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("Lathe", CellOf(after, 11, 0));
                Assert.AreEqual("Drill", CellOf(after, 0, 0));
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task TheReadOnlyColumnsOfTheDemoGridAreRefusedAsReadOnlyBeforeAnyEditorOpens()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");
                foreach (int column in new[] { DemoGrid.ItemColumn, DemoGrid.ReservedColumn })
                {
                    AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, column, "x", token)), "read_only", "column " + column);
                    Assert.IsFalse(await FromDemoGridAsync(app, demo => demo.IsEditing), "No editor was opened.");
                }
                JObject page = await ReadGridAsync(app, "demoGrid");
                CollectionAssert.AreEqual(new[] { "Bolt", "10", "0" }, ((JArray)page["rows"]![1]!["cells"]!).Select(c => (string)c!).ToArray());
                Assert.AreEqual(token, (string)page["viewToken"]!);
                Assert.AreEqual(JTokenType.Null, page["focusedRow"]!.Type, "A refused edit does not even focus the cell.");
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task ANegativeOrNonNumericQuantityIsRejectedWithItsErrorTextAndNoDialog()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");

                JToken negative = await app.ActAsync("set_cell", grid, Edit(1, 1, "-5", token));
                AssertGridFailed(negative, "edit_rejected", "a negative quantity");
                JToken details = negative["error"]!["details"]!;
                Assert.AreEqual("validation_failed", (string?)details["reason"]);
                Assert.AreEqual("Quantity must not be negative.", (string?)details["errorText"]);
                Assert.AreEqual("10", (string?)details["displayText"], "The cell still shows the old value.");

                JToken text = await app.ActAsync("set_cell", grid, Edit(1, 1, "abc", token));
                AssertGridFailed(text, "edit_rejected", "text in a number column");
                Assert.AreEqual("invalid_input", (string?)text["error"]!["details"]!["reason"]);
                Assert.AreEqual("Quantity must be a whole number.", (string?)text["error"]!["details"]!["errorText"]);

                // A refusal is a failed receipt, not a modal error box: nothing else is open, the editor is closed
                // and the view (so the token) did not change.
                JToken windows = Success(await app.RequestAsync("windows"));
                CollectionAssert.AreEqual(new[] { "TestWindow" }, ((JArray)windows["windows"]!).Select(w => (string)w["name"]!).ToArray());
                Assert.IsFalse(await FromDemoGridAsync(app, demo => demo.IsEditing));
                JObject page = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("10", CellOf(page, 1, 1));
                Assert.AreEqual(token, (string)page["viewToken"]!);

                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "12", token)), "a valid edit still works afterwards");
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task AnEditThatNeedsConfirmationKeepsTheReceiptStartedUntilTheDialogIsKept()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");

                JToken receipt = await StartSetCellAsync(app, grid, 2, 1, "1500", token);
                JObject keep = await app.WaitForNodeAsync("btnConfirmKeep");
                Assert.AreEqual("started", (string?)Success(await app.RequestAsync("action_status", ReceiptParameters(receipt)))["state"]);
                JToken windows = Success(await app.RequestAsync("windows"));
                Assert.IsTrue(((JArray)windows["windows"]!).Any(window => (string?)window["name"] == "dlgConfirmQuantity"), "list_windows shows the dialog.");
                Assert.AreEqual("20", CellOf(await ReadGridAsync(app, "demoGrid"), 2, 1), "The value is held until the dialog is answered.");
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(3, 1, "6", token)), "grid_busy", "a second edit during the dialog");

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(keep)));
                await app.WaitForCompletionAsync(closing);
                JToken status = await app.WaitForTerminalAsync(receipt);

                AssertGridCompleted(status, "keeping the change completes the receipt");
                Assert.AreEqual(true, (bool?)status["result"]!["committed"]);
                Assert.AreEqual("1500", (string?)status["result"]!["displayText"]);
                Assert.AreEqual("1500", CellOf(await ReadGridAsync(app, "demoGrid"), 2, 1));
                Assert.IsFalse(await FromDemoGridAsync(app, demo => demo.IsEditing));
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task DiscardingTheConfirmDialogFailsTheReceiptAsEditRejectedAndChangesNothing()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                string token = await TokenAsync(app, "demoGrid");

                JToken receipt = await StartSetCellAsync(app, grid, 4, 1, "2000", token);
                JObject discard = await app.WaitForNodeAsync("btnConfirmDiscard");
                Assert.AreEqual("started", (string?)Success(await app.RequestAsync("action_status", ReceiptParameters(receipt)))["state"]);

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(discard)));
                await app.WaitForCompletionAsync(closing);
                JToken status = await app.WaitForTerminalAsync(receipt);

                AssertGridFailed(status, "edit_rejected", "discarding the confirm dialog");
                Assert.AreEqual("validation_failed", (string?)status["error"]!["details"]!["reason"]);
                Assert.AreEqual("The change was not confirmed.", (string?)status["error"]!["details"]!["errorText"]);
                Assert.AreEqual("45", (string?)status["error"]!["details"]!["displayText"]);
                Assert.AreEqual("45", CellOf(await ReadGridAsync(app, "demoGrid"), 4, 1));
                Assert.IsFalse(await FromDemoGridAsync(app, demo => demo.IsEditing));
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(4, 1, "46", token)), "the grid takes edits again");
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task ATokenReadBeforeASortIsStaleForSelectAndEditAndAFreshOneWorks()
        {
            using (var app = StartWithDemoGrid())
            {
                JObject grid = await app.FindAsync("demoGrid");
                JObject first = await ReadGridAsync(app, "demoGrid");
                string old = (string)first["viewToken"]!;
                Assert.AreEqual("Anvil", CellOf(first, 0, 0));

                await OnDemoGridAsync(app, demo => demo.Sort(DemoGrid.QuantityColumn, descending: false));

                JObject sorted = await ReadGridAsync(app, "demoGrid");
                Assert.AreEqual("Lathe", CellOf(sorted, 0, 0), "The rows moved.");
                string fresh = (string)sorted["viewToken"]!;
                Assert.AreNotEqual(old, fresh);

                JToken select = await app.ActAsync("select", grid, Cell(0, 1, old));
                AssertGridFailed(select, "stale_view", "a select with the pre-sort token");
                Assert.AreEqual("view_changed", (string?)select["error"]!["details"]!["reason"]);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "9", old)), "stale_view", "an edit with the pre-sort token");
                Assert.AreEqual("2", CellOf(await ReadGridAsync(app, "demoGrid"), 0, 1), "The stale edit wrote nothing.");

                AssertGridCompleted(await app.ActAsync("select", grid, Cell(0, 1, fresh)), "a select with the fresh token");
                Assert.AreEqual(0, (int)(await ReadGridAsync(app, "demoGrid"))["focusedRow"]!);
            }
        }

        [TestMethod]
        [TestCategory("DemoGrid")]
        public async Task ShowContextMenuIsAdvertisedForControlsWithAMenuAndOpeningEnablesDeleteOnlyForASelectedRow()
        {
            // Needs an interactive desktop: ContextMenuStrip.Show raises Opening only when the window can be active, so this
            // can come back inconclusive or fail on a session without one.
            using (var app = StartWithDemoGrid())
            {
                JArray tree = await app.TreeAsync();
                JObject grid = ControlNamed(tree, "demoGrid");
                Assert.IsTrue(Capabilities(grid).Contains("show_context_menu"));
                Assert.IsFalse(Capabilities(ControlNamed(tree, "Replaceable")).Contains("show_context_menu"), "Only controls with a ContextMenuStrip advertise it.");

                // The strip is reached through the grid; nothing has opened it yet.
                JToken rooted = Success(await app.RequestAsync("tree", TargetParameters(grid)));
                JObject strip = ItemNamed((JArray)rooted["nodes"]!, "ctxGrid");
                Assert.AreEqual("context_menu", (string?)strip["role"]);
                Assert.AreEqual((string?)grid["id"], (string?)strip["parentId"]);
                Assert.AreEqual(false, (bool?)strip["opened"]);
                Assert.AreEqual(false, (bool?)(await ItemUnderAsync(app, strip, "ctxGridDelete"))["enabled"], "Opening has not run yet.");
                Assert.AreEqual(true, (bool?)(await ItemUnderAsync(app, strip, "ctxGridInfo"))["enabled"]);

                var sources = new List<Control?>();
                await OnDemoGridAsync(app, demo => demo.ContextMenuStrip!.Opening += (sender, e) => sources.Add(((ContextMenuStrip)sender!).SourceControl));

                // No row selected: the menu is shown, Opening runs with the grid as source and leaves Delete disabled.
                AssertInvokeCompleted(await app.ActAsync("invoke", grid, p => p["action"] = "show_context_menu"), "show_context_menu without a selection");
                Assert.AreEqual(1, sources.Count, "Showing the menu raised Opening.");
                Assert.IsNotNull(sources[0]);
                Assert.AreEqual("demoGrid", sources[0]!.Name, "The source control is the grid.");
                Assert.AreEqual(false, (bool?)(await ItemUnderAsync(app, strip, "ctxGridDelete"))["enabled"]);
                Assert.AreEqual(true, (bool?)(await ItemUnderAsync(app, strip, "ctxGridInfo"))["enabled"]);
                bool? openedObserved = (bool?)Success(await app.RequestAsync("inspect", TargetParameters(strip)))["opened"];
                Assert.IsNotNull(openedObserved, "The strip reports the observed opened state (true or false, depending on the window being active).");

                string token = await TokenAsync(app, "demoGrid");
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(3, 1, token)), "select a row");
                Assert.AreEqual(false, (bool?)(await ItemUnderAsync(app, strip, "ctxGridDelete"))["enabled"], "Selecting a row does not run Opening.");

                AssertInvokeCompleted(await app.ActAsync("invoke", grid, p => p["action"] = "show_context_menu"), "show_context_menu with a selection");
                Assert.AreEqual(2, sources.Count, "The menu is closed before it is shown again, so Opening runs again.");
                Assert.AreEqual(true, (bool?)(await ItemUnderAsync(app, strip, "ctxGridDelete"))["enabled"], "Opening enabled Delete for the selected row.");
                Assert.AreEqual(true, (bool?)(await ItemUnderAsync(app, strip, "ctxGridInfo"))["enabled"]);
            }
        }
    }
}
