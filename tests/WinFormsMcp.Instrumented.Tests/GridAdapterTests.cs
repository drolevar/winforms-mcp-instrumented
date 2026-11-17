using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>
    /// Grid capabilities, per-grid freshness state and the predicate form of AddAction, driven by a fake
    /// third-party adapter that uses only the public seam. Shares the fixture of
    /// <see cref="BridgeIntegrationTests"/>; requires Windows and .NET Framework 4.8.
    /// </summary>
    public sealed partial class BridgeIntegrationTests
    {
        private static FakeRecord GridRecord(string id, string name, string quantity)
            => new FakeRecord { ["id"] = id, ["name"] = name, ["qty"] = quantity };

        private static FakeGrid NewFakeGrid(string name)
        {
            var grid = new FakeGrid { Name = name, Width = 200, Height = 100 };
            grid.Editor.Name = name + "Editor";
            grid.Columns.Add(new FakeGridColumn("id") { ReadOnly = true });
            grid.Columns.Add(new FakeGridColumn("name"));
            grid.Columns.Add(new FakeGridColumn("qty") { Kind = GridValueKind.Number });
            grid.Records.Add(GridRecord("1", "Alpha", "30"));
            grid.Records.Add(GridRecord("2", "Bravo", "10"));
            grid.Records.Add(GridRecord("3", "Charlie", "20"));
            return grid;
        }

        private static TestApplication StartWithGrids(FakeGridAdapter adapter, params string[] gridNames)
            => new TestApplication(
                options => options.AddGridAdapter(adapter),
                window =>
                {
                    foreach (string name in gridNames) window.Controls.Add(NewFakeGrid(name));
                });

        private static async Task<JObject[]> DiagnosticEntriesAsync(TestApplication app)
            => ((JArray)Success(await app.RequestAsync("diagnostics"))["entries"]!).OfType<JObject>().ToArray();

        // --- capabilities and the gridAdapter field ----------------------------------------------------

        [TestMethod]
        public async Task AnAcceptedGridAdvertisesReadGridAndTheFeaturesTheAdapterReports()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                var cases = new[]
                {
                    (Features: GridFeatures.None, Expected: new string[0]),
                    (Features: GridFeatures.Select, Expected: new[] { "select" }),
                    (Features: GridFeatures.Scroll, Expected: new[] { "scroll" }),
                    (Features: GridFeatures.Edit, Expected: new[] { "set_cell" }),
                    (Features: GridFeatures.Select | GridFeatures.Scroll | GridFeatures.Edit, Expected: new[] { "select", "scroll", "set_cell" })
                };
                foreach (var testCase in cases)
                {
                    adapter.Features = testCase.Features;
                    string[] capabilities = Capabilities(await app.FindAsync("Grid"));
                    CollectionAssert.AreEqual(new[] { "inspect", "capture", "focus", "ensure_visible", "read_grid" }.Concat(testCase.Expected).ToArray(),
                        capabilities, "Features " + testCase.Features + ".");
                    Assert.AreEqual(capabilities.Length, capabilities.Distinct().Count(), "No duplicate capabilities.");
                }
            }
        }

        [TestMethod]
        public async Task ControlsTheAdapterDoesNotAcceptGetNoGridCapabilitiesAndNoGridFields()
        {
            var adapter = new FakeGridAdapter { AcceptFilter = control => control.Name != "Refused" };
            using (var app = StartWithGrids(adapter, "Accepted", "Refused"))
            {
                JArray tree = await app.TreeAsync();
                Assert.IsNotNull(ControlNamed(tree, "Accepted")["gridAdapter"]);
                foreach (string name in new[] { "Refused", "Replaceable", "TestWindow" })
                {
                    JObject node = ControlNamed(tree, name);
                    Assert.IsFalse(Capabilities(node).Contains("read_grid"), name + " is not a grid.");
                    Assert.IsNull(node["gridAdapter"], name);
                    Assert.IsNull(node["gridError"], name);
                }
            }
        }

        [TestMethod]
        public async Task TheGridAdapterFieldNamesTheHandlingAdapterAndDataGridViewIsBuiltIn()
        {
            var adapter = new FakeGridAdapter("my-grid");
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JArray tree = await app.TreeAsync();
                Assert.AreEqual("my-grid", (string?)ControlNamed(tree, "Grid")["gridAdapter"]);
                JObject builtIn = ControlNamed(tree, "BigGrid");
                Assert.AreEqual("datagridview", (string?)builtIn["gridAdapter"]);
                CollectionAssert.IsSubsetOf(new[] { "select", "scroll", "read_grid" }, Capabilities(builtIn));
                Assert.IsNull(ControlNamed(tree, "BigList")["gridAdapter"]);
            }
        }

        [TestMethod]
        public async Task AcceptsIsCalledOncePerControlInstanceAndAttachOncePerAcceptedGrid()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                for (int walk = 0; walk < 3; walk++) await app.TreeAsync();
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Accepts), "Grid"));
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Accepts), "Replaceable"));
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Attach), "Grid"));
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Attach), "Replaceable"), "Attach only for an accepted grid.");
                Assert.AreEqual(3, adapter.CallsOf(nameof(GridAdapter.GetFeatures), "Grid"), "GetFeatures runs in every walk.");
            }
        }

        [TestMethod]
        public async Task AThrowingAcceptsMeansNotAcceptedWithOneDiagnostic()
        {
            var adapter = new FakeGridAdapter { ThrowInAccepts = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.TreeAsync();
                JObject grid = await app.FindAsync("Grid");
                Assert.IsFalse(Capabilities(grid).Contains("read_grid"));
                Assert.IsNull(grid["gridAdapter"]);
                Assert.IsNull(grid["gridError"], "A throwing Accepts is 'not accepted', not a grid error.");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Accepts), "Grid"), "The failed answer is cached.");

                JObject[] entries = (await DiagnosticEntriesAsync(app)).Where(entry => ((string)entry["message"]!).Contains("Accepts")).ToArray();
                Assert.AreEqual(1, entries.Length, "One diagnostic for one grid, however many walks.");
                Assert.AreEqual("adapter_failed", (string?)entries[0]["category"]);
                StringAssert.Contains((string)entries[0]["message"]!, "fake-grid");
            }
        }

        [TestMethod]
        public async Task AThrowingAttachGivesGridErrorAndNoGridCapabilities()
        {
            var adapter = new FakeGridAdapter { ThrowInAttach = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.TreeAsync();
                JObject grid = await app.FindAsync("Grid");
                Assert.AreEqual("adapter_failed", (string?)grid["gridError"]);
                CollectionAssert.AreEqual(new[] { "inspect", "capture", "focus", "ensure_visible" }, Capabilities(grid));
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Attach), "Grid"), "Attach is not retried.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.GetFeatures), "Grid"), "An unattached grid is not asked for features.");
                Assert.AreEqual(1, (await DiagnosticEntriesAsync(app)).Count(entry => ((string)entry["message"]!).Contains("Attach")));
            }
        }

        [TestMethod]
        public async Task AThrowingGetFeaturesGivesGridErrorAndTheTreeCarriesOn()
        {
            var adapter = new FakeGridAdapter { ThrowInGetFeatures = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.TreeAsync();
                JArray tree = await app.TreeAsync();
                JObject grid = ControlNamed(tree, "Grid");
                Assert.AreEqual("adapter_failed", (string?)grid["gridError"]);
                CollectionAssert.AreEqual(new[] { "inspect", "capture", "focus", "ensure_visible" }, Capabilities(grid));
                Assert.IsNull(grid["gridAdapter"], "A failed grid is not advertised as a usable grid.");
                Assert.IsNotNull(ControlNamed(tree, "Replaceable"), "The rest of the tree is still described.");
                Assert.AreEqual(1, (await DiagnosticEntriesAsync(app)).Count(entry => ((string)entry["message"]!).Contains("GetFeatures")),
                    "One diagnostic per grid, not one per walk.");

                adapter.ThrowInGetFeatures = false;
                JObject recovered = await app.FindAsync("Grid");
                Assert.IsNull(recovered["gridError"], "Features are asked again in every walk.");
                Assert.IsTrue(Capabilities(recovered).Contains("read_grid"));
            }
        }

        // --- the freshness token ----------------------------------------------------------------------

        // The token a read reports (viewToken) is read on the wire. Checking a token still goes through the
        // bridge's internal GridHost by reflection, because select and set_grid_cell are not on the wire yet and
        // this assembly has no InternalsVisibleTo.
        private static class GridInternals
        {
            private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            private static object Required(object? value, string what)
            {
                Assert.IsNotNull(value, what + " was not found; the bridge internals this test reads by reflection changed.");
                return value!;
            }

            internal static object GridHostOf(TestApplication app)
            {
                object bridge = Required(app.Bridge, "The bridge");
                object runtime = Required(bridge.GetType().GetField("ui", AnyInstance)?.GetValue(bridge), "BridgeHost.ui");
                return Required(runtime.GetType().GetField("grids", AnyInstance)?.GetValue(runtime), "UiRuntime.grids");
            }

            private static object? Call(object target, string method, params object?[] arguments)
            {
                MethodInfo info = (MethodInfo)Required(target.GetType().GetMethod(method, AnyInstance), "GridHost." + method);
                return info.Invoke(target, arguments);
            }

            internal static object? Result(object host, string method, params object?[] arguments) => Call(host, method, arguments);

            // The code, the details' reason and the message of the BridgeFailure the call threw; null when it did not throw.
            internal static (string Code, string? Reason, string Message)? Failure(object host, string method, params object?[] arguments)
            {
                try
                {
                    Call(host, method, arguments);
                    return null;
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    Exception inner = exception.InnerException;
                    var type = inner.GetType();
                    Assert.AreEqual("BridgeFailure", type.Name, "Unexpected exception from GridHost." + method + ": " + inner);
                    string code = (string)Required(type.GetProperty("Code", AnyInstance)?.GetValue(inner), "BridgeFailure.Code");
                    var details = (JToken?)type.GetProperty("Details", AnyInstance)?.GetValue(inner);
                    return (code, (string?)details?["reason"], inner.Message);
                }
            }
        }

        // The token from the smallest read of the grid, as an agent gets it.
        private static async Task<string> TokenAsync(TestApplication app, string gridName)
            => (string)(await ReadGridAsync(app, gridName, rowCount: 1, columnCount: 1))["viewToken"]!;

        // Calls a GridHost method on the UI thread with the named control as its first argument.
        private static async Task<(string Code, string? Reason, string Message)?> GridHostFailureAsync(
            TestApplication app, string method, string controlName, params object?[] more)
        {
            (string, string?, string)? result = null;
            await app.OnUiAsync(() => result = GridInternals.Failure(GridInternals.GridHostOf(app), method,
                new object?[] { app.Window.Controls[controlName]! }.Concat(more).ToArray()));
            return result;
        }

        private static Task<(string Code, string? Reason, string Message)?> CheckTokenAsync(TestApplication app, string gridName, string? token)
            => GridHostFailureAsync(app, "CheckToken", gridName, token);

        private static Task<(string Code, string? Reason, string Message)?> CheckTokenIfSuppliedAsync(TestApplication app, string gridName, string? token)
            => GridHostFailureAsync(app, "CheckTokenIfSupplied", gridName, token);

        private static string TokenSalt(string token) => token.Substring(0, token.IndexOf('.'));

        private static long TokenCounter(string token) => long.Parse(token.Substring(token.IndexOf('.') + 1), System.Globalization.CultureInfo.InvariantCulture);

        [TestMethod]
        public async Task TwoGridsOfTheSameAdapterHaveDifferentTokenSalts()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "GridA", "GridB"))
            {
                await app.TreeAsync();
                string a = await TokenAsync(app, "GridA");
                string b = await TokenAsync(app, "GridB");
                StringAssert.Matches(a, new Regex(@"^[0-9a-f]{16}\.[0-9]+$"));
                StringAssert.Matches(b, new Regex(@"^[0-9a-f]{16}\.[0-9]+$"));
                Assert.AreEqual(TokenCounter(a), TokenCounter(b), "Both grids are untouched, so only the salt tells them apart.");
                Assert.AreNotEqual(TokenSalt(a), TokenSalt(b));

                Assert.IsNull(await CheckTokenAsync(app, "GridA", a));
                (string Code, string? Reason, string Message)? crossed = await CheckTokenAsync(app, "GridB", a);
                Assert.IsNotNull(crossed, "A token read from one grid must not be accepted for another.");
                Assert.AreEqual("stale_view", crossed!.Value.Code);
                Assert.AreEqual("view_changed", crossed.Value.Reason);
            }
        }

        [TestMethod]
        public async Task TheTokenIsStableAcrossReads()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                string first = await TokenAsync(app, "Grid");
                await app.TreeAsync();
                Success(await app.RequestAsync("inspect", TargetParameters(await app.FindAsync("Grid"))));
                Assert.AreEqual(first, await TokenAsync(app, "Grid"));
                Assert.AreEqual(first, await TokenAsync(app, "Grid"));
                Assert.IsNull(await CheckTokenAsync(app, "Grid", first));
                Assert.IsNull(await CheckTokenIfSuppliedAsync(app, "Grid", null), "No token supplied means no check.");
                Assert.IsNull(await CheckTokenIfSuppliedAsync(app, "Grid", first));
            }
        }

        [TestMethod]
        public async Task TheTokenChangesAfterAnEventDrivenSort()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                string before = await TokenAsync(app, "Grid");
                await app.OnUiAsync(() => ((FakeGrid)app.Window.Controls["Grid"]!).Sort(2, descending: false));
                string after = await TokenAsync(app, "Grid");

                Assert.AreNotEqual(before, after);
                Assert.AreEqual(TokenSalt(before), TokenSalt(after), "The salt belongs to the grid, not to the view.");
                Assert.AreEqual(TokenCounter(before) + 1, TokenCounter(after));
                Assert.AreEqual(after, await TokenAsync(app, "Grid"), "One change bumps the counter once.");

                (string Code, string? Reason, string Message)? stale = await CheckTokenAsync(app, "Grid", before);
                Assert.IsNotNull(stale);
                Assert.AreEqual("stale_view", stale!.Value.Code);
                Assert.AreEqual("view_changed", stale.Value.Reason);
                Assert.IsFalse(stale.Value.Message.Contains(after) || stale.Value.Message.Contains(TokenSalt(after)),
                    "The current token must not be in the error.");
                Assert.IsNull(await CheckTokenAsync(app, "Grid", after));
            }
        }

        [TestMethod]
        public async Task TheTokenChangesAfterAChangeSeenOnlyByTheFingerprint()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                string before = await TokenAsync(app, "Grid");
                adapter.Fingerprint = 42;
                (string Code, string? Reason, string Message)? stale = await CheckTokenAsync(app, "Grid", before);
                Assert.AreEqual("stale_view", stale?.Code, "No event was raised; the fingerprint must reveal the change.");

                string after = await TokenAsync(app, "Grid");
                Assert.AreEqual(TokenCounter(before) + 1, TokenCounter(after));
                Assert.AreEqual(after, await TokenAsync(app, "Grid"), "An unchanged fingerprint does not bump again.");
            }
        }

        [TestMethod]
        public async Task AChangeThatRaisesNoEventAndLeavesTheFingerprintAloneIsNotSeen()
        {
            var adapter = new FakeGridAdapter { ForwardViewEvents = false };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                string before = await TokenAsync(app, "Grid");
                await app.OnUiAsync(() => ((FakeGrid)app.Window.Controls["Grid"]!).Sort(2, descending: true));
                Assert.AreEqual(before, await TokenAsync(app, "Grid"),
                    "Documented limit: an adapter that forwards no event and fingerprints nothing cannot be noticed.");
            }
        }

        // --- read_grid for adapter grids -------------------------------------------------------------------

        private static async Task<WinFormsMcp.Instrumented.Protocol.RpcResponse> ReadGridRawAsync(
            TestApplication app, string gridName, int startRow = 0, int rowCount = 25, int startColumn = 0, int columnCount = 20)
            => await app.RequestAsync("read_grid", GridPage(await app.FindAsync(gridName), startRow, rowCount, startColumn, columnCount));

        private static async Task<JObject> ReadGridAsync(
            TestApplication app, string gridName, int startRow = 0, int rowCount = 25, int startColumn = 0, int columnCount = 20)
            => (JObject)Success(await ReadGridRawAsync(app, gridName, startRow, rowCount, startColumn, columnCount));

        private static async Task<string?> ReadGridErrorCodeAsync(
            TestApplication app, string gridName, int startRow = 0, int rowCount = 25, int startColumn = 0, int columnCount = 20)
            => (await ReadGridRawAsync(app, gridName, startRow, rowCount, startColumn, columnCount)).Error?.Code;

        private static string[] Names(JObject value) => value.Properties().Select(property => property.Name).ToArray();

        // Replaces the grid's columns and records: "f0".."f<n>" with the text "r<row>c<column>".
        private static Task FillGridAsync(TestApplication app, string gridName, int rows, int columns)
            => app.OnUiAsync(() =>
            {
                var grid = (FakeGrid)app.Window.Controls[gridName]!;
                grid.Columns.Clear();
                grid.Records.Clear();
                for (int c = 0; c < columns; c++) grid.Columns.Add(new FakeGridColumn("f" + c));
                for (int r = 0; r < rows; r++)
                {
                    var record = new FakeRecord();
                    for (int c = 0; c < columns; c++) record["f" + c] = "r" + r + "c" + c;
                    grid.Records.Add(record);
                }
            });

        [TestMethod]
        public async Task AnAdapterGridReadReturnsTheDocumentedShapeEvenWithoutAnyFeature()
        {
            var adapter = new FakeGridAdapter { Features = GridFeatures.None };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    grid.Columns[0].Visible = false;
                    grid.Columns[1].Kind = GridValueKind.Choice;
                    grid.Columns[1].Choices.AddRange(new[] { "Alpha", "Bravo" });
                    grid.Columns[2].Bands.Add("Stock");
                    grid.Columns[2].Bands.Add(null);
                    grid.SelectedRow = 1;
                    grid.FocusedRow = 2;
                    grid.FocusedColumn = 1;
                });

                JObject page = await ReadGridAsync(app, "Grid");

                CollectionAssert.AreEquivalent(
                    new[] { "generation", "dataKind", "gridAdapter", "viewToken", "totalRows", "totalRowsIncludingNewRow", "totalColumns",
                            "startRow", "startColumn", "focusedRow", "focusedColumn", "columns", "rows", "hasMoreRows", "hasMoreColumns" },
                    Names(page));
                Assert.AreEqual("fake-grid", (string?)page["dataKind"], "dataKind is the adapter's name.");
                Assert.AreEqual("fake-grid", (string?)page["gridAdapter"]);
                StringAssert.Matches((string)page["viewToken"]!, new Regex(@"^[0-9a-f]{16}\.[0-9]+$"));
                Assert.AreEqual(3, (int)page["totalRows"]!);
                Assert.AreEqual(3, (int)page["totalRowsIncludingNewRow"]!, "Kept equal to totalRows for compatibility.");
                Assert.AreEqual(3, (int)page["totalColumns"]!);
                Assert.AreEqual(0, (int)page["startRow"]!);
                Assert.AreEqual(0, (int)page["startColumn"]!);
                Assert.AreEqual(2, (int)page["focusedRow"]!);
                Assert.AreEqual(1, (int)page["focusedColumn"]!);
                Assert.AreEqual(false, (bool)page["hasMoreRows"]!);
                Assert.AreEqual(false, (bool)page["hasMoreColumns"]!);

                var columns = (JArray)page["columns"]!;
                Assert.AreEqual(3, columns.Count);
                foreach (JToken column in columns)
                    CollectionAssert.AreEquivalent(
                        new[] { "index", "name", "header", "visible", "field", "editable", "valueKind", "bandPath", "redacted", "choices", "choicesTruncated" },
                        Names((JObject)column));
                Assert.AreEqual(0, (int)columns[0]["index"]!);
                Assert.AreEqual("idColumn", (string?)columns[0]["name"]);
                Assert.AreEqual("id", (string?)columns[0]["header"]);
                Assert.AreEqual("id", (string?)columns[0]["field"]);
                Assert.AreEqual(false, (bool)columns[0]["visible"]!);
                Assert.AreEqual(false, (bool)columns[0]["editable"]!, "A read-only column.");
                Assert.AreEqual("text", (string?)columns[0]["valueKind"]);
                Assert.AreEqual(JTokenType.Null, columns[0]["bandPath"]!.Type, "No band: null.");
                Assert.AreEqual(false, (bool)columns[0]["redacted"]!);
                Assert.AreEqual(0, ((JArray)columns[0]["choices"]!).Count);
                Assert.AreEqual(false, (bool)columns[0]["choicesTruncated"]!);
                Assert.AreEqual(true, (bool)columns[1]["visible"]!);
                Assert.AreEqual(true, (bool)columns[1]["editable"]!);
                Assert.AreEqual("choice", (string?)columns[1]["valueKind"]);
                CollectionAssert.AreEqual(new[] { "Alpha", "Bravo" }, ((JArray)columns[1]["choices"]!).Select(choice => (string?)choice).ToArray());
                Assert.AreEqual("number", (string?)columns[2]["valueKind"]);
                var bandPath = (JArray)columns[2]["bandPath"]!;
                Assert.AreEqual("Stock", (string?)bandPath[0]);
                Assert.AreEqual(JTokenType.Null, bandPath[1].Type, "An unnamed band is null.");
                Assert.AreEqual(2, bandPath.Count);

                var rows = (JArray)page["rows"]!;
                Assert.AreEqual(3, rows.Count);
                foreach (JToken row in rows)
                {
                    CollectionAssert.AreEquivalent(new[] { "index", "visible", "selected", "cells" }, Names((JObject)row));
                    Assert.AreEqual(true, (bool)row["visible"]!, "Every data row of an adapter grid is visible.");
                }
                Assert.AreEqual(1, (int)rows[1]["index"]!);
                Assert.AreEqual(true, (bool)rows[1]["selected"]!);
                Assert.AreEqual(false, (bool)rows[0]["selected"]!);
                CollectionAssert.AreEqual(new[] { "1", "Alpha", "30" }, ((JArray)rows[0]["cells"]!).Select(cell => (string?)cell).ToArray());
                CollectionAssert.AreEqual(new[] { "3", "Charlie", "20" }, ((JArray)rows[2]["cells"]!).Select(cell => (string?)cell).ToArray());
            }
        }

        [TestMethod]
        public async Task TheFocusedCellIsNullWhenUnknownAndSelectedIsOmittedWhenTheAdapterCannotTell()
        {
            var adapter = new FakeGridAdapter { NoRowSelection = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(JTokenType.Null, page["focusedRow"]!.Type);
                Assert.AreEqual(JTokenType.Null, page["focusedColumn"]!.Type);
                foreach (JToken row in (JArray)page["rows"]!)
                    CollectionAssert.AreEquivalent(new[] { "index", "visible", "cells" }, Names((JObject)row));

                // A focus outside the view is not a focus.
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    grid.FocusedRow = 7;
                    grid.FocusedColumn = 1;
                });
                page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(JTokenType.Null, page["focusedRow"]!.Type);
                Assert.AreEqual(JTokenType.Null, page["focusedColumn"]!.Type);
            }
        }

        [TestMethod]
        public async Task AdapterGridPagesAreBoundedClampedAndValidatedLikeDataGridView()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await FillGridAsync(app, "Grid", 250, 60);

                JObject full = await ReadGridAsync(app, "Grid", 0, 200, 0, 50);
                Assert.AreEqual(200, ((JArray)full["rows"]!).Count);
                Assert.AreEqual(50, ((JArray)full["columns"]!).Count);
                Assert.AreEqual(true, (bool)full["hasMoreRows"]!);
                Assert.AreEqual(true, (bool)full["hasMoreColumns"]!);
                Assert.AreEqual(250, (int)full["totalRows"]!);
                Assert.AreEqual(60, (int)full["totalColumns"]!);

                JObject defaults = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(25, ((JArray)defaults["rows"]!).Count, "The default page is 25 rows.");
                Assert.AreEqual(20, ((JArray)defaults["columns"]!).Count, "The default page is 20 columns.");

                JObject tail = await ReadGridAsync(app, "Grid", 240, 25, 55, 20);
                Assert.AreEqual(10, ((JArray)tail["rows"]!).Count);
                Assert.AreEqual(5, ((JArray)tail["columns"]!).Count);
                Assert.AreEqual(false, (bool)tail["hasMoreRows"]!);
                Assert.AreEqual(false, (bool)tail["hasMoreColumns"]!);
                Assert.AreEqual("r249c59", (string?)tail["rows"]![9]!["cells"]![4]);
                Assert.AreEqual(240, (int)tail["startRow"]!);
                Assert.AreEqual(55, (int)tail["startColumn"]!);
                Assert.AreEqual(55, (int)tail["columns"]![0]!["index"]!);
                Assert.AreEqual(249, (int)tail["rows"]![9]!["index"]!);

                // startRow / startColumn equal to the count is an empty final page; past it is invalid.
                JObject emptyRows = await ReadGridAsync(app, "Grid", 250, 25, 0, 2);
                Assert.AreEqual(0, ((JArray)emptyRows["rows"]!).Count);
                Assert.AreEqual(false, (bool)emptyRows["hasMoreRows"]!);
                JObject emptyColumns = await ReadGridAsync(app, "Grid", 0, 5, 60, 20);
                Assert.AreEqual(0, ((JArray)emptyColumns["columns"]!).Count);
                Assert.AreEqual(false, (bool)emptyColumns["hasMoreColumns"]!);
                foreach (JToken row in (JArray)emptyColumns["rows"]!) Assert.AreEqual(0, ((JArray)row["cells"]!).Count);
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 251, 25, 0, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 25, 61, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", -1, 25, 0, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 25, -1, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 201, 0, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 0, 0, 2));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 10, 0, 51));
                Assert.AreEqual("invalid_argument", await ReadGridErrorCodeAsync(app, "Grid", 0, 10, 0, 0));
            }
        }

        [TestMethod]
        public async Task TheAdapterIsAskedOnlyAboutTheClampedPage()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await FillGridAsync(app, "Grid", 250, 60);
                // The tree walk that finds the grid also asks its view about redaction (for the hosted editor); count only the read.
                JObject gridNode = await app.FindAsync("Grid");
                int viewInfo = adapter.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid");
                int describe = adapter.CallsOf(nameof(GridAdapter.DescribeColumn), "Grid");
                int cells = adapter.CallsOf(nameof(GridAdapter.GetCellText), "Grid");
                int selected = adapter.CallsOf(nameof(GridAdapter.IsRowSelected), "Grid");
                int focused = adapter.CallsOf(nameof(GridAdapter.TryGetFocusedCell), "Grid");

                JObject page = (JObject)Success(await app.RequestAsync("read_grid", GridPage(gridNode, 10, 2, 5, 2)));

                Assert.AreEqual("r10c5", (string?)page["rows"]![0]!["cells"]![0]);
                Assert.AreEqual("r11c6", (string?)page["rows"]![1]!["cells"]![1]);
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid") - viewInfo, "One view query per read, for the token and the counts.");
                Assert.AreEqual(2, adapter.CallsOf(nameof(GridAdapter.DescribeColumn), "Grid") - describe);
                Assert.AreEqual(4, adapter.CallsOf(nameof(GridAdapter.GetCellText), "Grid") - cells);
                Assert.AreEqual(2, adapter.CallsOf(nameof(GridAdapter.IsRowSelected), "Grid") - selected);
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.TryGetFocusedCell), "Grid") - focused);
            }
        }

        [TestMethod]
        public async Task ACellOf10000CharactersIsCutAndListedInTruncated()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    grid.Columns[1].Header = new string('h', 5_000);
                    grid.Records[1]["name"] = new string('x', 10_000);
                    grid.Records[1]["qty"] = new string('y', 4_096);   // exactly at the limit: not cut
                    grid.Records[2]["id"] = new string('i', 4_097);
                    grid.Records[2]["name"] = new string('n', 4_097);
                });

                JObject page = await ReadGridAsync(app, "Grid");
                var rows = (JArray)page["rows"]!;

                Assert.AreEqual(4_096, ((string)rows[1]["cells"]![1]!).Length, "The cell is cut to MaxTextLength.");
                Assert.AreEqual(new string('x', 4_096), (string?)rows[1]["cells"]![1]);
                Assert.AreEqual(4_096, ((string)rows[1]["cells"]![2]!).Length);
                CollectionAssert.AreEqual(new[] { 1 }, rows[1]["truncated"]!.Select(index => (int)index).ToArray(),
                    "Only the cut cell is listed; a cell of exactly the limit is not.");
                CollectionAssert.AreEqual(new[] { 0, 1 }, rows[2]["truncated"]!.Select(index => (int)index).ToArray());
                Assert.IsNull(rows[0]["truncated"], "Omitted when no cell was cut.");
                Assert.AreEqual(4_096, ((string)page["columns"]![1]!["header"]!).Length);
            }
        }

        [TestMethod]
        public async Task ARedactedColumnHasNullCellsAndIsNeverAskedForText()
        {
            var adapter = new FakeGridAdapter { ThrowInGetCellText = (row, column) => column == 2 };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    grid.Columns[2].Redacted = true;
                    foreach (FakeRecord record in grid.Records) record["qty"] = "SECRET-" + record["id"];
                });

                JObject page = await ReadGridAsync(app, "Grid");

                Assert.AreEqual(true, (bool)page["columns"]![2]!["redacted"]!);
                Assert.AreEqual(false, (bool)page["columns"]![1]!["redacted"]!);
                foreach (JToken row in (JArray)page["rows"]!)
                {
                    var cells = (JArray)row["cells"]!;
                    Assert.AreEqual(3, cells.Count, "The cell is kept in position.");
                    Assert.AreEqual(JTokenType.Null, cells[2].Type, "A redacted cell is null.");
                    Assert.AreNotEqual(JTokenType.Null, cells[1].Type);
                }
                Assert.IsFalse(page.ToString().Contains("SECRET"));
                Assert.AreEqual(6, adapter.CallsOf(nameof(GridAdapter.GetCellText), "Grid"), "3 rows by the 2 columns that are not redacted.");
            }
        }

        [TestMethod]
        public async Task BandPathsAreCappedAtEightLevelsAndChoicesAtFiftyWithATruncationFlag()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    for (int level = 1; level <= 9; level++) grid.Columns[1].Bands.Add("band" + level);
                    for (int choice = 1; choice <= 51; choice++) grid.Columns[2].Choices.Add("choice" + choice);
                    grid.Columns[0].Choices.Add("only");
                });

                JObject page = await ReadGridAsync(app, "Grid");

                var bands = (JArray)page["columns"]![1]!["bandPath"]!;
                Assert.AreEqual(8, bands.Count, "The 9th band is dropped.");
                Assert.AreEqual("band8", (string?)bands[7]);
                var choices = (JArray)page["columns"]![2]!["choices"]!;
                Assert.AreEqual(50, choices.Count, "The 51st choice is dropped.");
                Assert.AreEqual("choice50", (string?)choices[49]);
                Assert.AreEqual(true, (bool)page["columns"]![2]!["choicesTruncated"]!);
                Assert.AreEqual(false, (bool)page["columns"]![0]!["choicesTruncated"]!);
            }
        }

        [TestMethod]
        public async Task CellsCountAgainstTheResponseBudgetAndAnOverBudgetPageFailsWithResponseLimit()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await FillGridAsync(app, "Grid", 70, 1);
                await app.OnUiAsync(() =>
                {
                    foreach (FakeRecord record in ((FakeGrid)app.Window.Controls["Grid"]!).Records) record["f0"] = new string('z', 5_000);
                });

                // 60 cells of 4,096 characters are 245,760 of the 262,144; 70 are over.
                JObject within = await ReadGridAsync(app, "Grid", 0, 60, 0, 1);
                Assert.AreEqual(60, ((JArray)within["rows"]!).Count);
                Assert.AreEqual("response_limit", await ReadGridErrorCodeAsync(app, "Grid", 0, 70, 0, 1));
            }
        }

        [TestMethod]
        public async Task ChoicesAndBandCaptionsCountAgainstTheResponseBudget()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await FillGridAsync(app, "Grid", 1, 10);
                await app.OnUiAsync(() =>
                {
                    var grid = (FakeGrid)app.Window.Controls["Grid"]!;
                    // Columns 0 and 1: 50 choices of 4,096 characters each (204,800 per column).
                    for (int column = 0; column < 2; column++)
                        for (int choice = 0; choice < 50; choice++) grid.Columns[column].Choices.Add(new string('c', 4_096));
                    // Columns 2..9: 8 bands of 4,096 characters each (32,768 per column; seven columns fit, eight do not).
                    for (int column = 2; column < 10; column++)
                        for (int level = 0; level < 8; level++) grid.Columns[column].Bands.Add(new string('b', 4_096));
                });

                Assert.IsNotNull(Success(await ReadGridRawAsync(app, "Grid", 0, 1, 0, 1)), "One column of choices fits.");
                Assert.AreEqual("response_limit", await ReadGridErrorCodeAsync(app, "Grid", 0, 1, 0, 2), "Two columns of choices do not.");
                Assert.IsNotNull(Success(await ReadGridRawAsync(app, "Grid", 0, 1, 2, 7)), "Seven banded columns fit.");
                Assert.AreEqual("response_limit", await ReadGridErrorCodeAsync(app, "Grid", 0, 1, 2, 8), "Eight banded columns with the cells do not.");
            }
        }

        [TestMethod]
        public async Task AnAdapterThatThrowsInGetCellTextFailsTheReadAndLeaksNoText()
        {
            var adapter = new FakeGridAdapter
            {
                ThrowInGetCellText = (row, column) => row == 1 && column == 1,
                // An adapter that puts the offending value into its exception message.
                GetCellTextFailureDetail = " The value 'Bravo' (cell text) is invalid."
            };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                var response = await ReadGridRawAsync(app, "Grid");

                Assert.IsNull(response.Result, "No partial page.");
                Assert.AreEqual("adapter_failed", response.Error?.Code);
                string message = response.Error!.Message;
                StringAssert.Contains(message, "InvalidOperationException");
                StringAssert.Contains(message, "GetCellText");
                string everything = message + response.Error.Details;
                foreach (string text in new[] { "Alpha", "Bravo", "Charlie", "cell text", "invalid" })
                    Assert.IsFalse(everything.Contains(text), "The error must not carry cell text or the adapter's message: " + text);

                string diagnostics = string.Join("\n", (await DiagnosticEntriesAsync(app)).Select(entry => entry.ToString()));
                Assert.IsTrue(diagnostics.Contains("GetCellText"), "The failure is recorded.");
                foreach (string text in new[] { "Alpha", "Bravo", "Charlie", "cell text", "invalid" })
                    Assert.IsFalse(diagnostics.Contains(text), "Diagnostics must not carry cell text or the adapter's message: " + text);

                adapter.ThrowInGetCellText = null;
                Success(await ReadGridRawAsync(app, "Grid"));
            }
        }

        [TestMethod]
        public async Task AnAdapterThatFailsInAnyOtherReadMemberFailsTheReadWithAdapterFailed()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                var cases = new (string Member, Action<bool> Set)[]
                {
                    (nameof(GridAdapter.GetViewInfo), on => adapter.ThrowInGetViewInfo = on),
                    (nameof(GridAdapter.DescribeColumn), on => adapter.ThrowInDescribeColumn = on),
                    (nameof(GridAdapter.IsRowSelected), on => adapter.ThrowInIsRowSelected = on),
                    (nameof(GridAdapter.TryGetFocusedCell), on => adapter.ThrowInTryGetFocusedCell = on)
                };
                foreach (var testCase in cases)
                {
                    testCase.Set(true);
                    var response = await ReadGridRawAsync(app, "Grid");
                    Assert.AreEqual("adapter_failed", response.Error?.Code, testCase.Member);
                    StringAssert.Contains(response.Error!.Message, testCase.Member);
                    StringAssert.Contains(response.Error.Message, "InvalidOperationException");
                    testCase.Set(false);
                    Success(await ReadGridRawAsync(app, "Grid"));
                }
            }
        }

        [TestMethod]
        public async Task AGridTheAdapterFailedForCannotBeRead()
        {
            var attachFails = new FakeGridAdapter { ThrowInAttach = true };
            using (var app = StartWithGrids(attachFails, "Grid"))
            {
                await app.TreeAsync();
                Assert.AreEqual("adapter_failed", await ReadGridErrorCodeAsync(app, "Grid"));
                Assert.AreEqual(0, attachFails.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid"), "An unattached grid is not read.");
            }
            var featuresFail = new FakeGridAdapter { ThrowInGetFeatures = true };
            using (var app = StartWithGrids(featuresFail, "Grid"))
            {
                // Describing the grid's hosted editor asks the view (to decide about redaction); the read itself must not.
                JObject node = await app.FindAsync("Grid");
                int viewQueries = featuresFail.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid");
                Assert.AreEqual("adapter_failed", (await app.RequestAsync("read_grid", GridPage(node, 0, 25, 0, 20))).Error?.Code);
                Assert.AreEqual(viewQueries, featuresFail.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid"), "The read did not query the view.");
            }
        }

        [TestMethod]
        public async Task AGroupedOrVirtualViewGivesUnsupportedOperation()
        {
            var adapter = new FakeGridAdapter { UnsupportedView = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                var response = await ReadGridRawAsync(app, "Grid");
                Assert.AreEqual("unsupported_operation", response.Error?.Code);
                StringAssert.Contains(response.Error!.Message, "Grouped views", "The adapter's reason reaches the agent.");
                adapter.UnsupportedView = false;
                Success(await ReadGridRawAsync(app, "Grid"));
            }
        }

        [TestMethod]
        public async Task AGridTheAdapterDoesNotAcceptCannotBeRead()
        {
            var adapter = new FakeGridAdapter { AcceptFilter = control => control.Name != "Refused" };
            using (var app = StartWithGrids(adapter, "Accepted", "Refused"))
            {
                Success(await ReadGridRawAsync(app, "Accepted"));
                Assert.AreEqual("unsupported_operation", await ReadGridErrorCodeAsync(app, "Refused"));
            }
        }

        // --- read_grid for DataGridView: unchanged shape plus gridAdapter and a fingerprint-only token ------

        private sealed class GridItem
        {
            public string Name { get; set; } = "";
        }

        [TestMethod]
        public async Task ADataGridViewReadKeepsItsShapeAndGainsOnlyGridAdapterAndViewToken()
        {
            using (var app = new TestApplication())
            {
                JObject page = await ReadGridAsync(app, "BigGrid", 0, 3, 0, 2);

                CollectionAssert.AreEquivalent(
                    new[] { "generation", "dataKind", "totalRowsIncludingNewRow", "totalColumns", "startRow", "startColumn", "columns", "rows",
                            "hasMoreRows", "hasMoreColumns", "gridAdapter", "viewToken" },
                    Names(page));
                Assert.AreEqual("datagridview", (string?)page["gridAdapter"]);
                Assert.AreEqual("formatted-display-values", (string?)page["dataKind"]);
                Assert.AreEqual(250, (int)page["totalRowsIncludingNewRow"]!);
                StringAssert.Matches((string)page["viewToken"]!, new Regex(@"^[0-9a-f]{16}\.0$"), "The first read records the baseline: counter 0.");
                CollectionAssert.AreEquivalent(new[] { "index", "name", "header", "visible" }, Names((JObject)page["columns"]![0]!));
                CollectionAssert.AreEquivalent(new[] { "index", "visible", "cells" }, Names((JObject)page["rows"]![0]!));
            }
        }

        [TestMethod]
        public async Task TheDataGridViewTokenIsPerGridStableAndChangesWithTheFingerprint()
        {
            using (var app = new TestApplication(null, window =>
            {
                var boundGrid = new DataGridView { Name = "BoundGrid", AutoGenerateColumns = false, AllowUserToAddRows = false, Top = 380, Width = 100, Height = 60 };
                boundGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "n", DataPropertyName = nameof(GridItem.Name) });
                boundGrid.DataSource = new List<GridItem> { new GridItem { Name = "one" }, new GridItem { Name = "two" } };
                window.Controls.Add(boundGrid);
            }))
            {
                async Task<string> Token(string name) => (string)(await ReadGridAsync(app, name, 0, 1, 0, 1))["viewToken"]!;

                string big = await Token("BigGrid");
                string bound = await Token("BoundGrid");
                Assert.AreNotEqual(TokenSalt(big), TokenSalt(bound), "Per-grid salt.");
                Assert.AreEqual(big, await Token("BigGrid"), "Stable across reads.");
                Assert.AreEqual(bound, await Token("BoundGrid"));
                await app.TreeAsync();
                Assert.AreEqual(big, await Token("BigGrid"), "A tree walk does not touch it.");

                // Row count.
                await app.OnUiAsync(() => ((DataGridView)app.Window.Controls["BigGrid"]!).Rows.Add("new", "row"));
                string afterAdd = await Token("BigGrid");
                Assert.AreEqual(TokenSalt(big), TokenSalt(afterAdd));
                Assert.AreEqual(TokenCounter(big) + 1, TokenCounter(afterAdd));
                Assert.AreEqual(afterAdd, await Token("BigGrid"));

                // Sort column and order.
                await app.OnUiAsync(() => { var grid = (DataGridView)app.Window.Controls["BigGrid"]!; grid.Sort(grid.Columns[0], ListSortDirection.Descending); });
                string afterSort = await Token("BigGrid");
                Assert.AreEqual(TokenCounter(afterAdd) + 1, TokenCounter(afterSort));
                await app.OnUiAsync(() => { var grid = (DataGridView)app.Window.Controls["BigGrid"]!; grid.Sort(grid.Columns[0], ListSortDirection.Ascending); });
                Assert.AreEqual(TokenCounter(afterSort) + 1, TokenCounter(await Token("BigGrid")), "Only the sort order differs.");

                // Data-source identity with the same row and column counts.
                Assert.AreEqual(bound, await Token("BoundGrid"));
                await app.OnUiAsync(() => ((DataGridView)app.Window.Controls["BoundGrid"]!).DataSource =
                    new List<GridItem> { new GridItem { Name = "three" }, new GridItem { Name = "four" } });
                Assert.AreEqual(TokenCounter(bound) + 1, TokenCounter(await Token("BoundGrid")));
            }
        }

        // --- select and scroll on grids -----------------------------------------------------------------

        private static Action<JObject> Cell(int? row, int? column, string? token) => p =>
        {
            if (row.HasValue) p["row"] = row.Value;
            if (column.HasValue) p["column"] = column.Value;
            if (token != null) p["viewToken"] = token;
        };

        private static void AssertGridFailed(JToken status, string code, string because)
        {
            Assert.AreEqual("failed", (string?)status["state"], because + ": " + status);
            Assert.AreEqual(code, (string?)status["error"]?["code"], because + ": " + status);
        }

        private static void AssertGridCompleted(JToken status, string because)
        {
            Assert.AreEqual("completed", (string?)status["state"], because + ": " + status);
            Assert.AreEqual(true, (bool?)status["result"]?["handlerReturned"], because);
        }

        private static Task OnGridAsync(TestApplication app, string name, Action<FakeGrid> action)
            => app.OnUiAsync(() => action((FakeGrid)app.Window.Controls[name]!));

        private static async Task<(int Row, int Column)> FirstVisibleAsync(TestApplication app, string name)
        {
            (int, int) result = (-1, -1);
            await OnGridAsync(app, name, grid => result = (grid.FirstVisibleRow, grid.FirstVisibleColumn));
            return result;
        }

        [TestMethod]
        public async Task SelectOnAnAdapterGridRequiresARowAndAViewToken()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var cases = new (string Case, Action<JObject> Configure)[]
                {
                    ("nothing", Cell(null, null, null)),
                    ("no token", Cell(1, null, null)),
                    ("no row", Cell(null, null, token)),
                    ("null token", p => { p["row"] = 1; p["viewToken"] = JValue.CreateNull(); }),
                    ("empty token", Cell(1, null, "")),
                    ("numeric token", p => { p["row"] = 1; p["viewToken"] = 5; }),
                    ("string row", p => { p["row"] = "1"; p["viewToken"] = token; }),
                    ("string column", p => { p["row"] = 1; p["column"] = "1"; p["viewToken"] = token; })
                };
                foreach (var testCase in cases)
                    AssertGridFailed(await app.ActAsync("select", grid, testCase.Configure), "invalid_argument", testCase.Case);
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Select)), "The adapter is not called for a malformed request.");
            }
        }

        [TestMethod]
        public async Task SelectOnAnAdapterGridFocusesTheCellMakesItTheOnlySelectionAndBringsItIntoView()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                AssertGridCompleted(await app.ActAsync("select", grid, Cell(1, 2, token)), "select row 1, column 2");
                JObject page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(1, (int)page["focusedRow"]!);
                Assert.AreEqual(2, (int)page["focusedColumn"]!);
                CollectionAssert.AreEqual(new[] { false, true, false },
                    ((JArray)page["rows"]!).Select(row => (bool)row["selected"]!).ToArray());
                Assert.AreEqual(1, (await FirstVisibleAsync(app, "Grid")).Row, "The row is brought into view.");
                Assert.AreEqual(token, (string?)page["viewToken"], "A select does not change the view, so the token stays.");

                // No column: the adapter picks the cell; the same token is still current. Only one row stays selected.
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(2, null, token)), "select row 2, no column");
                page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(2, (int)page["focusedRow"]!);
                Assert.AreEqual(0, (int)page["focusedColumn"]!, "The first visible column.");
                CollectionAssert.AreEqual(new[] { false, false, true },
                    ((JArray)page["rows"]!).Select(row => (bool)row["selected"]!).ToArray());
                Assert.AreEqual(2, adapter.CallsOf(nameof(GridAdapter.Select)));
            }
        }

        [TestMethod]
        public async Task AStaleViewTokenFailsStaleViewWithDetailsAndNeverEchoesATokenAndNothingIsSelected()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string old = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake => fake.Sort(1, true));
                string current = await TokenAsync(app, "Grid");
                Assert.AreNotEqual(old, current);

                JToken status = await app.ActAsync("select", grid, Cell(1, 1, old));

                AssertGridFailed(status, "stale_view", "a token read before the sort");
                var error = (JObject)status["error"]!;
                CollectionAssert.AreEquivalent(new[] { "code", "message", "details" }, Names(error));
                Assert.AreEqual("view_changed", (string?)error["details"]!["reason"]);
                CollectionAssert.AreEquivalent(new[] { "reason" }, Names((JObject)error["details"]!), "The current token is deliberately not returned.");
                foreach (string secret in new[] { old, current, TokenSalt(current) })
                    Assert.IsFalse(error.ToString().Contains(secret), "No token in the error.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Select)));
                Assert.AreEqual(JTokenType.Null, (await ReadGridAsync(app, "Grid"))["focusedRow"]!.Type, "Nothing was selected.");

                AssertGridCompleted(await app.ActAsync("select", grid, Cell(1, 1, current)), "the current token");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Select)));
            }
        }

        [TestMethod]
        public async Task SelectOfAHiddenColumnFailsHiddenControlAndAVisibleColumnStillWorks()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Visible = false);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                AssertGridFailed(await app.ActAsync("select", grid, Cell(0, 1, token)), "hidden_control", "the hidden column");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Select)), "Select is not called for a hidden column.");
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(0, 2, token)), "a visible column after the failure");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Select)));
            }
        }

        [TestMethod]
        public async Task SelectOutOfRangeIsInvalidArgumentAndAFailureLeavesTheGridUsable()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var cases = new (string Case, Action<JObject> Configure)[]
                {
                    ("row -1", Cell(-1, null, token)),
                    ("row 3 of 3", Cell(3, null, token)),
                    ("column -1", Cell(0, -1, token)),
                    ("column 3 of 3", Cell(0, 3, token))
                };
                foreach (var testCase in cases)
                    AssertGridFailed(await app.ActAsync("select", grid, testCase.Configure), "invalid_argument", testCase.Case);
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Select)));
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(2, 2, token)), "the last cell is in range");

                await FillGridAsync(app, "Grid", 0, 3);
                string emptyToken = await TokenAsync(app, "Grid");
                AssertGridFailed(await app.ActAsync("select", grid, Cell(0, null, emptyToken)), "invalid_argument", "row 0 of an empty grid");
            }
        }

        [TestMethod]
        public async Task SelectAndScrollNeedTheirFeatureAndAnUnusableGridFailsBeforeTheAdapterIsCalled()
        {
            var adapter = new FakeGridAdapter { AcceptFilter = control => control.Name != "Refused" };
            using (var app = StartWithGrids(adapter, "Grid", "Refused"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                foreach (var testCase in new[]
                {
                    (Features: GridFeatures.None, Select: "unsupported_operation", Scroll: "unsupported_operation"),
                    (Features: GridFeatures.Scroll | GridFeatures.Edit, Select: "unsupported_operation", Scroll: (string?)null),
                    (Features: GridFeatures.Select | GridFeatures.Edit, Select: (string?)null, Scroll: "unsupported_operation")
                })
                {
                    adapter.Features = testCase.Features;
                    JToken selected = await app.ActAsync("select", grid, Cell(1, 1, token));
                    JToken scrolled = await app.ActAsync("scroll", grid, Cell(1, null, token));
                    if (testCase.Select == null) AssertGridCompleted(selected, "select with " + testCase.Features);
                    else AssertGridFailed(selected, testCase.Select, "select with " + testCase.Features);
                    if (testCase.Scroll == null) AssertGridCompleted(scrolled, "scroll with " + testCase.Features);
                    else AssertGridFailed(scrolled, testCase.Scroll, "scroll with " + testCase.Features);
                }
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Select)), "Only the granted select reached the adapter.");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Scroll)), "Only the granted scroll reached the adapter.");

                // A control the adapter does not accept has no adapter select; it is not a DataGridView either.
                JObject refused = await app.FindAsync("Refused");
                AssertGridFailed(await app.ActAsync("select", refused, Cell(0, 0, token)), "unsupported_operation", "unaccepted grid select");
                AssertGridFailed(await app.ActAsync("scroll", refused, Cell(0, null, null)), "unsupported_operation", "unaccepted grid scroll");

                adapter.Features = GridFeatures.Select | GridFeatures.Scroll;
                adapter.ThrowInGetFeatures = true;
                AssertGridFailed(await app.ActAsync("select", grid, Cell(1, 1, token)), "adapter_failed", "GetFeatures failed");
                adapter.ThrowInGetFeatures = false;

                await OnGridAsync(app, "Grid", fake => fake.Enabled = false);
                AssertGridFailed(await app.ActAsync("select", grid, Cell(1, 1, token)), "disabled_control", "disabled grid");
                await OnGridAsync(app, "Grid", fake => { fake.Enabled = true; fake.Visible = false; });
                AssertGridFailed(await app.ActAsync("scroll", grid, Cell(1, null, null)), "hidden_control", "hidden grid");
                await OnGridAsync(app, "Grid", fake => fake.Visible = true);
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Select)), "The prechecks run before the adapter.");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Scroll)));
            }
        }

        [TestMethod]
        public async Task SelectChecksRunInTheDocumentedOrder()
        {
            var adapter = new FakeGridAdapter { Features = GridFeatures.Scroll };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string old = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake => { fake.Sort(1, true); fake.Columns[1].Visible = false; });

                AssertGridFailed(await app.ActAsync("select", grid, Cell(99, 1, old)), "unsupported_operation",
                    "the feature comes before range, token and hidden column");
                adapter.Features = GridFeatures.Select;
                AssertGridFailed(await app.ActAsync("select", grid, Cell(99, 1, old)), "invalid_argument",
                    "the range comes before the token");
                AssertGridFailed(await app.ActAsync("select", grid, Cell(0, 1, old)), "stale_view",
                    "the token comes before the hidden column");
                string current = await TokenAsync(app, "Grid");
                AssertGridFailed(await app.ActAsync("select", grid, Cell(0, 1, current)), "hidden_control", "the hidden column");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Select)));
            }
        }

        [TestMethod]
        public async Task ScrollBringsARowOrAColumnIntoViewAndChecksTheTokenOnlyWhenSupplied()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(2, null, null)), "row, no token");
                Assert.AreEqual((2, 0), await FirstVisibleAsync(app, "Grid"));
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(null, 1, token)), "column, current token");
                Assert.AreEqual((2, 1), await FirstVisibleAsync(app, "Grid"));
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(0, 2, token)), "row and column");
                Assert.AreEqual((0, 2), await FirstVisibleAsync(app, "Grid"));
                JObject page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(JTokenType.Null, page["focusedRow"]!.Type, "Scrolling moves neither focus nor selection.");
                Assert.IsFalse(((JArray)page["rows"]!).Any(row => (bool)row["selected"]!));
                Assert.AreEqual(token, (string?)page["viewToken"], "A scroll does not change the view.");

                var invalid = new (string Case, Action<JObject> Configure)[]
                {
                    ("neither row nor column", Cell(null, null, token)),
                    ("row -1", Cell(-1, null, null)),
                    ("row 3 of 3", Cell(3, null, null)),
                    ("column -1", Cell(null, -1, null)),
                    ("column 3 of 3", Cell(null, 3, null)),
                    ("numeric token", p => { p["row"] = 1; p["viewToken"] = 5; })
                };
                foreach (var testCase in invalid)
                    AssertGridFailed(await app.ActAsync("scroll", grid, testCase.Configure), "invalid_argument", testCase.Case);
                Assert.AreEqual(3, adapter.CallsOf(nameof(GridAdapter.Scroll)), "Only the three valid scrolls reached the adapter.");
                Assert.AreEqual((0, 2), await FirstVisibleAsync(app, "Grid"), "Nothing moved.");

                await OnGridAsync(app, "Grid", fake => fake.Sort(1, true));
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(1, null, null)), "no token, changed view: no check");
                JToken stale = await app.ActAsync("scroll", grid, Cell(2, null, token));
                AssertGridFailed(stale, "stale_view", "a supplied token from before the sort");
                Assert.AreEqual("view_changed", (string?)stale["error"]!["details"]!["reason"]);
                Assert.AreEqual((1, 2), await FirstVisibleAsync(app, "Grid"), "The stale scroll did not move the grid.");
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(2, null, await TokenAsync(app, "Grid"))), "the current token");
            }
        }

        [TestMethod]
        public async Task SelectAndScrollOnADataGridViewTakeAnOptionalTokenAndCheckItWhenSupplied()
        {
            using (var app = new TestApplication())
            {
                JObject grid = await app.FindAsync("BigGrid");

                // No token: unchanged behavior.
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(5, 1, null)), "select without a token");
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(20, null, null)), "scroll without a token");
                (int Row, int Column, int First) state = (-1, -1, -1);
                void Capture() => state = ReadDataGridViewState(app);
                await app.OnUiAsync(Capture);
                Assert.AreEqual((5, 1, 20), state);

                // A current token passes.
                string token = (string)(await ReadGridAsync(app, "BigGrid", 0, 1, 0, 1))["viewToken"]!;
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(6, 0, token)), "select with the current token");
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(30, null, token)), "scroll with the current token");
                await app.OnUiAsync(Capture);
                Assert.AreEqual((6, 0, 30), state);

                // The view changes: a supplied old token is stale, no token still works. Ranges come before the token.
                await app.OnUiAsync(() => ((DataGridView)app.Window.Controls["BigGrid"]!).Rows.Add("new", "row"));
                JToken staleSelect = await app.ActAsync("select", grid, Cell(7, 1, token));
                AssertGridFailed(staleSelect, "stale_view", "select with an old token");
                Assert.AreEqual("view_changed", (string?)staleSelect["error"]!["details"]!["reason"]);
                CollectionAssert.AreEquivalent(new[] { "reason" }, Names((JObject)staleSelect["error"]!["details"]!));
                AssertGridFailed(await app.ActAsync("scroll", grid, Cell(40, null, token)), "stale_view", "scroll with an old token");
                AssertGridFailed(await app.ActAsync("select", grid, Cell(9999, 1, token)), "invalid_argument", "range before token");
                AssertGridFailed(await app.ActAsync("select", grid, p => { p["row"] = 7; p["column"] = 1; p["viewToken"] = 5; }),
                    "invalid_argument", "a non-string token");
                await app.OnUiAsync(Capture);
                Assert.AreEqual((6, 0, 30), state, "Nothing moved.");
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(7, 1, null)), "no token, changed view: no check");
            }
        }

        private static (int Row, int Column, int First) ReadDataGridViewState(TestApplication app)
        {
            var grid = (DataGridView)app.Window.Controls["BigGrid"]!;
            DataGridViewCell? current = grid.CurrentCell;
            return (current?.RowIndex ?? -1, current?.ColumnIndex ?? -1, grid.FirstDisplayedScrollingRowIndex);
        }

        [TestMethod]
        public async Task SelectAndScrollOnAnElementTargetAreUnsupportedEvenWithGridArguments()
        {
            var elements = new FakeElementAdapter();
            elements.HostPlacements.Add(Place("Target"));
            var grids = new FakeGridAdapter();
            using (var app = new TestApplication(
                options => { options.AddElementAdapter(elements); options.AddGridAdapter(grids); },
                window =>
                {
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                    window.Controls.Add(NewFakeGrid("Grid"));
                }))
            {
                JObject element = ItemNamed(await app.TreeAsync(), "Target");
                string token = await TokenAsync(app, "Grid");
                foreach (string method in new[] { "select", "scroll" })
                {
                    JToken status = await app.ActAsync(method, element, Cell(0, 0, token));
                    AssertGridFailed(status, "unsupported_operation", method + " on an element");
                    Assert.AreEqual("This operation applies to controls, not elements.", (string?)status["error"]!["message"]);
                }
                Assert.AreEqual(0, grids.CallsOf(nameof(GridAdapter.Select)));
                Assert.AreEqual(0, grids.CallsOf(nameof(GridAdapter.Scroll)));
            }
        }

        [TestMethod]
        public async Task AFailedReceiptCarriesDetailsOnlyWhenTheFailureHasThemAndActionStatusReturnsThemUnchanged()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string old = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake => fake.Sort(2, false));

                JObject parameters = TargetParameters(grid);
                Cell(1, 1, old)(parameters);
                JToken receipt = Success(await app.RequestAsync("select", parameters));
                JToken first = await app.WaitForTerminalAsync(receipt);
                JToken second = Success(await app.RequestAsync("action_status", ReceiptParameters(receipt)));
                AssertGridFailed(first, "stale_view", "stale");
                var details = (JObject)first["error"]!["details"]!;
                Assert.AreEqual("view_changed", (string?)details["reason"]);
                Assert.IsTrue(JToken.DeepEquals(first["error"], second["error"]), "action_status returns the stored error unchanged.");

                JToken plain = await app.ActAsync("select", grid, Cell(-1, null, await TokenAsync(app, "Grid")));
                AssertGridFailed(plain, "invalid_argument", "no details");
                CollectionAssert.AreEquivalent(new[] { "code", "message" }, Names((JObject)plain["error"]!),
                    "A failure without details keeps the two-field error object.");

                JToken other = await app.ActAsync("set_text", await app.FindAsync("ReadOnlyText"), p => p["text"] = "x");
                AssertGridFailed(other, "read_only", "a non-grid failure");
                CollectionAssert.AreEquivalent(new[] { "code", "message" }, Names((JObject)other["error"]!));
            }
        }

        [TestMethod]
        public async Task AnAdapterRefusalMapsToItsWireCodeAndItsMessageIsNotRelayed()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var expected = new (GridOperationError Error, string Code)[]
                {
                    (GridOperationError.Unsupported, "unsupported_operation"),
                    (GridOperationError.ReadOnly, "read_only"),
                    (GridOperationError.Hidden, "hidden_control"),
                    (GridOperationError.NotRealized, "not_realized"),
                    (GridOperationError.StaleView, "stale_view")
                };
                foreach (var testCase in expected)
                {
                    adapter.RefuseSelectWith = testCase.Error;
                    adapter.RefuseScrollWith = testCase.Error;
                    foreach (string method in new[] { "select", "scroll" })
                    {
                        JToken status = await app.ActAsync(method, grid, Cell(1, 1, token));
                        AssertGridFailed(status, testCase.Code, method + " refused with " + testCase.Error);
                        string error = status["error"]!.ToString();
                        foreach (string text in new[] { "Bravo", "cell text", "invalid" })
                            Assert.IsFalse(error.Contains(text), "The refusal's message must not be relayed: " + text);
                        if (testCase.Error == GridOperationError.StaleView)
                            Assert.AreEqual("view_changed", (string?)status["error"]!["details"]!["reason"]);
                        else
                            Assert.IsNull(status["error"]!["details"]);
                    }
                }
                adapter.RefuseSelectWith = null;
                adapter.RefuseScrollWith = null;
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(1, 1, token)), "after the refusals");
            }
        }

        [TestMethod]
        public async Task AnAdapterThatThrowsInSelectOrScrollLeaksNoCellValueIntoTheReceiptOrDiagnostics()
        {
            var adapter = new FakeGridAdapter
            {
                ThrowInSelect = true,
                ThrowInScroll = true,
                // An adapter that puts the offending value into its exception message.
                SelectFailureDetail = " The value 'Bravo' (cell text) is invalid.",
                ScrollFailureDetail = " The value 'Charlie' (cell text) is invalid."
            };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var leaks = new[] { "Alpha", "Bravo", "Charlie", "cell text", "invalid" };

                foreach (string method in new[] { "select", "scroll" })
                {
                    JToken status = await app.ActAsync(method, grid, Cell(1, 1, token));
                    AssertGridFailed(status, "adapter_failed", method + " threw");
                    string message = (string)status["error"]!["message"]!;
                    StringAssert.Contains(message, "InvalidOperationException");
                    StringAssert.Contains(message, method == "select" ? "Select" : "Scroll");
                    StringAssert.Contains(message, "fake-grid");
                    foreach (string text in leaks)
                        Assert.IsFalse(status["error"]!.ToString().Contains(text), "The receipt must not carry cell text or the adapter's message: " + text);
                }

                string diagnostics = string.Join("\n", (await DiagnosticEntriesAsync(app)).Select(entry => entry.ToString()));
                Assert.IsTrue(diagnostics.Contains("Select") && diagnostics.Contains("Scroll"), "The failures are recorded.");
                foreach (string text in leaks)
                    Assert.IsFalse(diagnostics.Contains(text), "Diagnostics must not carry cell text or the adapter's message: " + text);

                adapter.ThrowInSelect = false;
                adapter.ThrowInScroll = false;
                AssertGridCompleted(await app.ActAsync("select", grid, Cell(1, 1, token)), "a failed mutation releases the grid");
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(1, null, null)), "and so for scroll");
            }
        }

        [TestMethod]
        public async Task ASecondGridMutationWhileOneIsStartedFailsGridBusy()
        {
            // The first mutation is held inside a modal form that Select opens before it applies the selection.
            var adapter = new FakeGridAdapter { ModalFormInSelect = () => FakeGrid.NewModal("SelectDialog") };
            using (var app = StartWithGrids(adapter, "Grid", "Other"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                JObject otherGrid = await app.FindAsync("Other");

                JObject firstParameters = TargetParameters(grid);
                Cell(1, 1, token)(firstParameters);
                JToken first = Success(await app.RequestAsync("select", firstParameters));
                JObject close = await app.WaitForNodeAsync("SelectDialogClose");
                Assert.AreEqual("started", (string?)Success(await app.RequestAsync("action_status", ReceiptParameters(first)))["state"]);

                AssertGridFailed(await app.ActAsync("select", grid, Cell(2, 2, token)), "grid_busy", "a second select");
                AssertGridFailed(await app.ActAsync("scroll", grid, Cell(2, null, null)), "grid_busy", "a scroll");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Select), "Grid"), "The refused select never reached the adapter.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Scroll), "Grid"), "The refused scroll never reached the adapter.");

                // Another grid is independent of the busy one.
                AssertGridCompleted(await app.ActAsync("scroll", otherGrid, Cell(2, null, null)), "a different grid");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Scroll), "Other"));

                // Reads are not mutations.
                Assert.AreEqual(JTokenType.Null, (await ReadGridAsync(app, "Grid"))["focusedRow"]!.Type, "The held select has not applied yet.");

                // Closing the dialog lets the first mutation finish, and it applies.
                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(close)));
                await app.WaitForCompletionAsync(closing);
                await app.WaitForCompletionAsync(first);
                JObject page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual(1, (int)page["focusedRow"]!);
                Assert.AreEqual(1, (int)page["focusedColumn"]!);

                // The flag is cleared: the grid takes mutations again (this select opens a second dialog, so use scroll).
                AssertGridCompleted(await app.ActAsync("scroll", grid, Cell(2, null, null)), "after the first completed");
            }
        }

        // --- set_cell (the edit mutation) ------------------------------------------------------------------

        private static Action<JObject> Edit(int? row, int? column, string? text, string? token, string? expected = null) => p =>
        {
            if (row.HasValue) p["row"] = row.Value;
            if (column.HasValue) p["column"] = column.Value;
            if (text != null) p["text"] = text;
            if (token != null) p["viewToken"] = token;
            if (expected != null) p["expectedText"] = expected;
        };

        private static int SetCellCalls(FakeGridAdapter adapter) => adapter.CallsOf(nameof(GridAdapter.SetCell));

        // Every adapter member the bridge could call for a set_cell request.
        private static int AdapterCalls(FakeGridAdapter adapter)
            => adapter.CallsOf(nameof(GridAdapter.GetFeatures)) + adapter.CallsOf(nameof(GridAdapter.GetViewInfo)) +
               adapter.CallsOf(nameof(GridAdapter.DescribeColumn)) + adapter.CallsOf(nameof(GridAdapter.GetCellText)) +
               adapter.CallsOf(nameof(GridAdapter.SetCell));

        private static string CellOf(JObject page, int row, int column) => (string)page["rows"]![row]!["cells"]![column]!;

        [TestMethod]
        public async Task SetCellEditsTheCellAndReportsTheDocumentedResult()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "Bluebird", token));

                AssertGridCompleted(status, "set_cell");
                var result = (JObject)status["result"]!;
                CollectionAssert.AreEquivalent(
                    new[] { "operation", "handlerReturned", "committed", "displayText", "displayTextRedacted", "rowAfter", "viewToken" }, Names(result));
                Assert.AreEqual("set_cell", (string?)result["operation"]);
                Assert.AreEqual(true, (bool?)result["committed"]);
                Assert.AreEqual("Bluebird", (string?)result["displayText"]);
                Assert.AreEqual(false, (bool?)result["displayTextRedacted"]);
                Assert.AreEqual(1, (int?)result["rowAfter"]);
                Assert.AreEqual(token, (string?)result["viewToken"], "The edit did not change the view.");
                Assert.AreEqual(1, SetCellCalls(adapter));

                JObject page = await ReadGridAsync(app, "Grid");
                Assert.AreEqual("Bluebird", CellOf(page, 1, 1));
                Assert.AreEqual("Alpha", CellOf(page, 0, 1));
                Assert.AreEqual(token, (string?)page["viewToken"]);

                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(2, 2, "", token)), "an empty text is a valid text");
                Assert.AreEqual("", CellOf(await ReadGridAsync(app, "Grid"), 2, 2));
            }
        }

        [TestMethod]
        public async Task AnEditThatResortsTheViewChangesTheTokenAndReportsRowAfter()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                await OnGridAsync(app, "Grid", fake => fake.Sort(1, false));
                string before = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(0, 1, "Zulu", before));

                AssertGridCompleted(status, "an edit on the sort column");
                var result = (JObject)status["result"]!;
                Assert.AreEqual(2, (int?)result["rowAfter"], "Alpha became Zulu and moved to the end.");
                string after = (string)result["viewToken"]!;
                Assert.AreNotEqual(before, after, "The view re-sorted.");
                Assert.AreEqual(after, await TokenAsync(app, "Grid"), "The receipt carries the token a read now reports.");
                Assert.AreEqual("Zulu", CellOf(await ReadGridAsync(app, "Grid"), 2, 1));

                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "Again", before)), "stale_view", "the token from before the edit");
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(0, 1, "Again", after)), "the token from the receipt");
            }
        }

        [TestMethod]
        public async Task RowAfterIsNullWhenTheRowLeftTheView()
        {
            var adapter = new FakeGridAdapter { ReportRowAfter = -4 };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "Gone", await TokenAsync(app, "Grid")));
                AssertGridCompleted(status, "set_cell");
                Assert.AreEqual(JTokenType.Null, status["result"]!["rowAfter"]!.Type);
            }
        }

        [TestMethod]
        public async Task SetCellOnAHiddenOrReadOnlyColumnFailsAndNeverCallsTheAdapter()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 0, "x", token)), "read_only", "the read-only id column");
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Visible = false);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "x", token)), "hidden_control", "the hidden column");
                await OnGridAsync(app, "Grid", fake => fake.Columns[0].Visible = false);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 0, "x", token)), "hidden_control", "hidden comes before read-only");
                Assert.AreEqual(0, SetCellCalls(adapter), "SetCell is not called in these cases.");
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(0, 2, "7", token)), "a visible editable column after the failures");
                Assert.AreEqual(1, SetCellCalls(adapter));
            }
        }

        [TestMethod]
        public async Task SetCellIsUnsupportedWithoutTheEditFeatureOnADataGridViewAndOnAnElement()
        {
            var elements = new FakeElementAdapter();
            elements.HostPlacements.Add(Place("Target"));
            var adapter = new FakeGridAdapter { Features = GridFeatures.Select | GridFeatures.Scroll, AcceptFilter = control => control.Name != "Refused" };
            using (var app = new TestApplication(
                options => { options.AddElementAdapter(elements); options.AddGridAdapter(adapter); },
                window =>
                {
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                    window.Controls.Add(NewFakeGrid("Grid"));
                    window.Controls.Add(NewFakeGrid("Refused"));
                }))
            {
                string token = await TokenAsync(app, "Grid");
                JObject grid = await app.FindAsync("Grid");
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "unsupported_operation", "an adapter without Edit");
                AssertGridFailed(await app.ActAsync("set_cell", await app.FindAsync("Refused"), Edit(1, 1, "x", token)), "unsupported_operation", "an unaccepted grid");
                AssertGridFailed(await app.ActAsync("set_cell", await app.FindAsync("Replaceable"), Edit(1, 1, "x", token)), "unsupported_operation", "a button");

                JObject bigGrid = await app.FindAsync("BigGrid");
                string bigToken = (string)(await ReadGridAsync(app, "BigGrid", 0, 1, 0, 1))["viewToken"]!;
                AssertGridFailed(await app.ActAsync("set_cell", bigGrid, Edit(0, 0, "changed", bigToken)), "unsupported_operation", "a DataGridView");
                Assert.AreEqual("a0", (string?)(await ReadGridAsync(app, "BigGrid", 0, 1, 0, 1))["rows"]![0]!["cells"]![0], "The DataGridView is untouched.");

                JToken onElement = await app.ActAsync("set_cell", ItemNamed(await app.TreeAsync(), "Target"), Edit(1, 1, "x", token));
                AssertGridFailed(onElement, "unsupported_operation", "an element");
                Assert.AreEqual("This operation applies to controls, not elements.", (string?)onElement["error"]!["message"]);
                Assert.AreEqual(0, SetCellCalls(adapter));

                adapter.Features = GridFeatures.Edit;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "with the Edit feature");
            }
        }

        [TestMethod]
        public async Task SetCellOnADisabledOrHiddenGridFailsBeforeTheAdapterIsCalled()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake => fake.Enabled = false);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "disabled_control", "disabled grid");
                await OnGridAsync(app, "Grid", fake => { fake.Enabled = true; fake.Visible = false; });
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "hidden_control", "hidden grid");
                Assert.AreEqual(0, SetCellCalls(adapter));
            }
        }

        [TestMethod]
        public async Task SetCellValidatesEveryRequiredArgumentBeforeTheAdapterIsAskedAnything()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var cases = new (string Case, Action<JObject> Configure)[]
                {
                    ("nothing", Edit(null, null, null, null)),
                    ("no row", Edit(null, 1, "x", token)),
                    ("no column", Edit(1, null, "x", token)),
                    ("no text", Edit(1, 1, null, token)),
                    ("no token", Edit(1, 1, "x", null)),
                    ("null text", p => { Edit(1, 1, null, token)(p); p["text"] = JValue.CreateNull(); }),
                    ("null token", p => { Edit(1, 1, "x", null)(p); p["viewToken"] = JValue.CreateNull(); }),
                    ("empty token", Edit(1, 1, "x", "")),
                    ("numeric token", p => { Edit(1, 1, "x", null)(p); p["viewToken"] = 5; }),
                    ("numeric text", p => { Edit(1, 1, null, token)(p); p["text"] = 5; }),
                    ("string row", p => { Edit(null, 1, "x", token)(p); p["row"] = "1"; }),
                    ("string column", p => { Edit(1, null, "x", token)(p); p["column"] = "1"; }),
                    ("numeric expectedText", p => { Edit(1, 1, "x", token)(p); p["expectedText"] = 5; })
                };
                int before = AdapterCalls(adapter);
                foreach (var testCase in cases)
                    AssertGridFailed(await app.ActAsync("set_cell", grid, testCase.Configure), "invalid_argument", testCase.Case);
                Assert.AreEqual(before, AdapterCalls(adapter), "No adapter member (not even GetFeatures or GetViewInfo) is called for a malformed request.");
            }
        }

        [TestMethod]
        public async Task SetCellRefusesATextOverTheLimitAndIndexesOutOfRange()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var cases = new (string Case, Action<JObject> Configure)[]
                {
                    ("text over the limit", Edit(1, 1, new string('x', 4097), token)),
                    ("row -1", Edit(-1, 1, "x", token)),
                    ("row 3 of 3", Edit(3, 1, "x", token)),
                    ("column -1", Edit(1, -1, "x", token)),
                    ("column 3 of 3", Edit(1, 3, "x", token))
                };
                foreach (var testCase in cases)
                {
                    JToken status = await app.ActAsync("set_cell", grid, testCase.Configure);
                    AssertGridFailed(status, "invalid_argument", testCase.Case);
                    Assert.IsFalse(status["error"]!.ToString().Contains("xxxx"), "The edit text is not echoed.");
                }
                Assert.AreEqual(0, SetCellCalls(adapter));
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(2, 2, new string('x', 4096), token)), "a text of exactly the limit");
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(2, 1, "y", token)), "the last row is in range");

                await FillGridAsync(app, "Grid", 0, 3);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 0, "x", await TokenAsync(app, "Grid"))), "invalid_argument", "row 0 of an empty grid");
            }
        }

        [TestMethod]
        public async Task AStaleViewTokenFailsSetCellAndAnExpectedTextMismatchFailsCellChanged()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string old = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake => fake.Sort(1, true));
                string current = await TokenAsync(app, "Grid");

                JToken stale = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", old));
                AssertGridFailed(stale, "stale_view", "an old token");
                CollectionAssert.AreEquivalent(new[] { "code", "message", "details" }, Names((JObject)stale["error"]!));
                Assert.AreEqual("view_changed", (string?)stale["error"]!["details"]!["reason"]);
                CollectionAssert.AreEquivalent(new[] { "reason" }, Names((JObject)stale["error"]!["details"]!));
                foreach (string secret in new[] { old, current, TokenSalt(current) })
                    Assert.IsFalse(stale["error"]!.ToString().Contains(secret), "No token in the error.");
                Assert.AreEqual(0, SetCellCalls(adapter));

                // The view is sorted descending: row 1 is Bravo.
                JToken mismatch = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", current, "bravo"));
                AssertGridFailed(mismatch, "stale_view", "expectedText differs (ordinal, case counts)");
                Assert.AreEqual("cell_changed", (string?)mismatch["error"]!["details"]!["reason"]);
                CollectionAssert.AreEquivalent(new[] { "reason" }, Names((JObject)mismatch["error"]!["details"]!));
                Assert.IsFalse(mismatch["error"]!.ToString().Contains("Bravo"), "The current cell text is not echoed.");
                Assert.AreEqual(0, SetCellCalls(adapter));
                Assert.AreEqual("Bravo", CellOf(await ReadGridAsync(app, "Grid"), 1, 1));

                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "Bluebird", current, "Bravo")), "expectedText equals the cell text");
                Assert.AreEqual(1, SetCellCalls(adapter));

                // An empty expectedText matches an empty cell.
                await OnGridAsync(app, "Grid", fake => fake.Records[0]["qty"] = "");
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(2, 2, "5", await TokenAsync(app, "Grid"), "")), "empty expectedText on an empty cell");
            }
        }

        [TestMethod]
        public async Task ExpectedTextOnATruncatedCellIsRefused()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                string longText = new string('L', 5000);
                await OnGridAsync(app, "Grid", fake => fake.Records[1]["name"] = longText);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                foreach (string expected in new[] { longText.Substring(0, 4096), longText })
                {
                    JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token, expected));
                    AssertGridFailed(status, "invalid_argument", "expectedText on a cell longer than the limit");
                    Assert.AreEqual("cell_truncated", (string?)status["error"]!["details"]!["reason"]);
                }
                Assert.AreEqual(0, SetCellCalls(adapter));
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "the plain token check still applies and passes");
                Assert.AreEqual(1, SetCellCalls(adapter));
            }
        }

        [TestMethod]
        public async Task ExpectedTextOnARedactedColumnIsRefusedAndItsTextIsNeverRequested()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Redacted = true);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                int reads = adapter.CallsOf(nameof(GridAdapter.GetCellText)); // the token read asked for one cell of the id column

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token, "Bravo"));
                AssertGridFailed(status, "invalid_argument", "expectedText on a redacted column");
                Assert.AreEqual("column_redacted", (string?)status["error"]!["details"]!["reason"]);
                Assert.AreEqual(reads, adapter.CallsOf(nameof(GridAdapter.GetCellText)), "A redacted column's text is never requested.");
                Assert.AreEqual(0, SetCellCalls(adapter));

                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "an edit without expectedText is allowed");
                Assert.AreEqual(reads, adapter.CallsOf(nameof(GridAdapter.GetCellText)), "Neither the edit nor the receipt reads the cell text.");
            }
        }

        [TestMethod]
        public async Task AnEditTheApplicationRefusesIsAFailedReceiptWithReasonErrorTextAndDisplayText()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var expected = new Dictionary<GridEditRejection, string>
                {
                    [GridEditRejection.NotEditable] = "not_editable",
                    [GridEditRejection.EditorNotShown] = "editor_not_shown",
                    [GridEditRejection.InvalidInput] = "invalid_input",
                    [GridEditRejection.AmbiguousInput] = "ambiguous_input",
                    [GridEditRejection.ValidationFailed] = "validation_failed",
                    [GridEditRejection.RowNotCommitted] = "row_not_committed",
                    [GridEditRejection.Other] = "other"
                };
                CollectionAssert.AreEquivalent(Enum.GetValues(typeof(GridEditRejection)).Cast<GridEditRejection>().ToArray(), expected.Keys.ToArray(),
                    "Every rejection reason is covered.");

                foreach (KeyValuePair<GridEditRejection, string> testCase in expected)
                {
                    adapter.ForcedRejection = testCase.Key;
                    adapter.ForcedErrorText = "Refused: " + testCase.Value;
                    JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token));
                    AssertGridFailed(status, "edit_rejected", testCase.Key.ToString());
                    var details = (JObject)status["error"]!["details"]!;
                    CollectionAssert.AreEquivalent(new[] { "reason", "errorText", "displayText" }, Names(details));
                    Assert.AreEqual(testCase.Value, (string?)details["reason"]);
                    Assert.AreEqual("Refused: " + testCase.Value, (string?)details["errorText"]);
                    Assert.AreEqual("Bravo", (string?)details["displayText"], "The text now shown in the cell.");
                    Assert.IsFalse(((string)status["error"]!["message"]!).Contains("Refused"), "The application's text is only in the details.");
                }

                adapter.ForcedErrorText = new string('e', 5000);
                var longError = (JObject)(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)))["error"]!["details"]!;
                Assert.AreEqual(1024, ((string)longError["errorText"]!).Length, "The application's error text is cut to 1,024 characters.");
                adapter.ForcedErrorText = null;
                var noError = (JObject)(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)))["error"]!["details"]!;
                Assert.AreEqual(JTokenType.Null, noError["errorText"]!.Type);

                adapter.ForcedRejection = null;
                Assert.AreEqual("Bravo", CellOf(await ReadGridAsync(app, "Grid"), 1, 1), "A refused edit changed nothing.");
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "a refusal releases the grid");

                // The application's own validation hook, with its message.
                await OnGridAsync(app, "Grid", fake => fake.EditValidation = context =>
                {
                    if (context.Text.StartsWith("-")) context.ErrorText = "Quantity must not be negative.";
                });
                JToken refused = await app.ActAsync("set_cell", grid, Edit(1, 2, "-5", token));
                AssertGridFailed(refused, "edit_rejected", "the validation hook");
                Assert.AreEqual("validation_failed", (string?)refused["error"]!["details"]!["reason"]);
                Assert.AreEqual("Quantity must not be negative.", (string?)refused["error"]!["details"]!["errorText"]);
                Assert.AreEqual("10", (string?)refused["error"]!["details"]!["displayText"]);
            }
        }

        [TestMethod]
        public async Task AViewChangeDuringTheEditIsStaleViewAndNothingIsWritten()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");

                // An event-driven change while the editor is being opened.
                string token = await TokenAsync(app, "Grid");
                adapter.BeforeViewCheck = fake => fake.Sort(1, true);
                JToken status = await app.ActAsync("set_cell", grid, Edit(0, 1, "x", token));
                AssertGridFailed(status, "stale_view", "a sort during the edit");
                Assert.AreEqual("view_changed", (string?)status["error"]!["details"]!["reason"]);
                Assert.AreEqual(1, SetCellCalls(adapter), "The adapter was called and found the view changed.");

                // A change that only the fingerprint sees.
                token = await TokenAsync(app, "Grid");
                adapter.BeforeViewCheck = fake => adapter.Fingerprint = adapter.Fingerprint + 1;
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "x", token)), "stale_view", "a fingerprint change during the edit");

                // Nothing changes: the live check is false and the edit is committed.
                token = await TokenAsync(app, "Grid");
                adapter.BeforeViewCheck = null;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(0, 1, "Done", token)), "no change during the edit");

                JObject page = await ReadGridAsync(app, "Grid");
                var names = Enumerable.Range(0, 3).Select(row => CellOf(page, row, 1)).ToArray();
                CollectionAssert.Contains(names, "Done");
                CollectionAssert.DoesNotContain(names, "x", "The refused edits wrote nothing.");
            }
        }

        [TestMethod]
        public async Task AGridDisposedBetweenReadAndEditFailsStaleTargetAndNeverCallsTheAdapter()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                await app.OnUiAsync(() =>
                {
                    Control doomed = app.Window.Controls["Grid"]!;
                    app.Window.Controls.Remove(doomed);
                    doomed.Dispose();
                });
                int before = AdapterCalls(adapter);

                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "stale_target", "the grid was disposed after the read");

                Assert.AreEqual(before, AdapterCalls(adapter), "The adapter is not called for a disposed grid.");
                Assert.AreEqual(0, SetCellCalls(adapter));
            }
        }

        [TestMethod]
        public async Task AGridDisposedDuringTheEditStillCompletesWithoutATokenAndWithoutADiagnostic()
        {
            var adapter = new FakeGridAdapter { AfterCommit = fake => fake.Dispose() };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "Bluebird", token));

                AssertGridCompleted(status, "the grid was disposed by the handler after the commit");
                Assert.AreEqual(true, (bool?)status["result"]!["committed"]);
                Assert.AreEqual(JTokenType.Null, status["result"]!["viewToken"]!.Type, "A disposed grid has no view to read a token from.");
                Assert.IsFalse((await DiagnosticEntriesAsync(app)).Any(entry => (string?)entry["category"] == "adapter_failed"),
                    "The disposed grid was not asked for its view.");
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "stale_target", "the grid is gone");
            }
        }

        [TestMethod]
        public async Task AModalEditKeepsTheReceiptStartedAndASecondEditFailsGridBusyUntilTheDialogIsClosed()
        {
            var adapter = new FakeGridAdapter { ModalFormInSetCell = () => FakeGrid.NewModal("EditDialog") };
            using (var app = StartWithGrids(adapter, "Grid", "Other"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JObject firstParameters = TargetParameters(grid);
                Edit(1, 1, "Bluebird", token)(firstParameters);
                JToken first = Success(await app.RequestAsync("set_cell", firstParameters));
                JObject close = await app.WaitForNodeAsync("EditDialogClose");
                Assert.AreEqual("started", (string?)Success(await app.RequestAsync("action_status", ReceiptParameters(first)))["state"]);

                JToken windows = Success(await app.RequestAsync("windows"));
                Assert.IsTrue(((JArray)windows["windows"]!).Any(window => (string?)window["name"] == "EditDialog"), "list_windows shows the dialog.");

                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(2, 1, "Other", token)), "grid_busy", "a second edit");
                AssertGridFailed(await app.ActAsync("select", grid, Cell(2, 1, token)), "grid_busy", "a select");
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.SetCell), "Grid"), "The refused edit never reached the adapter.");
                AssertGridCompleted(await app.ActAsync("scroll", await app.FindAsync("Other"), Cell(2, null, null)), "a different grid is independent");
                Assert.AreEqual("Bravo", CellOf(await ReadGridAsync(app, "Grid"), 1, 1), "The held edit has not been applied.");

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(close)));
                await app.WaitForCompletionAsync(closing);
                JToken status = await app.WaitForTerminalAsync(first);
                AssertGridCompleted(status, "closing the dialog completes the receipt");
                Assert.AreEqual(true, (bool?)status["result"]!["committed"]);
                Assert.AreEqual("Bluebird", CellOf(await ReadGridAsync(app, "Grid"), 1, 1));

                adapter.ModalFormInSetCell = null;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(2, 1, "Again", token)), "the flag is cleared");
            }
        }

        [TestMethod]
        public async Task AnEditOnARedactedColumnNeverEchoesTheEditTextOrTheCellText()
        {
            const string secret = "S3cr3t-Value-42";
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Redacted = true);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var everything = new List<string>();

                JToken committed = await app.ActAsync("set_cell", grid, Edit(1, 1, secret, token));
                AssertGridCompleted(committed, "an edit on a redacted column is allowed");
                Assert.AreEqual(JTokenType.Null, committed["result"]!["displayText"]!.Type);
                Assert.AreEqual(true, (bool?)committed["result"]!["displayTextRedacted"]);
                Assert.AreEqual(1, (int?)committed["result"]!["rowAfter"]);
                everything.Add(committed.ToString());

                adapter.ForcedRejection = GridEditRejection.ValidationFailed;
                adapter.ForcedErrorText = "The value '" + secret + "' is not allowed.";
                JToken rejected = await app.ActAsync("set_cell", grid, Edit(1, 1, secret, token));
                AssertGridFailed(rejected, "edit_rejected", "a rejection on a redacted column");
                var details = (JObject)rejected["error"]!["details"]!;
                Assert.AreEqual("validation_failed", (string?)details["reason"]);
                Assert.AreEqual(JTokenType.Null, details["displayText"]!.Type);
                Assert.AreEqual(JTokenType.Null, details["errorText"]!.Type, "The application's message may quote the value.");
                Assert.AreEqual(true, (bool?)details["displayTextRedacted"]);
                everything.Add(rejected.ToString());
                adapter.ForcedRejection = null;

                adapter.ThrowInSetCell = true;
                adapter.SetCellFailureDetail = " The value '" + secret + "' is invalid.";
                JToken failed = await app.ActAsync("set_cell", grid, Edit(1, 1, secret, token));
                AssertGridFailed(failed, "adapter_failed", "a throwing adapter");
                everything.Add(failed.ToString());
                adapter.ThrowInSetCell = false;

                JObject page = await ReadGridAsync(app, "Grid");
                everything.Add(page.ToString());
                Assert.AreEqual(JTokenType.Null, page["rows"]![1]!["cells"]![1]!.Type);
                everything.Add(string.Join("\n", (await DiagnosticEntriesAsync(app)).Select(entry => entry.ToString())));
                everything.Add(Success(await app.RequestAsync("action_status", ReceiptParameters(committed))).ToString());

                foreach (string text in everything)
                {
                    Assert.IsFalse(text.Contains(secret), "The secret appears in no read, receipt or diagnostic.");
                    Assert.IsFalse(text.Contains("is invalid"), "Nor does the adapter's message.");
                }
            }
        }

        [TestMethod]
        public async Task AnAdapterThatThrowsInSetCellGivesAdapterFailedWithoutTheEditText()
        {
            const string edit = "Bluebird-Edit";
            var adapter = new FakeGridAdapter { ThrowInSetCell = true, SetCellFailureDetail = " The value '" + edit + "' (Bravo) is invalid." };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, edit, token));

                AssertGridFailed(status, "adapter_failed", "SetCell threw");
                string message = (string)status["error"]!["message"]!;
                StringAssert.Contains(message, "InvalidOperationException");
                StringAssert.Contains(message, "SetCell");
                StringAssert.Contains(message, "fake-grid");
                string diagnostics = string.Join("\n", (await DiagnosticEntriesAsync(app)).Select(entry => entry.ToString()));
                Assert.IsTrue(diagnostics.Contains("SetCell"), "The failure is recorded.");
                foreach (string text in new[] { edit, "Bravo", "invalid" })
                {
                    Assert.IsFalse(status["error"]!.ToString().Contains(text), "The receipt must not carry the edit, the cell text or the adapter's message: " + text);
                    Assert.IsFalse(diagnostics.Contains(text), "Diagnostics must not carry them either: " + text);
                }

                adapter.ThrowInSetCell = false;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, edit, token)), "a failed edit releases the grid");
            }
        }

        [TestMethod]
        public async Task AnAdapterRefusalInSetCellMapsToItsWireCodeAndItsMessageIsNotRelayed()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var expected = new (GridOperationError Error, string Code)[]
                {
                    (GridOperationError.Unsupported, "unsupported_operation"),
                    (GridOperationError.ReadOnly, "read_only"),
                    (GridOperationError.Hidden, "hidden_control"),
                    (GridOperationError.NotRealized, "not_realized"),
                    (GridOperationError.StaleView, "stale_view")
                };
                foreach (var testCase in expected)
                {
                    adapter.RefuseSetCellWith = testCase.Error;
                    JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token));
                    AssertGridFailed(status, testCase.Code, "SetCell refused with " + testCase.Error);
                    foreach (string text in new[] { "Bravo", "cell text", "invalid" })
                        Assert.IsFalse(status["error"]!.ToString().Contains(text), "The refusal's message must not be relayed: " + text);
                    if (testCase.Error == GridOperationError.StaleView)
                        Assert.AreEqual("view_changed", (string?)status["error"]!["details"]!["reason"]);
                    else
                        Assert.IsNull(status["error"]!["details"]);
                }
                adapter.RefuseSetCellWith = null;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "x", token)), "after the refusals");
            }
        }

        [TestMethod]
        public async Task SetCellChecksRunInTheDocumentedOrder()
        {
            var adapter = new FakeGridAdapter { Features = GridFeatures.Select };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string old = await TokenAsync(app, "Grid");
                await OnGridAsync(app, "Grid", fake =>
                {
                    fake.Sort(1, true);
                    fake.Columns[1].Visible = false;
                    fake.Columns[1].ReadOnly = true;
                });
                string tooLong = new string('x', 4097);

                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(99, 1, tooLong, old, "nope")), "unsupported_operation",
                    "the Edit feature comes before everything else");
                adapter.Features = GridFeatures.Edit;
                JToken byText = await app.ActAsync("set_cell", grid, Edit(99, 1, tooLong, old, "nope"));
                AssertGridFailed(byText, "invalid_argument", "the text length comes before the range");
                StringAssert.Contains((string)byText["error"]!["message"]!, "text");
                JToken byRange = await app.ActAsync("set_cell", grid, Edit(99, 1, "x", old, "nope"));
                AssertGridFailed(byRange, "invalid_argument", "the range comes before the token");
                StringAssert.Contains((string)byRange["error"]!["message"]!, "row");
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "x", old, "nope")), "stale_view", "the token comes before the column checks");
                string current = await TokenAsync(app, "Grid");
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "x", current, "nope")), "hidden_control", "the hidden column comes before read-only");
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Visible = true);
                AssertGridFailed(await app.ActAsync("set_cell", grid, Edit(0, 1, "x", current, "nope")), "read_only", "read-only comes before expectedText");
                JToken byExpected = await app.ActAsync("set_cell", grid, Edit(0, 2, "x", current, "nope"));
                AssertGridFailed(byExpected, "stale_view", "expectedText comes last");
                Assert.AreEqual("cell_changed", (string?)byExpected["error"]!["details"]!["reason"]);
                Assert.AreEqual(0, SetCellCalls(adapter));
            }
        }

        [TestMethod]
        public async Task HelloAdvertisesSetCellAmongTheMethods()
        {
            using (var app = StartWithGrids(new FakeGridAdapter(), "Grid"))
            {
                JToken hello = Success(await app.RequestAsync("hello"));
                string[] methods = ((JArray)hello["methods"]!).Select(method => (string)method!).ToArray();
                CollectionAssert.Contains(methods, "set_cell");
                CollectionAssert.AreEqual(methods.Distinct().ToArray(), methods, "No duplicates.");
            }
        }

        // --- no leak ------------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ShowCollectableGridForm()
        {
            var form = new Form { Name = "CollectableForm", Width = 300, Height = 200 };
            FakeGrid grid = NewFakeGrid("Collectable");
            form.Controls.Add(grid);
            form.Show();
            return new WeakReference(grid);
        }

        // Opens the grid's form, lets the bridge describe it (Accept and Attach), raises the grid's event once so the
        // handler, signal and state are all in use, closes the form and reports whether the grid was collected.
        private static async Task<bool> GridIsCollectedAfterItsFormClosesAsync(FakeGridAdapter adapter)
        {
            using (var app = StartWithGrids(adapter))
            {
                WeakReference? weak = null;
                await app.OnUiAsync(() => weak = ShowCollectableGridForm());
                Assert.IsTrue(Capabilities(await app.FindAsync("Collectable")).Contains("read_grid"));
                Assert.AreEqual(1, adapter.CallsOf(nameof(GridAdapter.Attach), "Collectable"), "Attach subscribed a handler.");
                await app.OnUiAsync(() => ((FakeGrid)weak!.Target!).Sort(1, descending: false));
                await app.OnUiAsync(() => Application.OpenForms["CollectableForm"]!.Close());

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    if (!weak!.IsAlive) return true;
                    await Task.Delay(50);
                }
                return false;
            }
        }

        [TestMethod]
        public async Task AGridIsCollectedAfterItsFormClosesEvenThoughAttachSubscribedAHandler()
        {
            var adapter = new FakeGridAdapter();
            Assert.IsTrue(await GridIsCollectedAfterItsFormClosesAsync(adapter),
                "The grid, its event handler, the signal and the freshness state must be collected together.");
            Assert.AreEqual(1, adapter.SignalReferences.Length);
            Assert.IsFalse(adapter.SignalReferences[0].IsAlive, "Nothing keeps the signal once the grid is gone.");
        }

        [TestMethod]
        public async Task AGridIsCollectedEvenWhenTheAdapterKeepsEverySignalItWasGiven()
        {
            // An adapter may keep signals in a field (the contract forbids keeping grids). Then the signal and its
            // state are long-lived, so they must not lead back to the grid.
            var adapter = new FakeGridAdapter { KeepSignals = true };
            Assert.IsTrue(await GridIsCollectedAfterItsFormClosesAsync(adapter),
                "A signal kept by the adapter must not keep the grid alive: the signal may point only at the state, the state not at the grid.");
            Assert.AreEqual(1, adapter.SignalReferences.Length);
            Assert.IsTrue(adapter.SignalReferences[0].IsAlive, "The adapter keeps the signal, so it outlives the grid.");
        }

        // --- a handled grid that is unusable, and the one resolver ----------------------------------------

        [TestMethod]
        public async Task AGridWhoseAttachThrewHasNoTokenAndNoResolution()
        {
            var adapter = new FakeGridAdapter { ThrowInAttach = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.TreeAsync();
                foreach (var failure in new[]
                {
                    await GridHostFailureAsync(app, "CurrentToken", "Grid"),
                    await GridHostFailureAsync(app, "CheckToken", "Grid", "0000000000000000.0"),
                    await GridHostFailureAsync(app, "CheckTokenIfSupplied", "Grid", "0000000000000000.0"),
                    await GridHostFailureAsync(app, "Refresh", "Grid"),
                    await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.None)
                })
                {
                    Assert.AreEqual("adapter_failed", failure?.Code, "Without Attach no event reaches the token, so it must not be offered.");
                    Assert.IsFalse(failure!.Value.Message.Contains("0000000000000000"));
                }
                Assert.IsNull(await CheckTokenIfSuppliedAsync(app, "Grid", null), "No token supplied means no check.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.GetViewInfo), "Grid"), "A failed grid is not read.");
            }
        }

        [TestMethod]
        public async Task ResolveSeparatesUnsupportedFromFailedAndChecksTheRequiredFeature()
        {
            var adapter = new FakeGridAdapter { Features = GridFeatures.Select };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await app.TreeAsync();
                Assert.IsNull(await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.None), "Reading needs no feature.");
                Assert.IsNull(await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.Select));
                Assert.AreEqual("unsupported_operation", (await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.Edit))?.Code);
                Assert.AreEqual("unsupported_operation", (await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.Select | GridFeatures.Edit))?.Code);
                Assert.AreEqual("unsupported_operation", (await GridHostFailureAsync(app, "Resolve", "Replaceable", GridFeatures.None))?.Code,
                    "A control no adapter handles.");

                adapter.ThrowInGetFeatures = true;
                Assert.AreEqual("adapter_failed", (await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.None))?.Code);
                adapter.ThrowInGetFeatures = false;
                Assert.IsNull(await GridHostFailureAsync(app, "Resolve", "Grid", GridFeatures.Select), "A transient failure recovers.");
            }
        }

        [TestMethod]
        public async Task AnAcceptsThatReEntersTheBridgeForTheSameControlDoesNotFailTheWalk()
        {
            var adapter = new FakeGridAdapter();
            TestApplication? holder = null;
            int reentries = 0;
            object? innerResult = new object();
            adapter.OnAccepts = control =>
            {
                // A nested request describing the same control while the adapter pumps messages looks like this.
                if (Interlocked.Increment(ref reentries) != 1) return;
                innerResult = GridInternals.Result(GridInternals.GridHostOf(holder!), "AdapterFor", control);
            };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                holder = app;
                JArray tree = await app.TreeAsync();
                Assert.AreEqual(1, Volatile.Read(ref reentries), "Accepts runs once for the control; the nested call found the entry being created.");
                Assert.IsNull(innerResult, "The nested call sees the control as not handled yet.");
                Assert.IsTrue(Capabilities(ControlNamed(tree, "Grid")).Contains("read_grid"), "The outer call completes normally.");
            }
        }

        // --- scope --------------------------------------------------------------------------------------

        [TestMethod]
        public async Task AGridInsideAnExcludedParentIsNotDescribedAndItsOldIdIsStale()
        {
            var adapter = new FakeGridAdapter();
            using (var app = new TestApplication(
                options => options.AddGridAdapter(adapter),
                window =>
                {
                    window.Controls.Add(NewFakeGrid("Movable"));
                    window.Controls["Excluded"]!.Controls.Add(NewFakeGrid("InExcluded"));
                }))
            {
                JArray tree = await app.TreeAsync();
                Assert.IsFalse(tree.Any(node => (string?)node["name"] == "InExcluded"), "A grid in an excluded subtree is not described.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.Accepts), "InExcluded"), "The adapter is never consulted for it.");
                Assert.AreEqual(0, adapter.CallsOf(nameof(GridAdapter.GetFeatures), "InExcluded"));

                JObject movable = ControlNamed(tree, "Movable");
                Assert.IsTrue(Capabilities(movable).Contains("read_grid"));
                await app.OnUiAsync(() => app.Window.Controls["Excluded"]!.Controls.Add(app.Window.Controls["Movable"]!));

                Assert.AreEqual("stale_target", (await app.RequestAsync("inspect", TargetParameters(movable))).Error?.Code);
                Assert.AreEqual("stale_target", (await app.RequestAsync("read_grid", TargetParameters(movable))).Error?.Code);
                Assert.IsFalse((await app.TreeAsync()).Any(node => (string?)node["id"] == (string?)movable["id"]));
            }
        }

        // --- AddAction with a predicate -----------------------------------------------------------------

        [TestMethod]
        public async Task APredicateActionIsAdvertisedOnlyWhereThePredicateIsTrue()
        {
            using (var app = new TestApplication(options => options.AddAction("poke", control => control is Button && control.Name == "Replaceable", control => { })))
            {
                JArray tree = await app.TreeAsync();
                Assert.IsTrue(Capabilities(ControlNamed(tree, "Replaceable")).Contains("poke"));
                Assert.IsFalse(Capabilities(ControlNamed(tree, "RecreatedHandle")).Contains("poke"), "Another button.");
                Assert.IsFalse(Capabilities(ControlNamed(tree, "PlainLabel")).Contains("poke"), "Not a button.");
            }
        }

        [TestMethod]
        public async Task AThrowingPredicateIsNotAdvertisedAndLogsOneDiagnosticPerAction()
        {
            using (var app = new TestApplication(options =>
            {
                options.AddAction("boom", control => throw new InvalidOperationException("Fake predicate failure."), control => { });
                options.AddAction("boom-late", control => control.Name == "Replaceable" ? throw new InvalidOperationException("Fake predicate failure.") : false, control => { });
                options.AddAction("fine", control => control.Name == "Replaceable", control => { });
            }))
            {
                await app.TreeAsync();
                JArray tree = await app.TreeAsync();
                Assert.IsTrue(tree.Count > 20, "A throwing predicate must not fail the tree walk.");
                CollectionAssert.AreEqual(new[] { "inspect", "capture", "focus", "ensure_visible", "click", "fine" }, Capabilities(ControlNamed(tree, "Replaceable")));

                JObject[] entries = (await DiagnosticEntriesAsync(app)).Where(entry => ((string)entry["message"]!).Contains("custom action")).ToArray();
                Assert.AreEqual(2, entries.Length, "One diagnostic per failing action name, however many controls and walks.");
                Assert.AreEqual(1, entries.Count(entry => ((string)entry["message"]!).Contains("'boom'")));
                Assert.AreEqual(1, entries.Count(entry => ((string)entry["message"]!).Contains("'boom-late'")));

                JToken receipt = await app.ActAsync("invoke", ControlNamed(tree, "Replaceable"), p => p["action"] = "boom");
                AssertFailed(receipt, "unsupported_operation");
            }
        }

        [TestMethod]
        public async Task ThePredicateIsEvaluatedAgainWhenTheActionIsInvokedAndTheCallbackRunsOnTheUiThread()
        {
            int allowed = 1;
            int callbacks = 0;
            int callbackThread = -1;
            using (var app = new TestApplication(options => options.AddAction("poke",
                control => control.Name == "Replaceable" && Volatile.Read(ref allowed) == 1,
                control =>
                {
                    callbackThread = Thread.CurrentThread.ManagedThreadId;
                    Interlocked.Increment(ref callbacks);
                })))
            {
                int uiThread = -1;
                await app.OnUiAsync(() => uiThread = Thread.CurrentThread.ManagedThreadId);
                JObject node = await app.FindAsync("Replaceable");
                Assert.IsTrue(Capabilities(node).Contains("poke"));

                Volatile.Write(ref allowed, 0);
                AssertFailed(await app.ActAsync("invoke", node, p => p["action"] = "poke"), "unsupported_operation");
                Assert.AreEqual(0, Volatile.Read(ref callbacks), "The callback must not run once the predicate is false.");
                Assert.IsFalse(Capabilities(await app.FindAsync("Replaceable")).Contains("poke"));

                Volatile.Write(ref allowed, 1);
                JToken completed = await app.ActAsync("invoke", node, p => p["action"] = "poke");
                Assert.AreEqual("completed", (string?)completed["state"], completed.ToString());
                Assert.AreEqual(1, Volatile.Read(ref callbacks));
                Assert.AreEqual(uiThread, callbackThread, "The callback runs on the UI thread.");
            }
        }

        // --- a hosted cell editor must not reveal a redacted column ---------------------------------------

        private const string EditorSecret = "S3cr3t-Editor-Text";

        private static async Task<(JObject Node, JToken Inspected)> EditorAsync(TestApplication app, string name)
        {
            JObject node = ControlNamed(await app.TreeAsync(), name);
            JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(node)));
            return (node, inspected);
        }

        private static void AssertEditorHidden(JObject node, JToken inspected, string because)
        {
            foreach (JToken view in new[] { node, inspected })
            {
                Assert.AreEqual(JTokenType.Null, view["text"]!.Type, because + ": " + view);
                Assert.AreEqual(true, (bool?)view["textRedacted"], because);
                Assert.IsFalse(view.ToString().Contains(EditorSecret), because);
            }
        }

        [TestMethod]
        public async Task AHostedEditorInAGridWithARedactedColumnShowsNoTextInTheTreeOrInspectEvenWhileAModalHoldsTheEdit()
        {
            var adapter = new FakeGridAdapter { ModalFormInSetCell = () => FakeGrid.NewModal("EditDialog") };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Redacted = true);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                var before = await EditorAsync(app, "GridEditor");
                AssertEditorHidden(before.Node, before.Inspected, "even an empty editor of a redacted grid is redacted");

                JObject parameters = TargetParameters(grid);
                Edit(1, 1, EditorSecret, token)(parameters);
                JToken first = Success(await app.RequestAsync("set_cell", parameters));
                JObject close = await app.WaitForNodeAsync("EditDialogClose");
                Assert.AreEqual("started", (string?)Success(await app.RequestAsync("action_status", ReceiptParameters(first)))["state"]);
                string held = "";
                await OnGridAsync(app, "Grid", fake => held = fake.Editor.Text + "|" + fake.Editor.Visible);
                Assert.AreEqual(EditorSecret + "|True", held, "Precondition: the open editor holds the secret.");

                var during = await EditorAsync(app, "GridEditor");
                AssertEditorHidden(during.Node, during.Inspected, "while the validation modal holds the receipt started");
                Assert.IsFalse((await app.TreeAsync()).ToString().Contains(EditorSecret), "Nowhere in the tree.");

                await app.WaitForCompletionAsync(Success(await app.RequestAsync("invoke", TargetParameters(close))));
                JToken status = await app.WaitForTerminalAsync(first);
                AssertGridCompleted(status, "closing the dialog completes the edit");
                await OnGridAsync(app, "Grid", fake => held = fake.Editor.Text + "|" + fake.Editor.Visible);
                Assert.AreEqual(EditorSecret + "|False", held, "Precondition: the grid hid the editor without clearing it.");

                var after = await EditorAsync(app, "GridEditor");
                AssertEditorHidden(after.Node, after.Inspected, "after the edit, with the hidden editor still holding the text");
                Assert.IsFalse((await app.TreeAsync()).ToString().Contains(EditorSecret));
            }
        }

        [TestMethod]
        public async Task AHostedEditorInAGridWithoutARedactedColumnStillShowsItsText()
        {
            var adapter = new FakeGridAdapter { ModalFormInSetCell = () => FakeGrid.NewModal("EditDialog") };
            using (var app = StartWithGrids(adapter, "Grid", "Other"))
            {
                await OnGridAsync(app, "Other", fake => fake.Columns[1].Redacted = true);
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");
                JObject parameters = TargetParameters(grid);
                Edit(1, 1, EditorSecret, token)(parameters);
                JToken first = Success(await app.RequestAsync("set_cell", parameters));
                JObject close = await app.WaitForNodeAsync("EditDialogClose");

                var during = await EditorAsync(app, "GridEditor");
                foreach (JToken view in new[] { during.Node, during.Inspected })
                {
                    Assert.AreEqual(EditorSecret, (string?)view["text"], "The guard is per grid, not blanket: " + view);
                    Assert.AreEqual(false, (bool?)view["textRedacted"]);
                }
                var other = await EditorAsync(app, "OtherEditor");
                Assert.AreEqual(true, (bool?)other.Node["textRedacted"], "The redacted grid next to it is still guarded.");

                await app.WaitForCompletionAsync(Success(await app.RequestAsync("invoke", TargetParameters(close))));
                AssertGridCompleted(await app.WaitForTerminalAsync(first), "the edit completes");
                var after = await EditorAsync(app, "GridEditor");
                Assert.AreEqual(EditorSecret, (string?)after.Node["text"]);
            }
        }

        [TestMethod]
        public async Task AGridWhoseColumnsCannotBeReadCountsAsRedactedForItsDescendantsAndLogsOnce()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Editor.Text = EditorSecret);
                var open = await EditorAsync(app, "GridEditor");
                Assert.AreEqual(EditorSecret, (string?)open.Node["text"], "Readable and not redacted: the text shows.");

                foreach (string failing in new[] { "view", "column" })
                {
                    adapter.ThrowInGetViewInfo = failing == "view";
                    adapter.ThrowInDescribeColumn = failing == "column";
                    await app.TreeAsync();
                    var closed = await EditorAsync(app, "GridEditor");
                    AssertEditorHidden(closed.Node, closed.Inspected, "fail closed: " + failing);
                }
                string diagnostics = string.Join("\n", (await DiagnosticEntriesAsync(app)).Select(entry => entry.ToString()));
                Assert.AreEqual(1, Regex.Matches(diagnostics, "failed in (GetViewInfo|DescribeColumn)").Count,
                    "One diagnostic per grid, however many walks.");

                adapter.ThrowInGetViewInfo = false;
                adapter.ThrowInDescribeColumn = false;
                Assert.AreEqual(EditorSecret, (string?)(await EditorAsync(app, "GridEditor")).Node["text"], "A transient failure recovers.");
            }

            using (var app = StartWithGrids(new FakeGridAdapter { ThrowInAttach = true }, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake => fake.Editor.Text = EditorSecret);
                var attached = await EditorAsync(app, "GridEditor");
                AssertEditorHidden(attached.Node, attached.Inspected, "a grid whose Attach failed counts as redacted");
            }
        }

        [TestMethod]
        public async Task TheDescendantsOfAGridWithARedactedColumnHideTheirValueAndItems()
        {
            var adapter = new FakeGridAdapter();
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                await OnGridAsync(app, "Grid", fake =>
                {
                    fake.Columns[1].Redacted = true;
                    var number = new NumericUpDown { Name = "HostedNumber", Value = 42 };
                    var combo = new ComboBox { Name = "HostedCombo" };
                    combo.Items.AddRange(new object[] { "one", "two" });
                    combo.SelectedIndex = 1;
                    fake.Controls.Add(number);
                    fake.Controls.Add(combo);
                });
                JArray tree = await app.TreeAsync();
                Assert.IsNull(ControlNamed(tree, "HostedNumber")["value"], "A hosted editor's value is the cell value.");
                Assert.IsNull(ControlNamed(tree, "HostedCombo")["selectedIndex"]);
                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(ControlNamed(tree, "HostedCombo"))));
                Assert.AreEqual("redacted-grid", (string?)inspected["itemsUnavailable"]);
                Assert.IsNull(inspected["items"]);

                await OnGridAsync(app, "Grid", fake => fake.Columns[1].Redacted = false);
                tree = await app.TreeAsync();
                Assert.AreEqual(42m, (decimal?)ControlNamed(tree, "HostedNumber")["value"]);
                Assert.AreEqual(1, (int?)ControlNamed(tree, "HostedCombo")["selectedIndex"]);
            }
        }

        // --- capabilities follow the routing --------------------------------------------------------------

        private sealed class ListBoxGrid : ListBox
        {
        }

        // Accepts the controls named RoutedList and RoutedPanel, whatever their type; reports the features set on it.
        private sealed class RoutingAdapter : GridAdapter
        {
            internal GridFeatures Features { get; set; }

            internal RoutingAdapter() : base("routing")
            {
            }

            public override bool Accepts(Control control) => control.Name == "RoutedList" || control.Name == "RoutedPanel";

            public override GridFeatures GetFeatures(Control grid) => Features;

            public override GridViewInfo GetViewInfo(Control grid) => new GridViewInfo(0, 0, 0);

            public override void DescribeColumn(Control grid, int column, GridColumnDescription d)
            {
            }

            public override string? GetCellText(Control grid, int row, int column) => null;
        }

        [TestMethod]
        public async Task WhenAnAdapterHandlesAListBoxOrAnAutoScrollPanelOnlyItsFeaturesDecideSelectAndScroll()
        {
            var adapter = new RoutingAdapter();
            using (var app = new TestApplication(
                options => options.AddGridAdapter(adapter),
                window =>
                {
                    window.Controls.Add(new ListBoxGrid { Name = "RoutedList" });
                    window.Controls.Add(new Panel { Name = "RoutedPanel", AutoScroll = true });
                    window.Controls.Add(new ListBoxGrid { Name = "PlainList" });
                    window.Controls.Add(new Panel { Name = "PlainPanel", AutoScroll = true });
                }))
            {
                var cases = new[]
                {
                    (Features: GridFeatures.None, Expected: new string[0]),
                    (Features: GridFeatures.Select, Expected: new[] { "select" }),
                    (Features: GridFeatures.Scroll, Expected: new[] { "scroll" }),
                    (Features: GridFeatures.Select | GridFeatures.Scroll, Expected: new[] { "select", "scroll" })
                };
                foreach (var testCase in cases)
                {
                    adapter.Features = testCase.Features;
                    JArray tree = await app.TreeAsync();
                    foreach (string name in new[] { "RoutedList", "RoutedPanel" })
                    {
                        string[] capabilities = Capabilities(ControlNamed(tree, name));
                        Assert.AreEqual(capabilities.Length, capabilities.Distinct().Count(), "No duplicate capabilities: " + string.Join(",", capabilities));
                        CollectionAssert.AreEqual(new[] { "inspect", "capture", "focus", "ensure_visible", "read_grid" }.Concat(testCase.Expected).ToArray(),
                            capabilities, name + " with " + testCase.Features + ": the adapter decides, not the control type.");
                    }
                    CollectionAssert.IsSubsetOf(new[] { "select", "scroll" }, Capabilities(ControlNamed(tree, "PlainList")), "An unhandled list keeps its built-in handling.");
                    CollectionAssert.Contains(Capabilities(ControlNamed(tree, "PlainPanel")), "scroll");
                }
            }
        }

        // --- the leak test can detect a leak ----------------------------------------------------------------

        [TestMethod]
        public async Task AGridTheAdapterKeepsInAFieldStaysAliveSoTheLeakTestCanDetectALeak()
        {
            var adapter = new FakeGridAdapter { KeepGrids = true };
            Assert.IsFalse(await GridIsCollectedAfterItsFormClosesAsync(adapter),
                "Negative control: a grid kept by the adapter must still be alive, or the collection loop proves nothing.");
        }

        // --- honesty paths of set_grid_cell -----------------------------------------------------------------

        [TestMethod]
        public async Task AnEditWhoseTokenRefreshFailsAfterTheCommitIsStillCommittedWithoutATokenAndOneDiagnostic()
        {
            const string edit = "Bluebird-Edit";
            var adapter = new FakeGridAdapter();
            adapter.AfterCommit = fake => adapter.ThrowInGetViewInfo = true;
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, edit, token));

                AssertGridCompleted(status, "the edit is committed, so it is not reported as failed");
                Assert.AreEqual(true, (bool?)status["result"]!["committed"]);
                Assert.AreEqual(JTokenType.Null, status["result"]!["viewToken"]!.Type, "No token can be offered for a view that cannot be read.");
                JObject[] entries = (await DiagnosticEntriesAsync(app)).Where(entry => ((string)entry["message"]!).Contains("GetViewInfo")).ToArray();
                Assert.AreEqual(1, entries.Length, "One diagnostic.");
                Assert.IsFalse(entries[0].ToString().Contains(edit));

                adapter.ThrowInGetViewInfo = false;
                Assert.AreEqual(edit, CellOf(await ReadGridAsync(app, "Grid"), 1, 1), "The edit was applied.");
            }
        }

        [TestMethod]
        public async Task AnAdapterThatReturnsNoResultFromSetCellGivesAdapterFailedWithABridgeAuthoredDiagnostic()
        {
            var adapter = new FakeGridAdapter { ReturnNoResult = true };
            using (var app = StartWithGrids(adapter, "Grid"))
            {
                JObject grid = await app.FindAsync("Grid");
                string token = await TokenAsync(app, "Grid");

                JToken status = await app.ActAsync("set_cell", grid, Edit(1, 1, "Bluebird", token));

                AssertGridFailed(status, "adapter_failed", "a null result");
                StringAssert.Contains((string)status["error"]!["message"]!, "returned no result from SetCell");
                string[] messages = (await DiagnosticEntriesAsync(app)).Where(entry => (string?)entry["category"] == "adapter_failed")
                    .Select(entry => (string)entry["message"]!).ToArray();
                Assert.AreEqual(1, messages.Count(message => message.Contains("returned no result from SetCell")));
                Assert.IsFalse(messages.Any(message => message.Contains("InvalidOperationException")), "Not an adapter's own exception.");

                adapter.ReturnNoResult = false;
                AssertGridCompleted(await app.ActAsync("set_cell", grid, Edit(1, 1, "Bluebird", token)), "the grid is released");
            }
        }

        // --- diagnostics from the non-data members are bounded ---------------------------------------------

        [TestMethod]
        public async Task TheDiagnosticOfAFailingAcceptsAttachOrGetFeaturesCarriesAtMost200CharactersOfTheMessage()
        {
            foreach (string member in new[] { nameof(GridAdapter.Accepts), nameof(GridAdapter.Attach), nameof(GridAdapter.GetFeatures) })
            {
                var adapter = new FakeGridAdapter { FailureDetail = new string('x', 500) };
                adapter.ThrowInAccepts = member == nameof(GridAdapter.Accepts);
                adapter.ThrowInAttach = member == nameof(GridAdapter.Attach);
                adapter.ThrowInGetFeatures = member == nameof(GridAdapter.GetFeatures);
                using (var app = StartWithGrids(adapter, "Grid"))
                {
                    await app.TreeAsync();
                    string[] messages = (await DiagnosticEntriesAsync(app)).Select(entry => (string)entry["message"]!)
                        .Where(message => message.Contains("failed in " + member)).ToArray();
                    Assert.AreEqual(1, messages.Length, member);
                    string prefix = "Grid adapter 'fake-grid' failed in " + member + ": InvalidOperationException: ";
                    StringAssert.StartsWith(messages[0], prefix);
                    Assert.AreEqual(200, messages[0].Length - prefix.Length, member + " keeps the first 200 characters of the message and cuts the rest.");
                }
            }
        }
    }
}
