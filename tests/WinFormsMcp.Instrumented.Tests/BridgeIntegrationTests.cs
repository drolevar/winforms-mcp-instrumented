using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

[assembly: DoNotParallelize]

namespace WinFormsMcp.Instrumented.Tests
{
    /// <summary>Real named-pipe tests against a dedicated STA WinForms message loop.</summary>
    /// <remarks>Requires Windows and .NET Framework 4.8; these are not portable UI tests.</remarks>
    [TestClass]
    public sealed partial class BridgeIntegrationTests
    {
        [TestMethod]
        public async Task AControlCreatedAfterStartupAppearsWithoutRegistration()
        {
            using (var app = new TestApplication())
            {
                JArray before = await app.TreeAsync();
                Assert.IsFalse(before.Any(node => (string?)node["name"] == "AddedAtRuntime"));

                await app.OnUiAsync(() => app.Window.Controls.Add(new TextBox
                {
                    Name = "AddedAtRuntime", Text = "Created after the bridge", Top = 160, Width = 250
                }));

                JObject node = await app.FindAsync("AddedAtRuntime");
                Assert.AreEqual("Created after the bridge", (string?)node["text"]);
            }
        }

        [TestMethod]
        public async Task ReplacementWithTheSameNameGetsANewIdAndTheOldIdIsStale()
        {
            using (var app = new TestApplication())
            {
                JObject old = await app.FindAsync("Replaceable");
                await app.OnUiAsync(() =>
                {
                    Control original = app.Window.Controls["Replaceable"]!;
                    app.Window.Controls.Remove(original);
                    original.Dispose();
                    app.Window.Controls.Add(new Button { Name = "Replaceable", Text = "Replacement", Top = 20 });
                });

                JObject replacement = await app.FindAsync("Replaceable");
                Assert.AreNotEqual((string?)old["id"], (string?)replacement["id"]);
                RpcResponse stale = await app.RequestAsync("inspect", TargetParameters(old));
                Assert.AreEqual("stale_target", stale.Error?.Code);
                Assert.IsNull(stale.Result);
            }
        }

        [TestMethod]
        public async Task RecreatingTheNativeHandlePreservesManagedIdentity()
        {
            using (var app = new TestApplication())
            {
                JObject before = await app.FindAsync("RecreatedHandle");
                await app.OnUiAsync(() => app.HandleButton.RecreateForTest());
                JObject after = await app.FindAsync("RecreatedHandle");
                Assert.AreEqual((string?)before["id"], (string?)after["id"]);
                Assert.IsNotNull(Success(await app.RequestAsync("inspect", TargetParameters(before))));
            }
        }

        [TestMethod]
        public async Task ReparentingPreservesIdentityAndUpdatesContainment()
        {
            using (var app = new TestApplication())
            {
                JObject before = await app.FindAsync("ReparentedChild");
                await app.OnUiAsync(() => app.RightPanel.Controls.Add(app.ReparentedChild));
                JObject after = await app.FindAsync("ReparentedChild");
                Assert.AreEqual((string?)before["id"], (string?)after["id"]);
                Assert.AreNotEqual((string?)before["parentId"], (string?)after["parentId"]);
            }
        }

        [TestMethod]
        public async Task AnOldApplicationGenerationCannotTargetACurrentControl()
        {
            using (var app = new TestApplication())
            {
                JObject node = await app.FindAsync("Replaceable");
                JObject parameters = TargetParameters(node);
                parameters["target"]!["generation"] = Guid.NewGuid().ToString("N");
                RpcResponse response = await app.RequestAsync("inspect", parameters);
                Assert.AreEqual("stale_generation", response.Error?.Code);
            }
        }

        [TestMethod]
        public async Task AModalActionReturnsAReceiptAndTheDialogCanBeClosedByTheNextAction()
        {
            using (var app = new TestApplication())
            {
                JObject button = await app.FindAsync("OpenModal");
                JToken opening = Success(await app.RequestAsync("invoke", TargetParameters(button)));
                Assert.IsNotNull(opening["actionId"]);

                JObject close = await app.WaitForNodeAsync("CloseModal");
                JToken pending = Success(await app.RequestAsync("action_status", ReceiptParameters(opening)));
                Assert.AreEqual("started", (string?)pending["state"]);

                JToken closing = Success(await app.RequestAsync("invoke", TargetParameters(close)));
                await app.WaitForCompletionAsync(closing);
                await app.WaitForCompletionAsync(opening);
                JArray tree = await app.TreeAsync();
                Assert.IsFalse(tree.Any(node => (string?)node["name"] == "ModalWindow"));
            }
        }

