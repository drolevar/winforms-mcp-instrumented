using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace WinFormsMcp.Instrumented.Server.Tests;

/// <summary>Portable checks of sidecar behavior that does not need a running application.</summary>
[TestClass]
public sealed class ServerContractTests
{
    [TestMethod]
    public void BridgeErrorsUseTheDocumentedCamelCaseKeys()
    {
        // Regression: serializing RpcError directly produced Code/Message/Details, so an error from
        // the bridge looked different from one produced by the sidecar itself.
        JObject error = ApplicationSession.ErrorObject(new RpcError
        {
            Code = "stale_target",
            Message = "The observed control no longer exists.",
            Details = new JObject { ["delivery"] = "not_sent" }
        });

        CollectionAssert.AreEqual(new[] { "code", "message", "details" }, error.Properties().Select(p => p.Name).ToArray());
        Assert.AreEqual("stale_target", (string?)error["code"]);
        Assert.AreEqual("not_sent", (string?)error["details"]!["delivery"]);
    }

    [TestMethod]
    public void BridgeAndSidecarErrorsHaveTheSameShape()
    {
        JObject sidecar = (JObject)ApplicationSession.Error("not_configured", "m")["error"]!;
        JObject bridge = ApplicationSession.ErrorObject(new RpcError { Code = "stale_target", Message = "m" });

        foreach (string key in new[] { "code", "message" })
        {
            Assert.IsNotNull(sidecar[key], "sidecar error lacks " + key);
            Assert.IsNotNull(bridge[key], "bridge error lacks " + key);
        }
    }

    [TestMethod]
    public void ErrorWithoutDetailsSerializesDetailsAsNull()
    {
        JObject error = ApplicationSession.ErrorObject(new RpcError { Code = "c", Message = "m" });
        Assert.AreEqual(JTokenType.Null, error["details"]!.Type);
    }

    [TestMethod]
    public void LaunchModeRequiresNoPipeOrToken()
    {
        ServerOptions options = ServerOptions.Parse(new[] { "--app", @"C:\apps\demo.exe" });
        StringAssert.StartsWith(options.PipeName, "wfmi-");
        Assert.AreEqual(64, options.Token.Length);
    }

