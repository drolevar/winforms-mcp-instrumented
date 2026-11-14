using System;
using System.Collections.Generic;
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
    /// Element nodes in the tree, driven by a fake third-party adapter that uses only the public seam.
    /// Shares the fixture of <see cref="BridgeIntegrationTests"/>; requires Windows and .NET Framework 4.8.
    /// </summary>
    public sealed partial class BridgeIntegrationTests
    {
        private static TestApplication StartWithFake(FakeElementAdapter adapter)
            => new TestApplication(
                options => options.AddElementAdapter(adapter),
                window => window.Controls.Add(new Control { Name = FakeElementAdapter.HostName }));

        private static FakePlacement Place(string name, string? text = null, bool? opened = null, params FakePlacement[] children)
        {
            var command = new FakeCommand { CommandName = name, Text = text, Opened = opened };
            command.Children.AddRange(children);
            return new FakePlacement { Command = command };
        }

        private static JObject[] Items(JArray nodes)
            => nodes.OfType<JObject>().Where(node => (string?)node["kind"] == "item").ToArray();

        private static JObject ItemNamed(JArray nodes, string name)
            => Items(nodes).Single(node => (string?)node["name"] == name);

        [TestMethod]
        public async Task ElementsAppearAsKindItemNodesWithTheirParentChain()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement leaf = Place("Leaf");
            FakePlacement first = Place("First", null, true, leaf);
            FakePlacement second = Place("Second");
            adapter.HostPlacements.Add(first);
            adapter.HostPlacements.Add(second);

            using (var app = StartWithFake(adapter))
            {
                JArray nodes = await app.TreeAsync();
                JObject host = nodes.OfType<JObject>().Single(node => (string?)node["name"] == FakeElementAdapter.HostName);
                Assert.AreEqual("control", (string?)host["kind"]);
                Assert.IsTrue(nodes.OfType<JObject>().All(node => (string?)node["kind"] == "control" || (string?)node["kind"] == "item"));
                Assert.AreEqual(0, nodes.OfType<JObject>().Count(node => node["kind"] == null));

                JObject[] items = Items(nodes);
                Assert.AreEqual(3, items.Length);
                Assert.IsTrue(items.All(item => (string?)item["adapter"] == "fake"));
                Assert.AreEqual((string?)host["id"], (string?)ItemNamed(nodes, "First")["parentId"]);
                Assert.AreEqual((string?)host["id"], (string?)ItemNamed(nodes, "Second")["parentId"]);

                var byId = nodes.OfType<JObject>().ToDictionary(node => (string)node["id"]!);
                foreach (JObject item in items)
                {
                    string? cursor = (string?)item["parentId"];
                    int steps = 0;
                    while (cursor != null && cursor != (string?)host["id"] && steps++ < 10)
                        cursor = (string?)byId[cursor]["parentId"];
                    Assert.AreEqual((string?)host["id"], cursor, "The parent chain of " + item["name"] + " must reach the host.");
                }
                Assert.AreEqual((string?)ItemNamed(nodes, "First")["id"], (string?)ItemNamed(nodes, "Leaf")["parentId"]);
                Assert.AreEqual("fake_item", (string?)ItemNamed(nodes, "First")["role"]);
                Assert.AreEqual(true, (bool?)ItemNamed(nodes, "First")["opened"]);
            }
        }

        [TestMethod]
        public async Task AClosedDropdownCollapsesInWindowTrees()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Closed", null, false, Place("HiddenOne"), Place("HiddenTwo")));
            adapter.HostPlacements.Add(Place("Unknown", null, null, Place("ShownBecauseOpenedIsUnknown")));
            adapter.HostPlacements.Add(Place("Open", null, true, Place("ShownBecauseOpen")));

            using (var app = StartWithFake(adapter))
            {
                JArray nodes = await app.TreeAsync();
                JObject closed = ItemNamed(nodes, "Closed");
                Assert.AreEqual(true, (bool?)closed["childrenOmitted"]);
                Assert.AreEqual(2, (int)closed["childCount"]!);
                Assert.IsFalse(Items(nodes).Any(item => ((string?)item["name"])!.StartsWith("Hidden", StringComparison.Ordinal)));
                Assert.AreEqual(false, (bool?)ItemNamed(nodes, "Unknown")["childrenOmitted"]);
                Assert.IsNotNull(ItemNamed(nodes, "ShownBecauseOpenedIsUnknown"));
                Assert.IsNotNull(ItemNamed(nodes, "ShownBecauseOpen"));
            }
        }

        [TestMethod]
        public async Task ASharedChildCollectionIsEmittedOnceAndTheOtherPlacementPointsToIt()
        {
            var adapter = new FakeElementAdapter();
            var shared = new FakeCommand { CommandName = "Shared" };
            shared.Children.Add(Place("SharedChild"));
            var firstPlacement = new FakePlacement { Command = shared };
            var secondPlacement = new FakePlacement { Command = shared };
            adapter.HostPlacements.Add(firstPlacement);
            adapter.HostPlacements.Add(secondPlacement);

            using (var app = StartWithFake(adapter))
            {
                JArray nodes = await app.TreeAsync();
                JObject[] placements = Items(nodes).Where(item => (string?)item["name"] == "Shared").ToArray();
                Assert.AreEqual(2, placements.Length);
                Assert.AreEqual(1, Items(nodes).Count(item => (string?)item["name"] == "SharedChild"),
                    "The shared child must be emitted once.");
                Assert.AreEqual((string?)placements[0]["id"], (string?)ItemNamed(nodes, "SharedChild")["parentId"]);
                Assert.IsNull((string?)placements[0]["childrenOf"]);
                Assert.AreEqual((string?)placements[0]["id"], (string?)placements[1]["childrenOf"]);
            }
        }

        [TestMethod]
        public async Task AnAdapterThatIgnoresTheBudgetCannotExceedMaxNodes()
        {
            var adapter = new FakeElementAdapter { AddCountToIgnoreBudget = 5000 };
            using (var app = StartWithFake(adapter))
            {
                JToken result = Success(await app.RequestAsync("tree"));
                Assert.IsTrue((int)result["nodeCount"]! <= 2000, "nodeCount was " + result["nodeCount"]);
                Assert.AreEqual(true, (bool)result["truncated"]!);
                // The per-parent cap and the sink budget, not only MaxNodes, hold the line.
                JArray nodes = (JArray)result["nodes"]!;
                string hostId = (string)nodes.OfType<JObject>().Single(node => (string?)node["name"] == FakeElementAdapter.HostName)["id"]!;
                Assert.AreEqual(1_000, nodes.OfType<JObject>().Count(node => (string?)node["parentId"] == hostId),
                    "An adapter that ignores Add() == false is cut at 1,000 children per parent.");
            }
        }

        [TestMethod]
        public async Task ASeekingSinkAcceptsNothingMoreOnceItFoundItsElement()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement item = Place("Item");
            adapter.HostPlacements.Add(Place("Other"));
            adapter.HostPlacements.Add(item);

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                Assert.AreEqual(0, adapter.RemainingWhenAddRefused.Count, "A collecting sink with room refuses nothing.");
                Success(await app.RequestAsync("inspect", TargetParameters(node)));
                Assert.IsTrue(adapter.RemainingWhenAddRefused.Count > 0, "The stale check seeks the element through a sink.");
                Assert.IsTrue(adapter.RemainingWhenAddRefused.All(remaining => remaining == 0),
                    "A sink that found its element reports Remaining == 0: " + string.Join(",", adapter.RemainingWhenAddRefused));
            }
        }

        [TestMethod]
        public async Task AThrowingIsAliveFailsClosedInTheStaleCheckButDoesNotPrune()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Item"));

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                Success(await app.RequestAsync("inspect", TargetParameters(node)));

                adapter.ThrowInIsAlive = element => true;
                await AssertStaleAsync(app, "inspect", node, "An element whose IsAlive throws is reported stale.");
                // A prune pass (every windows request) must stay lenient: the entry survives the failing adapter.
                Success(await app.RequestAsync("windows"));

                adapter.ThrowInIsAlive = null;
                Assert.AreEqual("Item", (string?)Success(await app.RequestAsync("inspect", TargetParameters(node)))["name"],
                    "The entry was not pruned while IsAlive threw.");
            }
        }

        [TestMethod]
        public async Task AControlThatGetsAParentStopsBeingAnElement()
        {
            var adapter = new FakeElementAdapter();
            int clicks = 0;
            adapter.OnExecute = (element, action) =>
            {
                Interlocked.Increment(ref clicks);
                return ElementActionOutcome.HandlerReturned;
            };
            TextBox? floating = null;
            var app = new TestApplication(
                options => options.AddElementAdapter(adapter),
                window =>
                {
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                    // Unparented when the adapter first reports it, so it is an element for now.
                    floating = new TextBox { Name = "Floating" };
                    adapter.RawElements.Add(floating);
                });
            using (app)
            {
                JObject asItem = ItemNamed(await app.TreeAsync(), "Floating");

                await app.OnUiAsync(() => app.Window.Controls.Add(floating!));
                JArray after = await app.TreeAsync();
                JObject asControl = ControlNamed(after, "Floating");
                Assert.AreEqual((string?)asItem["id"], (string?)asControl["id"], "The identity is kept.");
                Assert.AreEqual(0, Items(after).Count(item => (string?)item["name"] == "Floating"), "It is no longer listed as an item.");

                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(asControl)));
                Assert.AreEqual("control", (string?)inspected["kind"]);

                // A selector reaches it as a control: set_text must not run the adapter's click.
                var parameters = new JObject { ["selector"] = new JObject { ["name"] = "Floating" }, ["text"] = "typed" };
                JToken receipt = await app.WaitForTerminalAsync(Success(await app.RequestAsync("set_text", parameters)));
                ActionResult(receipt);
                Assert.AreEqual(0, Volatile.Read(ref clicks), "The adapter's click must not run for a control.");
                Assert.AreEqual("typed", floating!.Text);
            }
        }

        [TestMethod]
        public async Task AnAdapterExceptionIsolatesThatSubtree()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement failsChildren = Place("FailsChildren", null, true, Place("NeverReached"));
            FakePlacement failsDescribe = Place("FailsDescribe");
            adapter.HostPlacements.Add(failsChildren);
            adapter.HostPlacements.Add(failsDescribe);
            adapter.HostPlacements.Add(Place("Healthy"));
            adapter.ThrowInGetChildren = parent => ReferenceEquals(parent, failsChildren);
            adapter.ThrowInDescribe = element => ReferenceEquals(element, failsDescribe);

            using (var app = StartWithFake(adapter))
            {
                JArray nodes = await app.TreeAsync();
                Assert.IsTrue(nodes.OfType<JObject>().Any(node => (string?)node["name"] == "Replaceable"),
                    "Unrelated controls must still be returned.");
                Assert.IsNotNull(ItemNamed(nodes, "Healthy"));
                Assert.IsNull((string?)ItemNamed(nodes, "Healthy")["childrenError"]);

                JObject children = ItemNamed(nodes, "FailsChildren");
                Assert.AreEqual("adapter_failed", (string?)children["childrenError"]);
                Assert.IsFalse(Items(nodes).Any(item => (string?)item["name"] == "NeverReached"));

                JObject[] described = Items(nodes).Where(item => (string?)item["role"] == "unknown").ToArray();
                Assert.AreEqual(1, described.Length, "Exactly the element whose Describe threw has the minimal node.");
                Assert.AreEqual("adapter_failed", (string?)described[0]["childrenError"]);
                Assert.AreEqual(false, (bool?)described[0]["enabled"]);
                Assert.AreEqual(0, ((JArray)described[0]["capabilities"]!).Count);

                JToken diagnostics = Success(await app.RequestAsync("diagnostics"));
                Assert.IsTrue(diagnostics["entries"]!.ToString().Contains("fake"),
                    "A diagnostic that names the adapter must be recorded.");

                // A failing GetChildren on a control parent marks that control node.
                adapter.ThrowInGetChildren = parent => parent is Control;
                JArray controlFailure = await app.TreeAsync();
                JObject host = controlFailure.OfType<JObject>().Single(node => (string?)node["name"] == FakeElementAdapter.HostName);
                Assert.AreEqual("adapter_failed", (string?)host["childrenError"]);
                Assert.AreEqual(0, Items(controlFailure).Length);
            }
        }

        [TestMethod]
        public async Task InvalidRoleOrCapabilityStringsAreRejectedAsAdapterFailures()
        {
            var adapter = new FakeElementAdapter { Role = "Bad Role" };
            adapter.HostPlacements.Add(Place("Item"));

            using (var app = StartWithFake(adapter))
            {
                JObject badRole = Items(await app.TreeAsync()).Single();
                Assert.AreEqual("unknown", (string?)badRole["role"]);
                Assert.AreEqual("adapter_failed", (string?)badRole["childrenError"]);
                Assert.AreEqual(0, ((JArray)badRole["capabilities"]!).Count);

                adapter.Role = "fake_item";
                adapter.Capability = "CLICK!";
                JObject badCapability = Items(await app.TreeAsync()).Single();
                Assert.AreEqual("unknown", (string?)badCapability["role"]);
                Assert.AreEqual("adapter_failed", (string?)badCapability["childrenError"]);
                Assert.AreEqual(0, ((JArray)badCapability["capabilities"]!).Count);

                foreach (string badRoleText in new[] { "level2", "fake_item\n" })
                {
                    adapter.Capability = "click";
                    adapter.Role = badRoleText;
                    JObject rejected = Items(await app.TreeAsync()).Single();
                    Assert.AreEqual("unknown", (string?)rejected["role"], "Role '" + badRoleText + "' must be rejected.");
                    Assert.AreEqual("adapter_failed", (string?)rejected["childrenError"]);
                }

                adapter.Role = "fake_item";
                adapter.Capability = "click\n";
                JObject newlineCapability = Items(await app.TreeAsync()).Single();
                Assert.AreEqual("adapter_failed", (string?)newlineCapability["childrenError"]);
                Assert.AreEqual(0, ((JArray)newlineCapability["capabilities"]!).Count);

                adapter.Capability = "click";
                JObject valid = Items(await app.TreeAsync()).Single();
                Assert.AreEqual("fake_item", (string?)valid["role"]);
                Assert.IsNull((string?)valid["childrenError"]);
                CollectionAssert.AreEqual(new[] { "click" }, ((JArray)valid["capabilities"]!).Select(c => (string?)c).ToArray());
            }
        }

        [TestMethod]
        public async Task ElementTextIsTruncatedToMaxTextLength()
        {
            var adapter = new FakeElementAdapter { TextOverride = new string('x', 10_000) };
            adapter.HostPlacements.Add(Place("Long"));

            using (var app = StartWithFake(adapter))
            {
                JObject item = Items(await app.TreeAsync()).Single();
                Assert.AreEqual(true, (bool?)item["textTruncated"]);
                Assert.IsTrue(((string?)item["text"])!.Length <= 4096);
            }
        }

        [TestMethod]
        public async Task ManyElementsDoNotBreakControlSelectors()
        {
            var adapter = new FakeElementAdapter();
            for (int index = 0; index < 2_500; index++) adapter.HostPlacements.Add(Place("Item" + index));
            JObject selector = new JObject { ["selector"] = new JObject { ["name"] = "Replaceable" } };

            using (var app = StartWithFake(adapter))
            {
                JToken tree = Success(await app.RequestAsync("tree", (JObject)selector.DeepClone()));
                Assert.AreEqual("Replaceable", (string?)tree["nodes"]![0]!["name"]);
                JToken inspected = Success(await app.RequestAsync("inspect", (JObject)selector.DeepClone()));
                Assert.AreEqual("Replaceable", (string?)inspected["name"]);
                Assert.AreEqual("control", (string?)inspected["kind"]);
            }
        }

        [TestMethod]
        public async Task AnAdapterCannotEmitAControlAsAnElement()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Real"));
            var app = new TestApplication(
                options => options.AddElementAdapter(adapter),
                window =>
                {
                    var host = new Control { Name = FakeElementAdapter.HostName };
                    window.Controls.Add(host);
                    // The host's own control, and an unrelated live control the adapter has no business owning.
                    adapter.RawElements.Add(host);
                    adapter.RawElements.Add(window.Controls["Replaceable"]!);
                });
            using (app)
            {
                JArray nodes = await app.TreeAsync();
                string[] ids = nodes.OfType<JObject>().Select(node => (string)node["id"]!).ToArray();
                Assert.AreEqual(ids.Length, ids.Distinct().Count(), "Every id must appear once.");
                Assert.AreEqual("control", (string?)nodes.OfType<JObject>().Single(n => (string?)n["name"] == FakeElementAdapter.HostName)["kind"]);
                JObject replaceable = nodes.OfType<JObject>().Single(n => (string?)n["name"] == "Replaceable");
                Assert.AreEqual("control", (string?)replaceable["kind"]);
                Assert.AreEqual(1, Items(nodes).Length, "Only the real placement is an element.");

                JToken diagnostics = Success(await app.RequestAsync("diagnostics"));
                Assert.IsTrue(diagnostics["entries"]!.ToString().Contains("fake"), "The dropped control must be reported against the adapter.");

                // A prune pass (every windows request) must not drop the live control's entry.
                Success(await app.RequestAsync("windows"));
                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(replaceable)));
                Assert.AreEqual("Replaceable", (string?)inspected["name"]);
            }
        }

        [TestMethod]
        public async Task AnUnparentedControlMayBeEmittedAsAnElement()
        {
            var adapter = new FakeElementAdapter();
            var app = new TestApplication(
                options => options.AddElementAdapter(adapter),
                window =>
                {
                    var host = new Control { Name = FakeElementAdapter.HostName };
                    // Created on the UI thread; the fake adapter describes a control element by its name and state.
                    var floating = new Panel { Name = "Floating" };
                    window.Controls.Add(host);
                    // An unparented control (like a context menu) is not visited by the bridge; its host is.
                    adapter.RawElements.Add(floating);
                    adapter.RawElements.Add(host);
                });
            using (app)
            {
                JArray nodes = await app.TreeAsync();
                string[] ids = nodes.OfType<JObject>().Select(node => (string)node["id"]!).ToArray();
                Assert.AreEqual(ids.Length, ids.Distinct().Count(), "Every id must appear once.");
                JObject[] emitted = nodes.OfType<JObject>().Where(node => (string?)node["shortType"] == "Panel" && (string?)node["kind"] == "item").ToArray();
                Assert.AreEqual(1, emitted.Length, "The unparented control appears once, as an item.");
                Assert.AreEqual("control", (string?)nodes.OfType<JObject>().Single(n => (string?)n["name"] == FakeElementAdapter.HostName)["kind"],
                    "The host control, which has a parent, is still dropped as an element.");
                Assert.AreEqual(1, Items(nodes).Length);
            }
        }

        [TestMethod]
        public async Task WindowRootElementsHangUnderTheirWindow()
        {
            var adapter = new FakeElementAdapter();
            adapter.WindowRoots.Add(Place("Popup"));

            using (var app = StartWithFake(adapter))
            {
                JArray nodes = await app.TreeAsync();
                JObject window = nodes.OfType<JObject>().Single(node => (string?)node["name"] == "TestWindow");
                Assert.AreEqual((string?)window["id"], (string?)ItemNamed(nodes, "Popup")["parentId"]);
            }
        }

        private static async Task AssertStaleAsync(TestApplication app, string method, JObject node, string? message = null)
        {
            RpcResponse response = await app.RequestAsync(method, TargetParameters(node));
            Assert.AreEqual("stale_target", response.Error?.Code, message ?? method + " on a stale element.");
            Assert.IsNull(response.Result);
        }

        [TestMethod]
        public async Task RemovedElementFailsStaleTargetAndReAddedRestoresTheSameId()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement first = Place("First");
            adapter.HostPlacements.Add(first);
            adapter.HostPlacements.Add(Place("Other"));

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "First");
                Assert.AreEqual("First", (string?)Success(await app.RequestAsync("inspect", TargetParameters(node)))["name"]);

                await app.OnUiAsync(() => adapter.HostPlacements.Remove(first));
                await AssertStaleAsync(app, "inspect", node);
                Assert.AreEqual(1, Items(await app.TreeAsync()).Length, "Only the remaining element is listed.");
                await AssertStaleAsync(app, "inspect", node, "A tree request between must not resurrect the element.");

                await app.OnUiAsync(() => adapter.HostPlacements.Add(first));
                Assert.AreEqual("First", (string?)Success(await app.RequestAsync("inspect", TargetParameters(node)))["name"]);
                Assert.AreEqual((string?)node["id"], (string?)ItemNamed(await app.TreeAsync(), "First")["id"]);
            }
        }

        [TestMethod]
        public async Task MovedElementKeepsItsId()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement leaf = Place("Leaf");
            FakePlacement menu = Place("Menu", null, true);
            adapter.HostPlacements.Add(menu);
            adapter.HostPlacements.Add(leaf);

            using (var app = StartWithFake(adapter))
            {
                JArray before = await app.TreeAsync();
                JObject node = ItemNamed(before, "Leaf");
                string hostId = (string)node["parentId"]!;

                await app.OnUiAsync(() =>
                {
                    adapter.HostPlacements.Remove(leaf);
                    menu.Command!.Children.Add(leaf);
                });
                JArray after = await app.TreeAsync();
                JObject moved = ItemNamed(after, "Leaf");
                Assert.AreEqual((string?)node["id"], (string?)moved["id"]);
                Assert.AreEqual((string?)ItemNamed(after, "Menu")["id"], (string?)moved["parentId"]);
                Assert.AreNotEqual(hostId, (string?)moved["parentId"]);
                Assert.AreEqual("Leaf", (string?)Success(await app.RequestAsync("inspect", TargetParameters(node)))["name"]);
            }
        }

        [TestMethod]
        public async Task DisposedOwnerOrClosedFormMakesTheElementStale()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement onHostObserved = Place("OnHostObserved");
            FakePlacement onHostParent = Place("OnHostParent");
            FakePlacement onSecondObserved = Place("OnSecondObserved");
            FakePlacement onSecondParent = Place("OnSecondParent");
            adapter.HostPlacements.Add(onHostObserved);
            adapter.HostPlacements.Add(onHostParent);
            adapter.NamedHosts["SecondHost"] = new List<FakePlacement> { onSecondObserved, onSecondParent };

            using (var app = StartWithFake(adapter))
            {
                Form? second = null;
                Control? mainHost = null;
                await app.OnUiAsync(() =>
                {
                    mainHost = app.Window.Controls[FakeElementAdapter.HostName];
                    second = new Form { Name = "SecondWindow", Width = 200, Height = 150 };
                    var secondHost = new Control { Name = "SecondHost" };
                    second.Controls.Add(secondHost);
                    // Two placements use the adapter's GetParent; the other two rely on the observed parent.
                    onHostParent.Parent = mainHost;
                    onSecondParent.Parent = secondHost;
                    second.Show();
                });

                JArray tree = await app.TreeAsync();
                JObject[] nodes =
                {
                    ItemNamed(tree, "OnHostObserved"), ItemNamed(tree, "OnHostParent"),
                    ItemNamed(tree, "OnSecondObserved"), ItemNamed(tree, "OnSecondParent")
                };
                foreach (JObject node in nodes) Success(await app.RequestAsync("inspect", TargetParameters(node)));

                // Closing a form disposes the controls under it.
                await app.OnUiAsync(() => second!.Close());
                await AssertStaleAsync(app, "inspect", nodes[2]);
                await AssertStaleAsync(app, "inspect", nodes[3]);
                Success(await app.RequestAsync("inspect", TargetParameters(nodes[0])));

                await app.OnUiAsync(() =>
                {
                    app.Window.Controls.Remove(mainHost!);
                    mainHost!.Dispose();
                });
                await AssertStaleAsync(app, "inspect", nodes[0]);
                await AssertStaleAsync(app, "inspect", nodes[1]);
            }
        }

        [TestMethod]
        public async Task ReplacementWithTheSameNameGetsANewId()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement original = Place("Same");
            adapter.HostPlacements.Add(original);

            using (var app = StartWithFake(adapter))
            {
                JObject old = ItemNamed(await app.TreeAsync(), "Same");
                await app.OnUiAsync(() =>
                {
                    adapter.HostPlacements.Remove(original);
                    adapter.HostPlacements.Add(Place("Same"));
                });
                JObject replacement = ItemNamed(await app.TreeAsync(), "Same");
                Assert.AreNotEqual((string?)old["id"], (string?)replacement["id"]);
                await AssertStaleAsync(app, "inspect", old);
                Assert.AreEqual("Same", (string?)Success(await app.RequestAsync("inspect", TargetParameters(replacement)))["name"]);
            }
        }

        [TestMethod]
        public async Task InspectOnAnElementReturnsItsDescriptionOnly()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Menu", "Menu text", true, Place("Child")));

            using (var app = StartWithFake(adapter))
            {
                JArray tree = await app.TreeAsync();
                JObject host = tree.OfType<JObject>().Single(node => (string?)node["name"] == FakeElementAdapter.HostName);
                JObject menu = ItemNamed(tree, "Menu");
                JToken inspected = Success(await app.RequestAsync("inspect", TargetParameters(menu)));

                Assert.AreEqual("item", (string?)inspected["kind"]);
                Assert.AreEqual("fake", (string?)inspected["adapter"]);
                Assert.AreEqual("fake_item", (string?)inspected["role"]);
                Assert.AreEqual((string?)menu["id"], (string?)inspected["id"]);
                Assert.AreEqual((string?)host["id"], (string?)inspected["parentId"]);
                Assert.AreEqual("Menu", (string?)inspected["name"]);
                Assert.AreEqual("Menu text", (string?)inspected["text"]);
                Assert.AreEqual(true, (bool?)inspected["opened"]);
                Assert.AreEqual(1, (int)inspected["childCount"]!);
                CollectionAssert.AreEqual(new[] { "click" }, ((JArray)inspected["capabilities"]!).Select(c => (string?)c).ToArray());
                Assert.IsNull(inspected["items"], "An element has no items array.");
                Assert.IsNull(inspected["totalItems"]);
                Assert.IsNull(inspected["itemsTruncated"]);
            }
        }

        [TestMethod]
        public async Task ATreeRootedAtAClosedItemExpandsItsSubtree()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement closed = Place("Closed", null, false,
                Place("ChildA", null, false, Place("Grand")),
                Place("ChildB"));
            adapter.HostPlacements.Add(closed);

            using (var app = StartWithFake(adapter))
            {
                JArray full = await app.TreeAsync();
                JObject root = ItemNamed(full, "Closed");
                JObject host = full.OfType<JObject>().Single(node => (string?)node["name"] == FakeElementAdapter.HostName);
                Assert.AreEqual(true, (bool?)root["childrenOmitted"], "A window tree still collapses the closed item.");

                JObject parameters = TargetParameters(root);
                JToken result = Success(await app.RequestAsync("tree", parameters));
                JArray nodes = (JArray)result["nodes"]!;
                Assert.AreEqual(4, nodes.Count);
                Assert.AreEqual(false, (bool)result["truncated"]!);
                Assert.AreEqual((string?)root["id"], (string?)nodes[0]["id"], "The root node is first.");
                Assert.AreEqual((string?)host["id"], (string?)nodes[0]["parentId"], "The root reports its real parent.");
                Assert.AreEqual(false, (bool?)nodes[0]["childrenOmitted"]);
                JObject childA = ItemNamed(nodes, "ChildA");
                Assert.AreEqual(false, (bool?)childA["childrenOmitted"], "A nested closed dropdown expands too.");
                Assert.AreEqual((string?)root["id"], (string?)childA["parentId"]);
                Assert.AreEqual((string?)childA["id"], (string?)ItemNamed(nodes, "Grand")["parentId"]);
                Assert.AreEqual((string?)root["id"], (string?)ItemNamed(nodes, "ChildB")["parentId"]);

                // maxDepth counts from the root element.
                parameters["maxDepth"] = 1;
                JToken shallow = Success(await app.RequestAsync("tree", parameters));
                Assert.AreEqual(3, (int)shallow["nodeCount"]!);
                Assert.AreEqual(true, (bool)shallow["truncated"]!);
                Assert.IsFalse(Items((JArray)shallow["nodes"]!).Any(item => (string?)item["name"] == "Grand"));

                parameters["maxDepth"] = 0;
                JToken rootOnly = Success(await app.RequestAsync("tree", parameters));
                Assert.AreEqual(1, (int)rootOnly["nodeCount"]!);
                Assert.AreEqual(true, (bool)rootOnly["truncated"]!);

                // maxNodes still bounds the subtree.
                parameters["maxDepth"] = 8;
                parameters["maxNodes"] = 2;
                JToken limited = Success(await app.RequestAsync("tree", parameters));
                Assert.AreEqual(2, (int)limited["nodeCount"]!);
                Assert.AreEqual(true, (bool)limited["truncated"]!);
            }
        }

        [TestMethod]
        public async Task ControlOnlyOperationsOnAnElementAreUnsupportedAfterTheStaleCheck()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement placement = Place("Target");
            adapter.HostPlacements.Add(placement);

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Target");
                var mutations = new (string Method, Action<JObject> Configure)[]
                {
                    ("set_text", p => p["text"] = "x"),
                    ("set_value", p => p["value"] = 1),
                    ("set_checked", p => p["checked"] = true),
                    ("select", p => p["index"] = 0),
                    ("scroll", p => p["amount"] = 1),
                    ("ensure_visible", p => { })
                };
                var reads = new (string Method, Action<JObject> Configure)[]
                {
                    ("read_grid", p => { }),
                    ("capture", p => p["scope"] = "control"),
                    ("capture", p => p["scope"] = "window")
                };

                foreach (var mutation in mutations)
                {
                    JToken receipt = await app.ActAsync(mutation.Method, node, mutation.Configure);
                    Assert.AreEqual("failed", (string?)receipt["state"], mutation.Method);
                    Assert.AreEqual("unsupported_operation", (string?)receipt["error"]!["code"], mutation.Method);
                    Assert.AreEqual("This operation applies to controls, not elements.", (string?)receipt["error"]!["message"]);
                }
                foreach (var read in reads)
                {
                    JObject parameters = TargetParameters(node);
                    read.Configure(parameters);
                    RpcResponse response = await app.RequestAsync(read.Method, parameters);
                    Assert.AreEqual("unsupported_operation", response.Error?.Code, read.Method);
                    Assert.IsNull(response.Result);
                }

                await app.OnUiAsync(() => adapter.HostPlacements.Remove(placement));
                foreach (var mutation in mutations)
                {
                    JToken receipt = await app.ActAsync(mutation.Method, node, mutation.Configure);
                    Assert.AreEqual("failed", (string?)receipt["state"], mutation.Method);
                    Assert.AreEqual("stale_target", (string?)receipt["error"]!["code"], mutation.Method);
                }
                foreach (var read in reads)
                {
                    JObject parameters = TargetParameters(node);
                    read.Configure(parameters);
                    RpcResponse response = await app.RequestAsync(read.Method, parameters);
                    Assert.AreEqual("stale_target", response.Error?.Code, read.Method);
                }
            }
        }

        [TestMethod]
        public async Task ATreeRootedAtAStaleElementReturnsStaleTarget()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement placement = Place("Doomed");
            adapter.HostPlacements.Add(placement);

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Doomed");
                Assert.AreEqual(1, (int)Success(await app.RequestAsync("tree", TargetParameters(node)))["nodeCount"]!);
                await app.OnUiAsync(() => adapter.HostPlacements.Remove(placement));
                await AssertStaleAsync(app, "tree", node);
            }
        }

        [TestMethod]
        public async Task ElementsUnderAnExcludedControlAreAbsentAndTheirOldIdsAreStale()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Menu", null, true, Place("Child")));
            bool hideHost = false;
            var app = new TestApplication(
                options =>
                {
                    options.AddElementAdapter(adapter);
                    options.Exclude(control => hideHost && control.Name == FakeElementAdapter.HostName);
                },
                window => window.Controls.Add(new Control { Name = FakeElementAdapter.HostName }));
            using (app)
            {
                JArray tree = await app.TreeAsync();
                JObject menu = ItemNamed(tree, "Menu");
                JObject child = ItemNamed(tree, "Child");
                Success(await app.RequestAsync("inspect", TargetParameters(child)));

                await app.OnUiAsync(() => hideHost = true);
                Assert.AreEqual(0, Items(await app.TreeAsync()).Length, "Elements under an excluded control are not listed.");
                await AssertStaleAsync(app, "inspect", menu);
                await AssertStaleAsync(app, "inspect", child, "A nested element is stale too.");
                await AssertStaleAsync(app, "tree", child);

                await app.OnUiAsync(() => hideHost = false);
                Assert.AreEqual((string?)child["id"], (string?)ItemNamed(await app.TreeAsync(), "Child")["id"]);
                Success(await app.RequestAsync("inspect", TargetParameters(child)));
            }
        }

        [TestMethod]
        public async Task AnExcludedControlEmittedAsAnElementIsHiddenAndItsOldIdIsStale()
        {
            var adapter = new FakeElementAdapter();
            bool hideFloating = false;
            bool throwOnUnparented = false;
            var app = new TestApplication(
                options =>
                {
                    options.AddElementAdapter(adapter);
                    options.Exclude(control =>
                    {
                        if (throwOnUnparented && control.Parent == null && !(control is Form))
                            throw new InvalidOperationException("Predicate written for parented controls.");
                        return hideFloating && control.Name == "Floating";
                    });
                },
                window =>
                {
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                    adapter.RawElements.Add(new Panel { Name = "Floating" });
                });
            using (app)
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Floating");
                Success(await app.RequestAsync("inspect", TargetParameters(node)));

                await app.OnUiAsync(() => hideFloating = true);
                Assert.AreEqual(0, Items(await app.TreeAsync()).Length, "An excluded control is not listed as an element either.");
                await AssertStaleAsync(app, "inspect", node);
                await AssertStaleAsync(app, "tree", node);
                JToken diagnostics = Success(await app.RequestAsync("diagnostics"));
                Assert.IsFalse(diagnostics["entries"]!.ToString().Contains("adapter_failed"),
                    "Exclusion is not an adapter failure.");

                await app.OnUiAsync(() => hideFloating = false);
                Assert.AreEqual((string?)node["id"], (string?)ItemNamed(await app.TreeAsync(), "Floating")["id"]);

                // A predicate that throws on the unparented control fails closed: the control is hidden.
                await app.OnUiAsync(() => throwOnUnparented = true);
                Assert.AreEqual(0, Items(await app.TreeAsync()).Length, "A throwing predicate hides the element.");
                await AssertStaleAsync(app, "inspect", node);
            }
        }

        // --- element actions: click, open, close ------------------------------------------------

        private static async Task<JObject> ItemUnderAsync(TestApplication app, JObject root, string name)
        {
            JToken tree = Success(await app.RequestAsync("tree", TargetParameters(root)));
            return ItemNamed((JArray)tree["nodes"]!, name);
        }

        private static JToken ActionResult(JToken receipt)
        {
            Assert.AreEqual("completed", (string?)receipt["state"], receipt.ToString());
            return receipt["result"]!;
        }

        private static void AssertFailed(JToken receipt, string code, string? message = null)
        {
            Assert.AreEqual("failed", (string?)receipt["state"], receipt.ToString());
            Assert.AreEqual(code, (string?)receipt["error"]!["code"], receipt.ToString());
            if (message != null) Assert.AreEqual(message, (string?)receipt["error"]!["message"]);
        }

        [TestMethod]
        public async Task ClickRunsTheHandlerForAnItemInsideAClosedMenu()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Menu", null, false, Place("Item")));
            int clicks = 0;
            string? seenAction = null;
            adapter.OnExecute = (element, action) =>
            {
                clicks++;
                seenAction = action;
                return ElementActionOutcome.HandlerReturned;
            };

            using (var app = StartWithFake(adapter))
            {
                JObject menu = ItemNamed(await app.TreeAsync(), "Menu");
                JObject item = await ItemUnderAsync(app, menu, "Item");

                JToken result = ActionResult(await app.ActAsync("invoke", item));
                Assert.AreEqual(1, clicks);
                Assert.AreEqual("click", seenAction, "The action defaults to click.");
                Assert.AreEqual("invoke", (string?)result["operation"]);
                Assert.AreEqual("click", (string?)result["action"]);
                Assert.AreEqual(true, (bool?)result["handlerReturned"]);
                Assert.AreEqual(false, (bool?)result["handlerDeferred"]);
                Assert.IsNull(result["opened"], "An item without a dropdown reports no opened state.");
            }
        }

        [TestMethod]
        public async Task DisabledItemGivesDisabledControlAndAvailableFalseGivesHiddenControl()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement placement = Place("Item");
            adapter.HostPlacements.Add(placement);
            int clicks = 0;
            adapter.OnExecute = (element, action) => { clicks++; return ElementActionOutcome.HandlerReturned; };

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");

                await app.OnUiAsync(() => placement.Command!.Enabled = false);
                AssertFailed(await app.ActAsync("invoke", node), "disabled_control");

                await app.OnUiAsync(() => { placement.Command!.Enabled = true; adapter.Available = false; });
                AssertFailed(await app.ActAsync("invoke", node), "hidden_control");

                await app.OnUiAsync(() => placement.Command!.Enabled = false);
                // Enabled is checked before Available.
                AssertFailed(await app.ActAsync("invoke", node), "disabled_control");
                Assert.AreEqual(0, clicks, "Prechecks refuse before the adapter runs.");

                await app.OnUiAsync(() => { placement.Command!.Enabled = true; adapter.Available = true; });
                ActionResult(await app.ActAsync("invoke", node));
                Assert.AreEqual(1, clicks);
            }
        }

        [TestMethod]
        public async Task OpenAndCloseReportTheObservedPostState()
        {
            var adapter = new FakeElementAdapter();
            adapter.ExtraCapabilities.Add("open");
            adapter.ExtraCapabilities.Add("close");
            FakePlacement working = Place("Working", null, false);
            FakePlacement inert = Place("Inert", null, false);
            adapter.HostPlacements.Add(working);
            adapter.HostPlacements.Add(inert);
            adapter.OnExecute = (element, action) =>
            {
                var placement = (FakePlacement)element;
                if (placement == working) placement.Command!.Opened = action == "open";
                return ElementActionOutcome.HandlerReturned;
            };

            using (var app = StartWithFake(adapter))
            {
                JArray tree = await app.TreeAsync();
                JObject workingNode = ItemNamed(tree, "Working");
                JObject inertNode = ItemNamed(tree, "Inert");

                JToken opened = ActionResult(await app.ActAsync("invoke", workingNode, p => p["action"] = "open"));
                Assert.AreEqual("open", (string?)opened["action"]);
                Assert.AreEqual(true, (bool?)opened["opened"]);
                Assert.AreEqual(true, (bool?)ItemNamed(await app.TreeAsync(), "Working")["opened"]);

                JToken closed = ActionResult(await app.ActAsync("invoke", workingNode, p => p["action"] = "close"));
                Assert.AreEqual(false, (bool?)closed["opened"]);

                JToken ignored = ActionResult(await app.ActAsync("invoke", inertNode, p => p["action"] = "open"));
                Assert.AreEqual(false, (bool?)ignored["opened"],
                    "The observed state is reported, never the requested one.");
            }
        }

        [TestMethod]
        public async Task OpenOnANonRealizedElementGivesNotRealized()
        {
            var adapter = new FakeElementAdapter { Visible = false };
            adapter.ExtraCapabilities.Add("open");
            adapter.ExtraCapabilities.Add("close");
            adapter.HostPlacements.Add(Place("Item", null, false));
            int executions = 0;
            adapter.OnExecute = (element, action) => { executions++; return ElementActionOutcome.HandlerReturned; };

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                const string message = "The element has no on-screen layout; open its parent menu or show the window first.";
                AssertFailed(await app.ActAsync("invoke", node, p => p["action"] = "open"), "not_realized", message);
                AssertFailed(await app.ActAsync("invoke", node, p => p["action"] = "close"), "not_realized", message);
                Assert.AreEqual(0, executions);

                ActionResult(await app.ActAsync("invoke", node));
                Assert.AreEqual(1, executions, "A click needs only enabled and available.");
            }
        }

        [TestMethod]
        public async Task AnActionNotInCapabilitiesGivesUnsupportedOperation()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Item"));
            int executions = 0;
            adapter.OnExecute = (element, action) => { executions++; return ElementActionOutcome.HandlerReturned; };

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                foreach (string action in new[] { "open", "close", "focus", "undeclared" })
                    AssertFailed(await app.ActAsync("invoke", node, p => p["action"] = action), "unsupported_operation");
                Assert.AreEqual(0, executions);
            }
        }

        [TestMethod]
        public async Task AdapterRefusalsAndFailuresMapToErrorCodes()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Item"));
            Exception? thrown = null;
            adapter.OnExecute = (element, action) => throw thrown!;

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                var cases = new (Exception Exception, string Code)[]
                {
                    (new ElementActionException(ElementActionError.Unsupported, "no"), "unsupported_operation"),
                    (new ElementActionException(ElementActionError.Disabled, "no"), "disabled_control"),
                    (new ElementActionException(ElementActionError.Hidden, "no"), "hidden_control"),
                    (new ElementActionException(ElementActionError.NotRealized, "no"), "not_realized"),
                    (new InvalidOperationException("handler broke"), "operation_failed")
                };
                foreach (var failure in cases)
                {
                    thrown = failure.Exception;
                    JToken receipt = await app.ActAsync("invoke", node);
                    AssertFailed(receipt, failure.Code, failure.Exception.Message);
                }
            }
        }

        [TestMethod]
        public async Task HandlerDeferredIsReportedWhenTheAdapterSaysSo()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Item"));
            adapter.OnExecute = (element, action) => ElementActionOutcome.HandlerDeferred;

            using (var app = StartWithFake(adapter))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                JToken result = ActionResult(await app.ActAsync("invoke", node));
                Assert.AreEqual(true, (bool?)result["handlerReturned"]);
                Assert.AreEqual(true, (bool?)result["handlerDeferred"]);
            }
        }

        [TestMethod]
        public async Task AModalHandlerLeavesTheReceiptStartedWhileTheDialogIsInspectable()
        {
            var adapter = new FakeElementAdapter();
            adapter.HostPlacements.Add(Place("Item"));
            Form? owner = null;
            adapter.OnExecute = (element, action) =>
            {
                using (var dialog = new Form { Name = "ElementModal", Text = "Element modal", Width = 240, Height = 150 })
                {
                    var close = new Button { Name = "CloseElementModal", Text = "Close", Top = 20 };
                    close.Click += (sender, eventArgs) => dialog.Close();
                    dialog.Controls.Add(close);
                    dialog.ShowDialog(owner);
                }
                return ElementActionOutcome.HandlerReturned;
            };

            using (var app = new TestApplication(
                options => options.AddElementAdapter(adapter),
                window =>
                {
                    owner = window;
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                }))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Item");
                JToken opening = Success(await app.RequestAsync("invoke", TargetParameters(node)));
                JObject close = await app.WaitForNodeAsync("CloseElementModal");
                JToken pending = Success(await app.RequestAsync("action_status", ReceiptParameters(opening)));
                Assert.AreEqual("started", (string?)pending["state"]);
                Assert.IsTrue((await app.TreeAsync()).Any(n => (string?)n["name"] == "ElementModal"),
                    "The dialog is inspectable while the handler is still inside ShowDialog.");

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(close)));
                await app.WaitForCompletionAsync(closing);
                await app.WaitForCompletionAsync(opening);
                Assert.IsFalse((await app.TreeAsync()).Any(n => (string?)n["name"] == "ElementModal"));
            }
        }

        [TestMethod]
        public async Task AQueuedClickWhoseElementIsRemovedBeforeStartFailsStaleAndNeverRunsTheHandler()
        {
            var adapter = new FakeElementAdapter();
            FakePlacement placement = Place("Doomed");
            adapter.HostPlacements.Add(placement);
            int clicks = 0;
            adapter.OnExecute = (element, action) =>
            {
                Interlocked.Increment(ref clicks);
                return ElementActionOutcome.HandlerReturned;
            };

            using (var app = StartWithFake(adapter))
            using (var gate = new ManualResetEventSlim())
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Doomed");
                // Held on the UI thread, the element is removed the moment the gate opens:
                // the invoke below is accepted first and would run second.
                Task block = app.BlockUiAsync(gate, () => adapter.HostPlacements.Remove(placement));

                JToken receipt = Success(await app.RequestAsync("invoke", TargetParameters(node)));
                Assert.AreEqual("queued", (string?)receipt["state"]);
                JToken pending = Success(await app.RequestAsync("action_status", ReceiptParameters(receipt)));
                Assert.AreEqual("queued", (string?)pending["state"], "The action cannot have started while the UI thread is held.");

                gate.Set();
                await block;
                JToken status = await app.WaitForTerminalAsync(receipt);
                AssertFailed(status, "stale_target");
                Assert.AreEqual(0, Volatile.Read(ref clicks), "The handler of a removed element must not run.");
            }
        }

        [TestMethod]
        public async Task AHandlerThatDisposesItsOwnerCompletesAndTheIdThenGoesStale()
        {
            var adapter = new FakeElementAdapter();
            adapter.ExtraCapabilities.Add("open");
            FakePlacement placement = Place("Self", null, true);
            adapter.HostPlacements.Add(placement);
            Form? owner = null;
            adapter.OnExecute = (element, action) =>
            {
                Control host = owner!.Controls[FakeElementAdapter.HostName]!;
                owner.Controls.Remove(host);
                host.Dispose();
                return ElementActionOutcome.HandlerReturned;
            };

            using (var app = new TestApplication(
                options => options.AddElementAdapter(adapter),
                window =>
                {
                    owner = window;
                    window.Controls.Add(new Control { Name = FakeElementAdapter.HostName });
                }))
            {
                JObject node = ItemNamed(await app.TreeAsync(), "Self");
                JToken result = ActionResult(await app.ActAsync("invoke", node));
                Assert.AreEqual(true, (bool?)result["handlerReturned"]);
                Assert.IsNull(result["opened"], "A gone element must not be described after its handler ran.");
                await AssertStaleAsync(app, "inspect", node);
            }
        }
    }
}