        [TestMethod]
        public async Task EnsureVisibleSelectsTheAncestorTabBeforeScrollingItsContainer()
        {
            using (var app = new TestApplication())
            {
                // The second tab has never been shown, so its AutoScroll panel is hidden and unlaid-out.
                JObject far = await app.FindAsync("FarChild");
                JToken receipt = Success(await app.RequestAsync("ensure_visible", TargetParameters(far)));
                await app.WaitForCompletionAsync(receipt);

                Assert.AreEqual(1, (int)(await app.FindAsync("Tabs"))["selectedIndex"]!, "The ancestor tab was not selected.");
                JObject panel = await app.FindAsync("ScrollPanel");
                JObject child = await app.FindAsync("FarChild");
                int panelTop = (int)panel["bounds"]!["y"]!;
                int panelBottom = panelTop + (int)panel["bounds"]!["height"]!;
                int childTop = (int)child["bounds"]!["y"]!;
                Assert.IsTrue(childTop >= panelTop && childTop < panelBottom,
                    "The child was not scrolled into its container's viewport: child top " + childTop +
                    ", viewport " + panelTop + ".." + panelBottom + ".");
            }
        }

        [TestMethod]
        public async Task AnIncorrectTokenIsRejectedAndAValidClientCanReconnect()
        {
            using (var app = new TestApplication())
            {
                using (var wrongClient = new NamedPipeClientStream(".", app.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await wrongClient.ConnectAsync(5000, deadline.Token);
                    await FrameCodec.WriteAsync(wrongClient, new RpcRequest
                    {
                        Id = "unauthorized-hello",
                        Token = new string('x', 64),
                        Method = "hello"
                    }, deadline.Token);
                    RpcResponse? response = await FrameCodec.ReadAsync<RpcResponse>(wrongClient, deadline.Token);
                    Assert.IsNotNull(response);
                    Assert.AreEqual("unauthorized", response!.Error?.Code);
                    Assert.IsNull(response.Result);
                }

                JObject validNode = await app.FindAsync("Replaceable");
                Assert.IsNotNull(validNode["id"]);
            }
        }

        private static JObject TargetParameters(JToken node) => new JObject
        {
            ["target"] = new JObject
            {
                ["id"] = (string?)node["id"],
                ["generation"] = (string?)node["generation"]
            }
        };

        private static JObject ReceiptParameters(JToken receipt) => new JObject
        {
            ["actionId"] = (string?)receipt["actionId"],
            ["generation"] = (string?)receipt["generation"]
        };

        private static JToken Success(RpcResponse response)
        {
            Assert.IsNull(response.Error, response.Error?.Code + ": " + response.Error?.Message);
            Assert.IsNotNull(response.Result);
            return response.Result!;
        }

        private sealed class RecreatingButton : Button
        {
            internal void RecreateForTest()
            {
                _ = Handle;
                RecreateHandle();
            }
        }

        private sealed class TestApplication : IDisposable
        {
            private readonly string? oldPipe;
            private readonly string? oldToken;
            private readonly string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
            private readonly SemaphoreSlim requests = new SemaphoreSlim(1, 1);
            private readonly Thread uiThread;
            private Exception? startupFailure;
            private NamedPipeClientStream? client;
            private int requestId;
            private int disposed;

            private readonly Action<McpBridgeBuilder>? configure;
            private readonly Action<Form>? populate;

            internal TestApplication(Action<McpBridgeBuilder>? configure = null, Action<Form>? populate = null)
            {
                this.configure = configure;
                this.populate = populate;
                PipeName = "WinFormsMcp.Tests." + Guid.NewGuid().ToString("N");
                oldPipe = Environment.GetEnvironmentVariable(BridgeProtocol.EnvironmentPipeName);
                oldToken = Environment.GetEnvironmentVariable(BridgeProtocol.EnvironmentToken);
                Environment.SetEnvironmentVariable(BridgeProtocol.EnvironmentPipeName, PipeName);
                Environment.SetEnvironmentVariable(BridgeProtocol.EnvironmentToken, token);
                uiThread = new Thread(Run) { IsBackground = true, Name = "WinForms MCP test UI" };
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(10)) || startupFailure != null)
                {
                    Dispose();
                    throw new InvalidOperationException("The test WinForms message loop could not start.", startupFailure);
                }
            }

            internal string PipeName { get; }
            internal Form Window { get; private set; } = null!;
            internal RecreatingButton HandleButton { get; private set; } = null!;
            internal Panel RightPanel { get; private set; } = null!;
            internal Button ReparentedChild { get; private set; } = null!;
            internal TabControl Tabs { get; private set; } = null!;
            internal Button DoomedButton { get; private set; } = null!;
            internal int DoomedClicks => Volatile.Read(ref doomedClicks);
            private int doomedClicks;