    [TestMethod]
    public void AttachModeNeedsBothAPipeAndATokenVariable()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "demo-pipe" }));
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--token-env", "SOME_VARIABLE" }));
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(Array.Empty<string>()));
    }

    [TestMethod]
    public void ATokenFromTheEnvironmentMustBeLongEnoughAndIsNeverAnArgument()
    {
        const string variable = "WFMI_TEST_TOKEN_SERVER_CONTRACT";
        try
        {
            Environment.SetEnvironmentVariable(variable, "too-short");
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", variable }));

            string token = new('a', 32);
            Environment.SetEnvironmentVariable(variable, token);
            ServerOptions options = ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", variable });
            Assert.AreEqual(token, options.Token);
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token", token }));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [TestMethod]
    public void PipeNamesAreRestrictedToASafeAlphabet()
    {
        foreach (string bad in new[] { @"a\b", "a/b", "a b", "", new string('x', 129) })
            Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--app", @"C:\a.exe", "--pipe", bad }), bad);
    }

    [TestMethod]
    public void ApplicationArgumentsRequireALaunchableApplication()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ServerOptions.Parse(new[] { "--pipe", "p", "--token-env", "PATH", "--", "x" }));
        ServerOptions options = ServerOptions.Parse(new[] { "--app", @"C:\a.exe", "--", "--flag", "value" });
        CollectionAssert.AreEqual(new[] { "--flag", "value" }, options.ApplicationArguments);
    }

    private static MethodInfo ToolMethod(string toolName) =>
        typeof(WinFormsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

    private static string ToolDescription(string toolName) =>
        ToolMethod(toolName).GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string ParameterDescription(string toolName, string parameterName) =>
        ToolMethod(toolName).GetParameters().Single(p => p.Name == parameterName)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

    [TestMethod]
    public void TheToolSurfaceHasTwentyTools()
    {
        int count = typeof(WinFormsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);
        Assert.AreEqual(20, count);
    }

    [TestMethod]
    public void SetGridCellHasTheDocumentedSchemaAndIsAMutation()
    {
        MethodInfo method = ToolMethod("set_grid_cell");
        ParameterInfo[] parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();

        CollectionAssert.AreEqual(
            new[] { "generation", "id", "row", "column", "text", "viewToken", "expectedText" },
            parameters.Select(p => p.Name).ToArray());
        Assert.AreEqual(typeof(int), parameters.Single(p => p.Name == "row").ParameterType);
        Assert.AreEqual(typeof(int), parameters.Single(p => p.Name == "column").ParameterType);
        Assert.IsFalse(parameters.Single(p => p.Name == "viewToken").IsOptional, "viewToken is required");
        Assert.IsFalse(parameters.Single(p => p.Name == "text").IsOptional, "text is required");
        Assert.IsTrue(parameters.Single(p => p.Name == "expectedText").IsOptional, "expectedText is optional");

        // A mutation like set_text: the attribute carries no ReadOnly flag.
        Assert.IsFalse(method.GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly);
        Assert.IsFalse(ToolMethod("set_text").GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly);
    }

    [TestMethod]
    public void SetGridCellDescribesTheEditContract()
    {
        string description = ToolDescription("set_grid_cell");

        StringAssert.Contains(description, "committed");
        StringAssert.Contains(description, "edit_rejected");
        StringAssert.Contains(description, "reason");
        StringAssert.Contains(description, "errorText");
        StringAssert.Contains(description, "displayText");
        StringAssert.Contains(description, "read_grid");
        StringAssert.Contains(description, "stale_view");
        StringAssert.Contains(description, "modal");
        StringAssert.Contains(description, "started");
        StringAssert.Contains(description, "action_status");
        StringAssert.Contains(description, "grid_busy");
        StringAssert.Contains(description, "expectedText");
    }

    [TestMethod]
    public void GridToolsDescribeAdaptersAndViewTokens()
    {
        string readGrid = ToolDescription("read_grid");
        StringAssert.Contains(readGrid, "application grid adapter");
        StringAssert.Contains(readGrid, "sorted/filtered");
        StringAssert.Contains(readGrid, "viewToken");
        StringAssert.Contains(readGrid, "editable");
        StringAssert.Contains(readGrid, "valueKind");
        StringAssert.Contains(readGrid, "bandPath");
        StringAssert.Contains(readGrid, "choices");

        foreach (string tool in new[] { "select_control", "scroll_control" })
        {
            string both = ToolDescription(tool) + " " + ParameterDescription(tool, "viewToken");
            StringAssert.Contains(both, "viewToken");
            StringAssert.Contains(both, "read_grid");
            StringAssert.Contains(both, "stale_view");
        }
        StringAssert.Contains(ToolDescription("select_control") + ParameterDescription("select_control", "viewToken"), "required");

        string tree = ToolDescription("control_tree");
        StringAssert.Contains(tree, "gridAdapter");
        StringAssert.Contains(tree, "gridError: adapter_failed");
        StringAssert.Contains(tree, "textRedacted");
        foreach (string capability in new[] { "read_grid", "select", "scroll", "set_cell" })
            StringAssert.Contains(tree, capability);
    }

    [TestMethod]
    public async Task ViewTokenIsPassedThroughForSelectAndScroll()
    {
        await using var bridge = new FakeBridge(new[] { "select", "scroll", "set_cell" });
        var tools = bridge.Tools();

        await tools.Select("g1", "c1", CancellationToken.None, row: 3, column: 1, viewToken: "salt.7");
        await tools.Scroll("g1", "c1", CancellationToken.None, row: 3, viewToken: "salt.8");
        await tools.Select("g1", "c1", CancellationToken.None, row: 4);
        await tools.Scroll("g1", "c1", CancellationToken.None, row: 4);

        Assert.AreEqual(4, bridge.Requests.Count);
        Assert.AreEqual("select", bridge.Requests[0].Method);
        Assert.AreEqual("salt.7", (string?)bridge.Requests[0].Parameters["viewToken"]);
        Assert.AreEqual(3, (int?)bridge.Requests[0].Parameters["row"]);
        Assert.AreEqual("scroll", bridge.Requests[1].Method);
        Assert.AreEqual("salt.8", (string?)bridge.Requests[1].Parameters["viewToken"]);
        Assert.IsNull(bridge.Requests[2].Parameters["viewToken"], "an omitted token is not sent");
        Assert.IsNull(bridge.Requests[3].Parameters["viewToken"], "an omitted token is not sent");
    }

    [TestMethod]
    public async Task SetGridCellSendsTheBridgeSetCellRequest()
    {
        await using var bridge = new FakeBridge(new[] { "select", "set_cell" });
        bridge.Respond = _ => new JObject { ["actionId"] = "a1", ["state"] = "started" };

        CallToolResult result = await bridge.Tools().SetGridCell("g1", "c9", 2, 5, "12.5", "salt.3", CancellationToken.None, "10");

        Assert.AreNotEqual(true, result.IsError);
        RpcRequest request = bridge.Requests.Single();
        Assert.AreEqual("set_cell", request.Method);
        Assert.AreEqual("g1", (string?)request.Parameters["target"]!["generation"]);
        Assert.AreEqual("c9", (string?)request.Parameters["target"]!["id"]);
        Assert.AreEqual(2, (int?)request.Parameters["row"]);
        Assert.AreEqual(5, (int?)request.Parameters["column"]);
        Assert.AreEqual("12.5", (string?)request.Parameters["text"]);
        Assert.AreEqual("salt.3", (string?)request.Parameters["viewToken"]);
        Assert.AreEqual("10", (string?)request.Parameters["expectedText"]);
        Assert.AreEqual("started", (string?)Payload(result)["result"]!["state"]);

        await bridge.Tools().SetGridCell("g1", "c9", 2, 5, "x", "salt.3", CancellationToken.None);
        Assert.IsNull(bridge.Requests[1].Parameters["expectedText"], "an omitted expectedText is not sent");
    }

    [TestMethod]
    public async Task SetGridCellIsUnsupportedWhenAnOlderBridgeLacksSetCell()
    {
        await using var bridge = new FakeBridge(new[] { "select", "set_text", "read_grid" });

        CallToolResult result = await bridge.Tools().SetGridCell("g1", "c9", 0, 0, "x", "salt.1", CancellationToken.None);

        Assert.AreEqual(true, result.IsError);
        JObject error = (JObject)Payload(result)["error"]!;
        Assert.AreEqual("unsupported_operation", (string?)error["code"]);
        Assert.IsNotNull(error["message"]);
        Assert.AreEqual(0, bridge.Requests.Count, "nothing may be sent to the bridge");
    }

    [TestMethod]
    public async Task ActionStatusSurfacesTheFailedReceiptErrorDetailsUnchanged()
    {
        var receipt = new JObject
        {
            ["actionId"] = "a1",
            ["state"] = "failed",
            ["error"] = new JObject
            {
                ["code"] = "edit_rejected",
                ["message"] = "The grid refused the edit.",
                ["details"] = new JObject { ["reason"] = "validation_failed", ["errorText"] = "Value must be positive.", ["displayText"] = "7" }
            }
        };
        await using var bridge = new FakeBridge(new[] { "set_cell" });
        bridge.Respond = _ => receipt.DeepClone();

        CallToolResult result = await bridge.Tools().ActionStatus("g1", "a1", CancellationToken.None);

        Assert.AreNotEqual(true, result.IsError, "a failed receipt is a successful status read");
        Assert.IsTrue(JToken.DeepEquals(receipt, Payload(result)["result"]), Payload(result).ToString());
    }

    private static JObject Payload(CallToolResult result) =>
        JObject.Parse(((TextContentBlock)result.Content.Single()).Text);

    /// <summary>A named-pipe bridge that answers hello with a configurable method list and records every other request.</summary>
    private sealed class FakeBridge : IAsyncDisposable
    {
        private readonly string[] _methods;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly ServerOptions _options;

        public FakeBridge(string[] methods)
        {
            _methods = methods;
            _options = ServerOptions.Parse(new[] { "--app", @"C:\a.exe", "--pipe", "wfmi-test-" + Guid.NewGuid().ToString("N") });
            _loop = Task.Run(Serve);
        }

        public List<RpcRequest> Requests { get; } = new();
        public Func<RpcRequest, JToken> Respond { get; set; } = _ => new JObject();

        public WinFormsTools Tools() => new(new ApplicationSession(_options));

        private readonly List<Task> _handlers = new();

        // The next listening instance is created before the current connection is served. A client that connects
        // between two calls (the sidecar opens a connection per call, and one extra hello connection for the
        // set_grid_cell capability gate) then always finds a listener; on Linux a named pipe is a unix socket and a
        // client that arrives while no instance exists is refused.
        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut,
                        -1 /* unlimited instances */, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    NamedPipeServerStream connected = pipe;
                    pipe = null;
                    lock (_handlers) _handlers.Add(Task.Run(() => Handle(connected)));
                }
                catch (OperationCanceledException) { }
                catch (System.IO.IOException) { }
                finally
                {
                    if (pipe != null) await pipe.DisposeAsync();
                }
            }
        }

        private async Task Handle(NamedPipeServerStream pipe)
        {
            try
            {
                RpcRequest hello = (await FrameCodec.ReadAsync<RpcRequest>(pipe, _stop.Token))!;
                await FrameCodec.WriteAsync(pipe, new RpcResponse
                {
                    Id = hello.Id,
                    Generation = "g1",
                    Result = new JObject { ["methods"] = new JArray(_methods) }
                }, _stop.Token);
                RpcRequest? request = await FrameCodec.ReadAsync<RpcRequest>(pipe, _stop.Token);
                if (request == null) return;
                lock (Requests) Requests.Add(request);
                await FrameCodec.WriteAsync(pipe, new RpcResponse { Id = request.Id, Generation = "g1", Result = Respond(request) }, _stop.Token);
                // Let the client read the response before the pipe is disposed.
                await FrameCodec.ReadAsync<RpcRequest>(pipe, _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (System.IO.IOException) { }
            finally
            {
                await pipe.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            Task[] handlers;
            lock (_handlers) handlers = _handlers.ToArray();
            try { await Task.WhenAll(handlers); } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    [TestMethod]
    public void InvokeControlDescribesElementActions()
    {
        string description = ToolDescription("invoke_control");
        string action = ParameterDescription("invoke_control", "action");
        string both = description + " " + action;

        StringAssert.Contains(both, "element");
        StringAssert.Contains(action, "open");
        StringAssert.Contains(action, "close");
        StringAssert.Contains(both, "observed");
        StringAssert.Contains(both, "opened");
        StringAssert.Contains(both, "best-effort");
        StringAssert.Contains(both, "active window");
        StringAssert.Contains(both, "semantic click");
        StringAssert.Contains(both, "closed menu");
        StringAssert.Contains(both, "SourceControl");
        StringAssert.Contains(both, "Opening");
        StringAssert.Contains(both, "not_realized");
    }

    [TestMethod]
    public void ControlTreeDescribesItemsAndCollapsedDropdowns()
    {
        string description = ToolDescription("control_tree");

        StringAssert.Contains(description, "kind: \"item\"");
        StringAssert.Contains(description, "childrenOmitted");
        StringAssert.Contains(description, "closed dropdown");
        StringAssert.Contains(description, "rooted at the item");
        StringAssert.Contains(description, "ContextMenuStrip");
        StringAssert.Contains(description, "not discovered");
    }

    [TestMethod]
    public void InspectControlMentionsElementIds()
    {
        string description = ToolDescription("inspect_control");
        StringAssert.Contains(description, "element");
    }
}
