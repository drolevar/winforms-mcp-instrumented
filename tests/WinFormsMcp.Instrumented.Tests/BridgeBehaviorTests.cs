using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>
    /// Per-control validation, bounded paging, redaction, exclusion and UI-thread races.
    /// Shares the fixture of <see cref="BridgeIntegrationTests"/>; requires Windows and .NET Framework 4.8.
    /// </summary>
    public sealed partial class BridgeIntegrationTests
    {
        // --- action validation ------------------------------------------------------------------

        [TestMethod]
        public async Task ActionsThatCannotApplyFailTheirReceiptWithASpecificCode()
        {
            // Each row is a request the bridge must refuse. The receipt is accepted ("queued"); the
            // refusal is observed when the action starts, because validation re-runs on the UI thread.
            var cases = new (string Method, string Control, Action<JObject>? Parameters, string Code)[]
            {
                ("invoke", "DisabledButton", null, "disabled_control"),
                ("invoke", "HiddenButton", null, "hidden_control"),
                ("invoke", "PlainLabel", null, "unsupported_operation"),
                ("invoke", "Replaceable", p => p["action"] = "not-registered", "unsupported_operation"),
                ("invoke", "Replaceable", p => p["action"] = 42, "invalid_argument"),
                ("set_text", "ReadOnlyText", p => p["text"] = "x", "read_only"),
                ("set_text", "Replaceable", p => p["text"] = "x", "unsupported_operation"),
                ("set_text", "FreeText", null, "invalid_argument"),
                ("set_value", "Number", p => p["value"] = 11, "invalid_argument"),
                ("set_value", "Number", p => p["value"] = "not a number", "invalid_argument"),
                ("set_value", "Replaceable", p => p["value"] = 1, "unsupported_operation"),
                ("set_checked", "Replaceable", p => p["checked"] = true, "unsupported_operation"),
                ("set_checked", "Number", p => p["checked"] = "yes", "invalid_argument"),
                ("select", "BigList", p => p["index"] = 250, "invalid_argument"),
                ("select", "BigList", p => p["index"] = -2, "invalid_argument"),
                ("select", "Replaceable", p => p["index"] = 0, "unsupported_operation"),
                ("scroll", "Replaceable", p => p["y"] = 0, "unsupported_operation"),
            };

            using (var app = new TestApplication())
            {
                var mismatches = new List<string>();
                foreach (var testCase in cases)
                {
                    JObject node = await app.FindAsync(testCase.Control);
                    JToken status = await app.ActAsync(testCase.Method, node, testCase.Parameters);
                    string? state = (string?)status["state"];
                    string? code = (string?)status["error"]?["code"];
                    if (state != "failed" || code != testCase.Code)
                        mismatches.Add(testCase.Method + " on " + testCase.Control + ": expected failed/" +
                                       testCase.Code + ", got " + state + "/" + code);
                }
                Assert.AreEqual(0, mismatches.Count, string.Join(Environment.NewLine, mismatches));
            }
        }

        [TestMethod]
        public async Task ValidActionsChangeTheControlAndReportHandlerReturn()
        {
            using (var app = new TestApplication())
            {
                JToken set = await app.ActAsync("set_value", await app.FindAsync("Number"), p => p["value"] = 3);
                Assert.AreEqual("completed", (string?)set["state"], set.ToString());
                Assert.AreEqual(true, (bool?)set["result"]!["handlerReturned"]);
                Assert.AreEqual(3m, (decimal)(await app.FindAsync("Number"))["value"]!);

                JToken text = await app.ActAsync("set_text", await app.FindAsync("FreeText"), p => p["text"] = "typed");
                Assert.AreEqual("completed", (string?)text["state"]);
                Assert.AreEqual("typed", (string?)(await app.FindAsync("FreeText"))["text"]);
            }
        }

        // --- bounded observation ----------------------------------------------------------------

        [TestMethod]
        public async Task ListItemsArePagedWithinTheDocumentedBounds()
        {
            using (var app = new TestApplication())
            {
                JObject list = await app.FindAsync("BigList");

                JToken first = Success(await app.RequestAsync("inspect", Paging(list, 0, 100)));
                Assert.AreEqual(250, (int)first["totalItems"]!);
                Assert.AreEqual(100, ((JArray)first["items"]!).Count);
                Assert.AreEqual(true, (bool)first["itemsTruncated"]!, "More items remain after the first page.");
                Assert.AreEqual("current-view-position", (string?)first["itemIdentity"]);

                JToken second = Success(await app.RequestAsync("inspect", Paging(list, 100, 100)));
                Assert.AreEqual(100, (int)second["items"]![0]!["index"]!);

                JToken last = Success(await app.RequestAsync("inspect", Paging(list, 200, 100)));
                Assert.AreEqual(50, ((JArray)last["items"]!).Count);
                Assert.AreEqual(false, (bool)last["itemsTruncated"]!);

                Assert.AreEqual(0, ((JArray)Success(await app.RequestAsync("inspect", Paging(list, 250, 100)))["items"]!).Count,
                    "startIndex equal to the count is an empty final page.");
                Assert.AreEqual("invalid_argument", (await app.RequestAsync("inspect", Paging(list, 251, 100))).Error?.Code);
                Assert.AreEqual("invalid_argument", (await app.RequestAsync("inspect", Paging(list, 0, 101))).Error?.Code);
                Assert.AreEqual("invalid_argument", (await app.RequestAsync("inspect", Paging(list, 0, 0))).Error?.Code);
            }
        }

        [TestMethod]
        public async Task GridReadsAreBoundedAndReportWhetherMoreRowsRemain()
        {
            using (var app = new TestApplication())
            {
                JObject grid = await app.FindAsync("BigGrid");

                JToken page = Success(await app.RequestAsync("read_grid", GridPage(grid, 0, 200, 0, 2)));
                Assert.AreEqual(200, ((JArray)page["rows"]!).Count);
                Assert.AreEqual(true, (bool)page["hasMoreRows"]!);
                Assert.AreEqual("formatted-display-values", (string?)page["dataKind"]);

                JToken tail = Success(await app.RequestAsync("read_grid", GridPage(grid, 240, 25, 0, 2)));
                Assert.AreEqual(10, ((JArray)tail["rows"]!).Count);
                Assert.AreEqual(false, (bool)tail["hasMoreRows"]!);
                Assert.AreEqual("a249", (string?)tail["rows"]![9]!["cells"]![0]);

                Assert.AreEqual("invalid_argument", (await app.RequestAsync("read_grid", GridPage(grid, 0, 201, 0, 2))).Error?.Code);
                Assert.AreEqual("invalid_argument", (await app.RequestAsync("read_grid", GridPage(grid, 0, 10, 0, 51))).Error?.Code);
                Assert.AreEqual("unsupported_operation",
                    (await app.RequestAsync("read_grid", TargetParameters(await app.FindAsync("BigList")))).Error?.Code);
            }
        }

        [TestMethod]
        public async Task LongTextIsTruncatedAndFlagged()
        {
            using (var app = new TestApplication())
            {
                await app.OnUiAsync(() => ((TextBox)app.Window.Controls["FreeText"]!).Text = new string('x', 5_000));
                JObject node = await app.FindAsync("FreeText");
                Assert.AreEqual(4_096, ((string?)node["text"])!.Length);
                Assert.AreEqual(true, (bool)node["textTruncated"]!);
            }
        }

        [TestMethod]
        public async Task PasswordTextIsNeverReturned()
        {
            using (var app = new TestApplication())
            {
                JObject inTree = await app.FindAsync("Password");
                Assert.AreEqual(JTokenType.Null, inTree["text"]!.Type);
                Assert.AreEqual(true, (bool)inTree["textRedacted"]!);

                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(inTree)));
                Assert.AreEqual(JTokenType.Null, inspected["text"]!.Type);
                Assert.IsFalse(inspected.ToString().Contains("secret"), "The password value leaked into an inspect result.");
            }
        }

        // --- scope: exclusion and detachment ----------------------------------------------------

        [TestMethod]
        public async Task ExcludedSubtreesAreAbsentAndAControlMovedIntoOneBecomesStale()
        {
            using (var app = new TestApplication())
            {
                JArray tree = await app.TreeAsync();
                Assert.IsFalse(tree.Any(n => (string?)n["name"] == "Excluded" || (string?)n["name"] == "InsideExcluded"),
                    "An excluded control or one of its descendants appeared in the tree.");

                JObject label = await app.FindAsync("PlainLabel");
                await app.OnUiAsync(() => app.Window.Controls["PlainLabel"]!.Name = "Excluded");
                Assert.AreEqual("stale_target", (await app.RequestAsync("inspect", TargetParameters(label))).Error?.Code);
                Assert.IsFalse((await app.TreeAsync()).Any(n => (string?)n["id"] == (string?)label["id"]));
            }
        }

        [TestMethod]
        public async Task ADetachedControlIsUntargetableAndRegainsItsIdentityWhenReattached()
        {
            using (var app = new TestApplication())
            {
                JObject before = await app.FindAsync("Replaceable");
                Control? held = null;
                await app.OnUiAsync(() =>
                {
                    held = app.Window.Controls["Replaceable"];
                    app.Window.Controls.Remove(held);
                });
                Assert.AreEqual("stale_target", (await app.RequestAsync("inspect", TargetParameters(before))).Error?.Code,
                    "A removed-but-alive control is outside the instrumented scope.");

                await app.OnUiAsync(() => app.Window.Controls.Add(held!));
                JObject after = await app.FindAsync("Replaceable");
                Assert.AreEqual((string?)before["id"], (string?)after["id"], "The same object keeps its identity.");
                Success(await app.RequestAsync("inspect", TargetParameters(before)));
            }
        }

        // --- races on the UI thread -------------------------------------------------------------

        [TestMethod]
        public async Task ABusyUiThreadYieldsUiTimeoutAndTheBridgeRecovers()
        {
            using (var app = new TestApplication())
            using (var gate = new ManualResetEventSlim())
            {
                Task block = app.BlockUiAsync(gate);
                RpcResponse timedOut = await app.RequestAsync("tree");
                Assert.AreEqual("ui_timeout", timedOut.Error?.Code, "A read must not wait forever or pretend the UI is unchanged.");
                Assert.IsNull(timedOut.Result);

                gate.Set();
                await block;
                // The abandoned read must not run late and must not poison later requests.
                Assert.IsTrue((await app.TreeAsync()).Any(n => (string?)n["name"] == "Replaceable"));
            }
        }

        [TestMethod]
        public async Task ADisposedTargetFailsAQueuedActionWithoutRunningItsHandler()
        {
            using (var app = new TestApplication())
            using (var gate = new ManualResetEventSlim())
            {
                JObject doomed = await app.FindAsync("Doomed");
                // The UI thread is held, then disposes the target the moment it is released:
                // the invoke below is accepted first and would run second.
                Task block = app.BlockUiAsync(gate, () =>
                {
                    app.Window.Controls.Remove(app.DoomedButton);
                    app.DoomedButton.Dispose();
                });

                JToken receipt = Success(await app.RequestAsync("invoke", TargetParameters(doomed)));
                Assert.AreEqual("queued", (string?)receipt["state"]);
                JToken pending = Success(await app.RequestAsync("action_status", ReceiptParameters(receipt)));
                Assert.AreEqual("queued", (string?)pending["state"], "The action cannot have started while the UI thread is held.");

                gate.Set();
                await block;
                JToken status = await app.WaitForTerminalAsync(receipt);
                Assert.AreEqual("failed", (string?)status["state"]);
                Assert.AreEqual("stale_target", (string?)status["error"]?["code"],
                    "Validation must be repeated when the action starts, not only when it is accepted.");
                Assert.AreEqual(0, app.DoomedClicks, "The click handler of a disposed control must not run.");
            }
        }

        [TestMethod]
        public async Task TooManyPendingActionsAreRefusedAndTheJournalRecoversAfterwards()
        {
            using (var app = new TestApplication())
            using (var gate = new ManualResetEventSlim())
            {
                JObject button = await app.FindAsync("Replaceable");
                Task block = app.BlockUiAsync(gate);

                var receipts = new List<JToken>();
                for (int i = 0; i < 32; i++)
                    receipts.Add(Success(await app.RequestAsync("invoke", TargetParameters(button))));
                RpcResponse refused = await app.RequestAsync("invoke", TargetParameters(button));
                Assert.AreEqual("action_limit", refused.Error?.Code, "The 33rd pending action must be refused, not silently queued.");

                gate.Set();
                await block;
                Assert.AreEqual("completed", (string?)(await app.WaitForTerminalAsync(receipts[0]))["state"]);
                Assert.AreEqual("completed", (string?)(await app.WaitForTerminalAsync(receipts[31]))["state"]);
                Assert.AreEqual("completed", (string?)(await app.ActAsync("invoke", button))["state"],
                    "A new action must be accepted once the backlog has drained.");
            }
        }

        // --- element adapter registration -------------------------------------------------------

        [TestMethod]
        public async Task AdapterNamesAreValidatedAndAppearInHello()
        {
            foreach (string bad in new[] { "", "Upper", "has space", new string('a', 49), "fake\n", new string('a', 48) + "\n" })
                Assert.ThrowsExactly<ArgumentException>(() => new NamedAdapter(bad), "Name '" + bad + "' must be rejected.");
            Assert.ThrowsExactly<ArgumentNullException>(() => new McpBridgeBuilder().AddElementAdapter(null!));

            var builder = new McpBridgeBuilder().AddElementAdapter(new NamedAdapter("same"));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddElementAdapter(new NamedAdapter("same")));

            using (var app = StartWithFake(new FakeElementAdapter()))
            {
                JToken hello = Success(await app.RequestAsync("hello"));
                CollectionAssert.AreEqual(new[] { "fake", "toolstrip" }, ((JArray)hello["elementAdapters"]!).Select(n => (string?)n).ToArray());
            }
        }

        [TestMethod]
        public async Task HelloAdvertisesTheAdaptersTheRuntimeFroze()
        {
            McpBridgeBuilder? captured = null;
            var app = new TestApplication(
                options =>
                {
                    captured = options;
                    options.AddElementAdapter(new FakeElementAdapter());
                },
                window => window.Controls.Add(new Control { Name = FakeElementAdapter.HostName }));
            using (app)
            {
                // The runtime copied the list when the bridge started; a later registration changes nothing.
                captured!.AddElementAdapter(new NamedAdapter("late"));
                JToken hello = Success(await app.RequestAsync("hello"));
                CollectionAssert.AreEqual(new[] { "fake", "toolstrip" }, ((JArray)hello["elementAdapters"]!).Select(n => (string?)n).ToArray());
            }
        }

        [TestMethod]
        public void AddActionRejectsOpenAndClose()
        {
            var builder = new McpBridgeBuilder();
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction<Button>("open", _ => { }));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction<Button>("close", _ => { }));
            builder.AddAction<Button>("custom", _ => { });
            ArgumentNullException nullCallback = Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddAction<Button>("no-callback", null!));
            Assert.AreEqual("callback", nullCallback.ParamName);
        }

        // --- grid adapter registration and the AddAction predicate overload ---------------------------

        [TestMethod]
        public async Task GridAdapterNamesAreValidatedAndAppearInHello()
        {
            foreach (string bad in new[] { "", "Upper", "has space", new string('a', 49), "fake\n", new string('a', 48) + "\n" })
                Assert.ThrowsExactly<ArgumentException>(() => new NamedGridAdapter(bad), "Name '" + bad + "' must be rejected.");
            Assert.ThrowsExactly<ArgumentNullException>(() => new McpBridgeBuilder().AddGridAdapter(null!));

            var builder = new McpBridgeBuilder().AddGridAdapter(new NamedGridAdapter("same"));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddGridAdapter(new NamedGridAdapter("same")));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddGridAdapter(new NamedGridAdapter("datagridview")),
                "The built-in name is reserved.");

            using (var app = new TestApplication(options => options.AddGridAdapter(new NamedGridAdapter("fake-grid")), _ => { }))
            {
                JToken hello = Success(await app.RequestAsync("hello"));
                CollectionAssert.AreEqual(new[] { "fake-grid", "datagridview" }, ((JArray)hello["gridAdapters"]!).Select(n => (string?)n).ToArray());
            }

            // Registration order is kept (not sorted), and the built-in handling is always last.
            using (var app = new TestApplication(options => options
                .AddGridAdapter(new NamedGridAdapter("b")).AddGridAdapter(new NamedGridAdapter("a")), _ => { }))
            {
                JToken hello = Success(await app.RequestAsync("hello"));
                CollectionAssert.AreEqual(new[] { "b", "a", "datagridview" }, ((JArray)hello["gridAdapters"]!).Select(n => (string?)n).ToArray());
            }

            using (var app = new TestApplication())
            {
                JToken hello = Success(await app.RequestAsync("hello"));
                CollectionAssert.AreEqual(new[] { "datagridview" }, ((JArray)hello["gridAdapters"]!).Select(n => (string?)n).ToArray());
            }
        }

        [TestMethod]
        public void AddActionWithAPredicateValidatesNames()
        {
            var builder = new McpBridgeBuilder();
            Func<Control, bool> accepts = _ => true;
            Action<Control> callback = _ => { };
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction("", accepts, callback));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction("  ", accepts, callback));
            foreach (string reserved in new[] { "click", "focus", "open", "close" })
                Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction(reserved, accepts, callback), reserved);
            Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddAction("custom", null!, callback));
            Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddAction("custom", accepts, null!));

            builder.AddAction("custom", accepts, callback);
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction("custom", accepts, callback));
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction<Button>("custom", _ => { }),
                "Names are unique across both overloads.");
            builder.AddAction<Button>("other", _ => { });
            Assert.ThrowsExactly<ArgumentException>(() => builder.AddAction("other", accepts, callback));
        }

        [TestMethod]
        public void GridColumnDescriptionLimitsBandsAndChoices()
        {
            // Bridge-owned types have internal constructors and this assembly has no InternalsVisibleTo.
            var description = (WinFormsMcp.Instrumented.Grids.GridColumnDescription)Activator.CreateInstance(
                typeof(WinFormsMcp.Instrumented.Grids.GridColumnDescription), nonPublic: true)!;
            System.Reflection.PropertyInfo truncated = typeof(WinFormsMcp.Instrumented.Grids.GridColumnDescription)
                .GetProperty("ChoicesTruncated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.IsNotNull(truncated, "GridColumnDescription.ChoicesTruncated was not found; this test reads it by reflection.");

            Assert.IsTrue(description.Visible, "A column is visible unless the adapter says otherwise.");
            Assert.IsFalse(description.Editable);
            Assert.IsFalse(description.Redacted);
            Assert.AreEqual(WinFormsMcp.Instrumented.Grids.GridValueKind.Text, description.ValueKind);

            for (int i = 0; i < 8; i++)
                Assert.IsTrue(description.AddBand(i == 3 ? null : "Band " + i), "Band " + (i + 1) + " must be accepted.");
            Assert.IsFalse(description.AddBand("Ninth"), "The 9th band must be refused.");

            for (int i = 0; i < 50; i++)
                Assert.IsTrue(description.AddChoice("Choice " + i), "Choice " + (i + 1) + " must be accepted.");
            Assert.IsFalse((bool)truncated.GetValue(description)!, "Exactly 50 choices are not truncated.");
            Assert.IsFalse(description.AddChoice("Fifty-first"), "The 51st choice must be refused.");
            Assert.IsTrue((bool)truncated.GetValue(description)!, "Refusing a choice must flag the list as truncated.");
        }

        [TestMethod]
        public void AGridViewSignalRefusesNoStateAndNeverThrowsFromChanged()
        {
            // The signal runs inside application event handlers; its constructor is internal, so read it by reflection.
            Type? stateType = typeof(McpBridge).Assembly.GetType("WinFormsMcp.Instrumented.Grids.GridState");
            Assert.IsNotNull(stateType, "GridState was not found; this test reads it by reflection.");
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;

            var refused = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => Activator.CreateInstance(
                typeof(WinFormsMcp.Instrumented.Grids.GridViewSignal), Any, null, new object?[] { null }, null));
            Assert.IsInstanceOfType<ArgumentNullException>(refused.InnerException);

            object state = Activator.CreateInstance(stateType!, Any, null, null, null)!;
            var signal = (WinFormsMcp.Instrumented.Grids.GridViewSignal)Activator.CreateInstance(
                typeof(WinFormsMcp.Instrumented.Grids.GridViewSignal), Any, null, new[] { state }, null)!;
            signal.Changed();
            signal.Changed();
        }

        private sealed class NamedGridAdapter : WinFormsMcp.Instrumented.Grids.GridAdapter
        {
            internal NamedGridAdapter(string name) : base(name) { }

            public override bool Accepts(Control control) => false;

            public override WinFormsMcp.Instrumented.Grids.GridFeatures GetFeatures(Control grid)
                => WinFormsMcp.Instrumented.Grids.GridFeatures.None;

            public override WinFormsMcp.Instrumented.Grids.GridViewInfo GetViewInfo(Control grid)
                => new WinFormsMcp.Instrumented.Grids.GridViewInfo(0, 0, 0);

            public override void DescribeColumn(Control grid, int column, WinFormsMcp.Instrumented.Grids.GridColumnDescription d) { }

            public override string? GetCellText(Control grid, int row, int column) => null;
        }

        private sealed class NamedAdapter : WinFormsMcp.Instrumented.Elements.ElementAdapter
        {
            internal NamedAdapter(string name) : base(name) { }

            public override void GetChildren(object parent, WinFormsMcp.Instrumented.Elements.ElementSink sink) { }

            public override bool IsAlive(object element) => false;

            public override void Describe(object element, WinFormsMcp.Instrumented.Elements.ElementDescription description) { }
        }

        private static JObject Paging(JToken node, int startIndex, int maxItems)
        {
            JObject parameters = TargetParameters(node);
            parameters["startIndex"] = startIndex;
            parameters["maxItems"] = maxItems;
            return parameters;
        }

        private static JObject GridPage(JToken node, int startRow, int rowCount, int startColumn, int columnCount)
        {
            JObject parameters = TargetParameters(node);
            parameters["startRow"] = startRow;
            parameters["rowCount"] = rowCount;
            parameters["startColumn"] = startColumn;
            parameters["columnCount"] = columnCount;
            return parameters;
        }
    }
}