            private void Run()
            {
                try
                {
                    using (McpBridge.Start(options =>
                    {
                        options.UiRequestTimeout = TimeSpan.FromSeconds(3);
                        options.Exclude(control => control.Name == "Excluded");
                        configure?.Invoke(options);
                    }))
                    using (var window = new Form { Name = "TestWindow", Text = "WinForms MCP tests", Width = 500, Height = 400 })
                    {
                        Window = window;
                        window.Controls.Add(new Button { Name = "Replaceable", Text = "Original", Top = 20 });
                        HandleButton = new RecreatingButton { Name = "RecreatedHandle", Text = "Handle", Top = 50 };
                        window.Controls.Add(HandleButton);
                        var open = new Button { Name = "OpenModal", Text = "Open modal", Top = 80 };
                        open.Click += (sender, eventArgs) =>
                        {
                            using (var dialog = new Form { Name = "ModalWindow", Text = "Modal test", Width = 240, Height = 150 })
                            {
                                var close = new Button { Name = "CloseModal", Text = "Close", Top = 20 };
                                close.Click += (closeSender, closeEventArgs) => dialog.Close();
                                dialog.Controls.Add(close);
                                dialog.ShowDialog(window);
                            }
                        };
                        window.Controls.Add(open);
                        var left = new Panel { Name = "LeftPanel", Left = 0, Top = 200, Width = 180, Height = 100 };
                        RightPanel = new Panel { Name = "RightPanel", Left = 200, Top = 200, Width = 180, Height = 100 };
                        ReparentedChild = new Button { Name = "ReparentedChild", Text = "Move me" };
                        left.Controls.Add(ReparentedChild);
                        window.Controls.Add(left);
                        window.Controls.Add(RightPanel);
                        Tabs = new TabControl { Name = "Tabs", Left = 250, Top = 0, Width = 230, Height = 190 };
                        var firstPage = new TabPage("First") { Name = "FirstPage" };
                        var secondPage = new TabPage("Second") { Name = "SecondPage" };
                        var scrolling = new Panel { Name = "ScrollPanel", Dock = DockStyle.Fill, AutoScroll = true };
                        scrolling.Controls.Add(new TextBox { Name = "FarChild", Left = 10, Top = 1700, Width = 120 });
                        secondPage.Controls.Add(scrolling);
                        Tabs.TabPages.Add(firstPage);
                        Tabs.TabPages.Add(secondPage);
                        window.Controls.Add(Tabs);

                        // Fixtures for validation, paging, redaction, exclusion and race tests.
                        window.Controls.Add(new Button { Name = "DisabledButton", Text = "Disabled", Enabled = false, Top = 110 });
                        window.Controls.Add(new Button { Name = "HiddenButton", Text = "Hidden", Visible = false, Top = 110, Left = 80 });
                        window.Controls.Add(new TextBox { Name = "ReadOnlyText", Text = "fixed", ReadOnly = true, Top = 140 });
                        window.Controls.Add(new TextBox { Name = "Password", Text = "secret", UseSystemPasswordChar = true, Top = 140, Left = 110 });
                        window.Controls.Add(new TextBox { Name = "FreeText", Top = 140, Left = 220 });
                        window.Controls.Add(new NumericUpDown { Name = "Number", Minimum = 0, Maximum = 10, Value = 5, Top = 170 });
                        window.Controls.Add(new Label { Name = "PlainLabel", Text = "label", Top = 170, Left = 110 });
                        DoomedButton = new Button { Name = "Doomed", Text = "Doomed", Top = 170, Left = 200 };
                        DoomedButton.Click += (_, __) => Interlocked.Increment(ref doomedClicks);
                        window.Controls.Add(DoomedButton);
                        var bigList = new ListBox { Name = "BigList", Top = 200, Left = 380, Width = 100, Height = 80 };
                        for (int i = 0; i < 250; i++) bigList.Items.Add("Item " + i);
                        window.Controls.Add(bigList);
                        var bigGrid = new DataGridView { Name = "BigGrid", AllowUserToAddRows = false, Top = 290, Left = 380, Width = 100, Height = 80 };
                        bigGrid.Columns.Add("a", "A");
                        bigGrid.Columns.Add("b", "B");
                        for (int i = 0; i < 250; i++) bigGrid.Rows.Add("a" + i, "b" + i);
                        window.Controls.Add(bigGrid);
                        var excluded = new Panel { Name = "Excluded", Top = 290, Left = 0, Width = 100, Height = 50 };
                        excluded.Controls.Add(new TextBox { Name = "InsideExcluded" });
                        window.Controls.Add(excluded);
                        populate?.Invoke(window);
                        window.Shown += (_, __) => ready.Set();
                        Application.Run(window);
                    }
                }
                catch (Exception exception)
                {
                    startupFailure = exception;
                    ready.Set();
                }
            }

