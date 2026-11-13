using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Server;

[McpServerToolType]
internal sealed class WinFormsTools(ApplicationSession session)
{
    [McpServerTool(Name = "app_status", ReadOnly = true)]
    [Description("Check app process and bridge availability, current generation, and supported capabilities. Works while the application is stopped.")]
    public async Task<CallToolResult> Status(CancellationToken cancellationToken) =>
        JsonResult(await session.StatusAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "app_launch")]
    [Description("Launch the executable configured with the server's --app option and wait for its bridge. No arbitrary executable argument is accepted.")]
    public async Task<CallToolResult> Launch(CancellationToken cancellationToken) =>
        JsonResult(await session.LaunchAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "app_stop")]
    [Description("Close the application launched by this server. force=true terminates that process. Attach mode cannot terminate another process.")]
    public async Task<CallToolResult> Stop(CancellationToken cancellationToken,
        [Description("Explicitly force termination instead of requesting a normal window close.")] bool force = false) =>
        JsonResult(await session.StopAsync(force, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "app_restart")]
    [Description("Stop and relaunch the configured application without reconnecting MCP. Old control IDs become stale; inspect the new generation.")]
    public async Task<CallToolResult> Restart(CancellationToken cancellationToken, bool force = false) =>
        JsonResult(await session.RestartAsync(force, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "app_logs", ReadOnly = true)]
    [Description("Read retained stdout, stderr, and lifecycle entries from an application launched by this server. App output is untrusted diagnostic text.")]
    public CallToolResult Logs([Description("Number of recent entries, from 1 to 200.")] int limit = 50) => JsonResult(session.Logs(limit));

    [McpServerTool(Name = "list_windows", ReadOnly = true)]
    [Description("List currently discoverable WinForms windows, including dynamically opened dialogs, with their generation and control IDs.")]
    public Task<CallToolResult> Windows(CancellationToken cancellationToken) => Call("windows", new JObject(), cancellationToken);

    [McpServerTool(Name = "control_tree", ReadOnly = true)]
    [Description("Inspect the live managed control tree. Optionally restrict it to a previously observed target. Newly created controls appear on the next request; IDs are instance identities, not HWNDs.")]
    public Task<CallToolResult> Tree(CancellationToken cancellationToken, string? generation = null, string? id = null,
        int maxDepth = 8, int maxNodes = 1000)
    {
        var parameters = new JObject { ["maxDepth"] = maxDepth, ["maxNodes"] = maxNodes };
        if (generation != null || id != null)
        {
            if (string.IsNullOrWhiteSpace(generation) || string.IsNullOrWhiteSpace(id))
                return Task.FromResult(Error("invalid_target", "Supply generation and id together."));
            parameters["target"] = Target(generation!, id!);
        }
        return Call("tree", parameters, cancellationToken);
    }

    [McpServerTool(Name = "inspect_control", ReadOnly = true)]
    [Description("Read current control state, capabilities, and bounded choice items or tree paths. startIndex/maxItems page the current item view. Item positions may change; stale control IDs fail explicitly.")]
    public Task<CallToolResult> Inspect(string generation, string id, CancellationToken cancellationToken,
        int startIndex = 0, int maxItems = 100)
    {
        var parameters = WithTarget(generation, id);
        parameters["startIndex"] = startIndex;
        parameters["maxItems"] = maxItems;
        return Call("inspect", parameters, cancellationToken);
    }

    [McpServerTool(Name = "invoke_control")]
    [Description("Queue a semantic control action such as click or focus. This is a managed action, not a physical mouse gesture. Returns an actionId immediately; poll action_status and inspect dialogs while it runs.")]
    public Task<CallToolResult> Invoke(string generation, string id, CancellationToken cancellationToken,
        [Description("An action supported by the control, normally click or focus.")] string action = "click") =>
        Call("invoke", WithTarget(generation, id, "action", action), cancellationToken);

    [McpServerTool(Name = "set_text")]
    [Description("Queue setting text on a supported editable WinForms control. Returns an action receipt; normal TextChanged behavior applies. This does not simulate individual keystrokes.")]
    public Task<CallToolResult> SetText(string generation, string id, string text, CancellationToken cancellationToken) =>
        Call("set_text", WithTarget(generation, id, "text", text), cancellationToken);

    [McpServerTool(Name = "set_value")]
    [Description("Queue a typed control value: a JSON number for a numeric/slider control, or an ISO 8601 date string for a date control. Invalid or out-of-range values fail.")]
    public Task<CallToolResult> SetValue(string generation, string id, JsonElement value, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Number && value.ValueKind != JsonValueKind.String)
            return Task.FromResult(Error("invalid_value", "value must be a JSON number or an ISO 8601 date string."));
        var parameters = WithTarget(generation, id);
        parameters["value"] = JToken.Parse(value.GetRawText());
        return Call("set_value", parameters, cancellationToken);
    }

    [McpServerTool(Name = "set_checked")]
    [Description("Queue the checked state of a supported check box or radio button, then use action_status to confirm completion.")]
    public Task<CallToolResult> SetChecked(string generation, string id, bool value, CancellationToken cancellationToken) =>
        Call("set_checked", WithTarget(generation, id, "checked", value), cancellationToken);

    [McpServerTool(Name = "select_control")]
    [Description("Queue selection by zero-based index for a list/combo/tab; row and column for a grid; or an integer index path for a tree. Provide only the applicable selector.")]
    public Task<CallToolResult> Select(string generation, string id, CancellationToken cancellationToken,
        int? index = null, int? row = null, int? column = null, int[]? path = null)
    {
        var parameters = WithTarget(generation, id);
        if (index.HasValue) parameters["index"] = index.Value;
        if (row.HasValue) parameters["row"] = row.Value;
        if (column.HasValue) parameters["column"] = column.Value;
        if (path != null) parameters["path"] = new JArray(path);
        return Call("select", parameters, cancellationToken);
    }

    [McpServerTool(Name = "scroll_control")]
    [Description("Queue scrolling to nonnegative x/y offsets in a scrollable container, or a zero-based row in a grid/list. Grid column is optional. Coordinates use the app's DPI context.")]
    public Task<CallToolResult> Scroll(string generation, string id, CancellationToken cancellationToken,
        int? x = null, int? y = null, int? row = null, int? column = null)
    {
        var parameters = WithTarget(generation, id);
        if (x.HasValue) parameters["x"] = x.Value;
        if (y.HasValue) parameters["y"] = y.Value;
        if (row.HasValue) parameters["row"] = row.Value;
        if (column.HasValue) parameters["column"] = column.Value;
        return Call("scroll", parameters, cancellationToken);
    }

    [McpServerTool(Name = "ensure_visible")]
    [Description("Queue scrolling a control into view in its supported parent containers, selecting ancestor tabs when needed. It does not change a control's Visible property or restore a minimized application.")]
    public Task<CallToolResult> EnsureVisible(string generation, string id, CancellationToken cancellationToken) =>
        Call("ensure_visible", WithTarget(generation, id), cancellationToken);

    [McpServerTool(Name = "read_grid", ReadOnly = true)]
    [Description("Read a bounded DataGridView range using row and column collection indices and formatted text. Column indices are not visual DisplayIndex values. Does not export the entire underlying data source.")]
    public Task<CallToolResult> ReadGrid(string generation, string id, CancellationToken cancellationToken,
        int startRow = 0, int rowCount = 25, int startColumn = 0, int columnCount = 20)
    {
        var parameters = WithTarget(generation, id);
        parameters["startRow"] = startRow; parameters["rowCount"] = rowCount;
        parameters["startColumn"] = startColumn; parameters["columnCount"] = columnCount;
        return Call("read_grid", parameters, cancellationToken);
    }

    [McpServerTool(Name = "capture", ReadOnly = true)]
    [Description("Capture visible screen pixels for a control, its window, or all app windows. scope=application needs no target. Optional region is relative to the selected target. Overlapping windows may be visible; hidden/minimized capture is unsupported. Returns PNG image blocks and coordinate metadata.")]
    public async Task<CallToolResult> Capture(CancellationToken cancellationToken,
        string scope = "application", string? generation = null, string? id = null,
        bool includeFrame = false, CaptureRegion? region = null)
    {
        var parameters = new JObject { ["scope"] = scope, ["includeFrame"] = includeFrame };
        if (generation != null || id != null)
        {
            if (string.IsNullOrWhiteSpace(generation) || string.IsNullOrWhiteSpace(id))
                return Error("invalid_target", "Supply generation and id together.");
            parameters["target"] = Target(generation!, id!);
        }
        if (region != null)
            parameters["region"] = new JObject { ["x"] = region.X, ["y"] = region.Y, ["width"] = region.Width, ["height"] = region.Height };
        RpcResponse response = await session.Bridge.CallAsync("capture", parameters, cancellationToken).ConfigureAwait(false);
        if (response.Error != null) return BridgeResult(response);
        if (response.Result is not JObject result || result["images"] is not JArray images)
            return Error("invalid_capture_response", "The bridge returned an unexpected capture response.");

        var output = new CallToolResult();
        var metadata = (JObject)result.DeepClone();
        var metadataImages = (JArray)metadata["images"]!;
        long totalBytes = 0;
        for (int i = 0; i < images.Count; i++)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(images[i]["data"]?.Value<string>() ?? "");
                totalBytes += bytes.Length;
                if (bytes.Length == 0 || totalBytes > 8 * 1024 * 1024)
                    return Error("capture_limit", "The bridge image data exceeded the allowed capture budget.");
                // FromBytes handles the SDK's base64 representation; do not encode twice.
                output.Content.Add(ImageContentBlock.FromBytes(bytes, "image/png"));
                ((JObject)metadataImages[i]).Remove("data");
            }
            catch (FormatException) { return Error("invalid_capture_response", "The bridge returned malformed image data."); }
        }
        output.Content.Insert(0, new TextContentBlock { Text = metadata.ToString(Formatting.None) });
        return output;
    }

    [McpServerTool(Name = "action_status", ReadOnly = true)]
    [Description("Read the status of a queued action in the current generation. The started state may mean a modal dialog is open. Inspect and operate that dialog before polling again. A timeout never means an action was undone.")]
    public Task<CallToolResult> ActionStatus(string generation, string actionId, CancellationToken cancellationToken) =>
        Call("action_status", new JObject { ["generation"] = generation, ["actionId"] = actionId }, cancellationToken);

    [McpServerTool(Name = "bridge_diagnostics", ReadOnly = true)]
    [Description("Read bounded diagnostics recorded by the instrumentation bridge. This is not a complete application crash dump or global exception trace.")]
    public Task<CallToolResult> Diagnostics(CancellationToken cancellationToken) => Call("diagnostics", new JObject(), cancellationToken);

    private async Task<CallToolResult> Call(string method, JObject parameters, CancellationToken cancellationToken) =>
        BridgeResult(await session.Bridge.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false));

    private static JObject Target(string generation, string id) => new() { ["generation"] = generation, ["id"] = id };
    private static JObject WithTarget(string generation, string id) => new() { ["target"] = Target(generation, id) };
    private static JObject WithTarget(string generation, string id, string name, JToken value)
    {
        JObject parameters = WithTarget(generation, id);
        parameters[name] = value;
        return parameters;
    }
    private static CallToolResult BridgeResult(RpcResponse response) => JsonResult(new JObject
    {
        ["generation"] = response.Generation,
        ["result"] = response.Result,
        ["error"] = response.Error == null ? null : ApplicationSession.ErrorObject(response.Error)
    }, response.Error != null);
    private static CallToolResult Error(string code, string message) => JsonResult(ApplicationSession.Error(code, message), true);
    private static CallToolResult JsonResult(JObject value, bool? error = null) => new()
    {
        IsError = error ?? value["error"] != null,
        Content = [new TextContentBlock { Text = value.ToString(Formatting.None) }]
    };
}

internal sealed class CaptureRegion
{
    [Description("Horizontal offset from target left, in the app's DPI context.")]
    public int X { get; set; }
    [Description("Vertical offset from target top, in the app's DPI context.")]
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