            internal Task OnUiAsync(Action action)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Window.BeginInvoke(new Action(() =>
                {
                    try { action(); completion.SetResult(true); }
                    catch (Exception exception) { completion.SetException(exception); }
                }));
                return completion.Task;
            }

            internal async Task<RpcResponse> RequestAsync(string method, JObject? parameters = null)
            {
                await requests.WaitAsync();
                try
                {
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        if (client == null)
                        {
                            client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                            await client.ConnectAsync(5000, deadline.Token);
                            Success(await ExchangeAsync("hello", new JObject(), deadline.Token));
                        }
                        return await ExchangeAsync(method, parameters ?? new JObject(), deadline.Token);
                    }
                }
                finally { requests.Release(); }
            }

            private async Task<RpcResponse> ExchangeAsync(string method, JObject parameters, CancellationToken cancellation)
            {
                string id = Interlocked.Increment(ref requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await FrameCodec.WriteAsync(client!, new RpcRequest
                {
                    Id = id, Token = token, Method = method, Parameters = parameters
                }, cancellation);
                RpcResponse? response = await FrameCodec.ReadAsync<RpcResponse>(client!, cancellation);
                Assert.IsNotNull(response, "The bridge disconnected before replying.");
                Assert.AreEqual(id, response!.Id);
                return response;
            }

            /// <summary>Queues a mutation and waits until its receipt is completed or failed.</summary>
            internal async Task<JToken> ActAsync(string method, JToken node, Action<JObject>? configure = null)
            {
                JObject parameters = TargetParameters(node);
                configure?.Invoke(parameters);
                return await WaitForTerminalAsync(Success(await RequestAsync(method, parameters)));
            }

            internal async Task<JToken> WaitForTerminalAsync(JToken receipt)
            {
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                do
                {
                    JToken status = Success(await RequestAsync("action_status", ReceiptParameters(receipt)));
                    string? state = (string?)status["state"];
                    if (state == "completed" || state == "failed") return status;
                    await Task.Delay(25);
                } while (DateTime.UtcNow < deadline);
                Assert.Fail("The action reached neither completed nor failed.");
                throw new InvalidOperationException("Unreachable assertion failure.");
            }

            /// <summary>
            /// Occupies the UI thread until the gate opens, then runs an optional step on that thread.
            /// Requests posted after this call queue behind it, which makes races deterministic.
            /// </summary>
            internal Task BlockUiAsync(ManualResetEventSlim gate, Action? afterRelease = null)
                => OnUiAsync(() =>
                {
                    gate.Wait(TimeSpan.FromSeconds(20));
                    afterRelease?.Invoke();
                });

            internal async Task<JArray> TreeAsync()
            {
                JToken result = Success(await RequestAsync("tree"));
                return (JArray)result["nodes"]!;
            }

            internal async Task<JObject> FindAsync(string name)
            {
                JObject[] found = (await TreeAsync()).OfType<JObject>()
                    .Where(node => (string?)node["name"] == name).ToArray();
                Assert.AreEqual(1, found.Length, "Expected exactly one control named " + name + ".");
                return found[0];
            }

            internal async Task<JObject> WaitForNodeAsync(string name)
            {
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                do
                {
                    JObject? found = (await TreeAsync()).OfType<JObject>()
                        .SingleOrDefault(node => (string?)node["name"] == name);
                    if (found != null) return found;
                    await Task.Delay(25);
                } while (DateTime.UtcNow < deadline);
                Assert.Fail("Control did not appear: " + name + ".");
                throw new InvalidOperationException("Unreachable assertion failure.");
            }

            internal async Task WaitForCompletionAsync(JToken receipt)
            {
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                do
                {
                    JToken status = Success(await RequestAsync("action_status", ReceiptParameters(receipt)));
                    string? state = (string?)status["state"];
                    if (state == "completed") return;
                    Assert.AreNotEqual("failed", state, status.ToString());
                    await Task.Delay(25);
                } while (DateTime.UtcNow < deadline);
                Assert.Fail("The action did not complete before its deadline.");
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                client?.Dispose();
                try
                {
                    if (Window != null)
                        Window.BeginInvoke(new Action(() =>
                        {
                            foreach (Form dialog in Window.OwnedForms) dialog.Close();
                            Window.Close();
                        }));
                }
                catch (InvalidOperationException) { }

                bool stopped = uiThread.Join(TimeSpan.FromSeconds(10));
                Environment.SetEnvironmentVariable(BridgeProtocol.EnvironmentPipeName, oldPipe);
                Environment.SetEnvironmentVariable(BridgeProtocol.EnvironmentToken, oldToken);
                if (stopped) ready.Dispose();
                requests.Dispose();
                if (!stopped) throw new InvalidOperationException("The test UI thread did not stop.");
            }
        }
    }
}
