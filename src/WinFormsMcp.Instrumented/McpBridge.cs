using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented;

/// <summary>Starts optional instrumentation on the application's owning STA thread.</summary>
public static class McpBridge
{
    /// <summary>
    /// Starts the bridge when WINFORMS_MCP_PIPE and WINFORMS_MCP_TOKEN are configured.
    /// With no pipe configured, returns an inert disposable without creating UI or I/O objects.
    /// Call before Application.Run, then dispose after that message loop returns.
    /// </summary>
    public static IDisposable Start(Action<McpBridgeBuilder>? configure = null)
    {
        string? pipeName = Environment.GetEnvironmentVariable(BridgeProtocol.EnvironmentPipeName);
        if (string.IsNullOrWhiteSpace(pipeName)) return InactiveBridge.Instance;
        string? token = Environment.GetEnvironmentVariable(BridgeProtocol.EnvironmentToken);
        if (string.IsNullOrWhiteSpace(token) || token!.Length < 32)
            throw new InvalidOperationException("WINFORMS_MCP_TOKEN must contain at least 32 characters when instrumentation is activated.");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Start instrumentation on the WinForms STA thread before Application.Run.");
        if (pipeName!.Length > 128)
            throw new ArgumentException("The pipe name must contain at most 128 characters.");
        foreach (char character in pipeName)
            if (!char.IsLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
                throw new ArgumentException("Use letters, digits, dots, underscores, or hyphens in the pipe name.");

        var options = new McpBridgeBuilder();
        configure?.Invoke(options);
        options.Validate();
        return new BridgeHost(pipeName, token, options);
    }

    private sealed class InactiveBridge : IDisposable
    {
        internal static readonly InactiveBridge Instance = new();
        public void Dispose() { }
    }
}

internal sealed class BridgeHost : IDisposable
{
    private readonly string pipeName;
    private readonly string token;
    private readonly McpBridgeBuilder options;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object pipeLock = new();
    private readonly object diagnosticsLock = new();
    private readonly Queue<JObject> diagnostics = new();
    private readonly UiRuntime ui;
    private readonly Task worker;
    private NamedPipeServerStream? currentPipe;
    private int disposed;

    internal BridgeHost(string pipeName, string token, McpBridgeBuilder options)
    {
        this.pipeName = pipeName;
        this.token = token;
        this.options = options;
        Generation = Guid.NewGuid().ToString("N");
        ui = new UiRuntime(Generation, options, RecordDiagnostic);
        // Create the first endpoint synchronously so activation failures are visible at startup.
        try { currentPipe = CreatePipe(); }
        catch { ui.Dispose(); lifetime.Dispose(); throw; }
        worker = Task.Run(AcceptLoopAsync);
        Application.ApplicationExit += OnApplicationExit;
    }

    internal string Generation { get; }

    private NamedPipeServerStream CreatePipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? user = identity.User;
        if (user == null) throw new InvalidOperationException("The current Windows account has no security identifier.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65_536, 65_536, security);
    }

    private async Task AcceptLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe;
            lock (pipeLock) pipe = currentPipe;
            if (pipe == null) return;
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                bool greeted = false;
                while (!lifetime.IsCancellationRequested && pipe.IsConnected)
                {
                    RpcRequest? request = await WithFrameDeadlineAsync(pipe, "request",
                        cancellation => FrameCodec.ReadAsync<RpcRequest>(pipe, cancellation)).ConfigureAwait(false);
                    if (request == null) break;
                    var response = new RpcResponse { Id = request.Id, Generation = Generation };
                    bool close = false;
                    try
                    {
                        if (!TokensEqual(token, request.Token))
                        {
                            close = true;
                            throw new BridgeFailure("unauthorized", "The configured bridge token was not accepted.");
                        }
                        if (request.ProtocolVersion != BridgeProtocol.Version)
                        {
                            close = true;
                            throw new BridgeFailure("protocol_mismatch", "The bridge protocol version is incompatible.");
                        }
                        if (string.IsNullOrWhiteSpace(request.Id))
                            throw new BridgeFailure("invalid_request", "A request ID is required.");
                        if (!greeted && request.Method != "hello")
                            throw new BridgeFailure("handshake_required", "Send hello before application operations.");
                        response.Result = await ExecuteAsync(request).ConfigureAwait(false);
                        if (request.Method == "hello") greeted = true;
                    }
                    catch (Exception exception) when (!(exception is OperationCanceledException))
                    {
                        response.Error = BridgeFailure.Describe(exception);
                    }
                    await WithFrameDeadlineAsync(pipe, "response", async cancellation =>
                    {
                        await FrameCodec.WriteAsync(pipe, response, cancellation).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    if (close) break;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
            catch (TimeoutException exception) { RecordDiagnostic("transport_timeout", exception.Message); }
            catch (IOException exception) { RecordDiagnostic("transport", exception.Message); }
            catch (JsonException exception) { RecordDiagnostic("invalid_frame", exception.Message); }
            catch (Exception exception) { RecordDiagnostic("connection", exception.Message); }
            finally
            {
                pipe.Dispose();
                lock (pipeLock)
                {
                    if (ReferenceEquals(currentPipe, pipe)) currentPipe = null;
                }
            }

            if (lifetime.IsCancellationRequested) return;
            try
            {
                lock (pipeLock)
                {
                    if (lifetime.IsCancellationRequested) return;
                    currentPipe = CreatePipe();
                }
            }
            catch (Exception exception)
            {
                RecordDiagnostic("listener_failed", exception.Message);
                return;
            }
        }
    }

    private async Task<T> WithFrameDeadlineAsync<T>(NamedPipeServerStream pipe, string direction,
        Func<CancellationToken, Task<T>> operation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // Disposal also unblocks runtimes where cancelling an outstanding pipe read is insufficient.
        using var closeOnTimeout = deadline.Token.Register(pipe.Dispose);
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        catch (Exception exception) when (deadline.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The peer did not complete a " + direction + " frame within 30 seconds; the connection was closed.", exception);
        }
    }

    private Task<JToken> ExecuteAsync(RpcRequest request)
    {
        if (request.Parameters == null) throw new BridgeFailure("invalid_request", "Parameters must be an object.");
        if (request.Method == "hello")
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return Task.FromResult<JToken>(new JObject
            {
                ["name"] = "WinForms MCP Instrumented",
                ["generation"] = Generation,
                ["protocolVersion"] = BridgeProtocol.Version,
                ["processId"] = process.Id,
                ["bridgeVersion"] = typeof(McpBridge).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                ["uiThreadCoverage"] = "registered-thread-only",
                ["captureMode"] = "screen_copy",
                ["methods"] = new JArray(UiRuntime.Methods),
                ["elementAdapters"] = new JArray(ui.AdapterNames),
                ["limits"] = new JObject
                {
                    ["maxFrameBytes"] = BridgeProtocol.MaxFrameBytes,
                    ["maxNodes"] = options.MaxNodes,
                    ["maxDepth"] = options.MaxDepth,
                    ["maxTextLength"] = options.MaxTextLength,
                    ["uiRequestTimeoutMilliseconds"] = (int)options.UiRequestTimeout.TotalMilliseconds,
                    ["maxImageBytes"] = options.MaxImageBytes,
                    ["maxCapturePixels"] = options.MaxCapturePixels,
                    ["maxPendingActions"] = 32,
                    ["maxRetainedActions"] = 256,
                    ["frameTimeoutSeconds"] = 30
                }
            });
        }
        if (request.Method == "diagnostics")
        {
            lock (diagnosticsLock)
                return Task.FromResult<JToken>(new JObject
                {
                    ["generation"] = Generation,
                    ["scope"] = "bridge-only",
                    ["entries"] = new JArray(diagnostics)
                });
        }
        return ui.ExecuteAsync(request.Method, request.Parameters, lifetime.Token);
    }

    private static bool TokensEqual(string expected, string? actual)
    {
        if (actual == null) return false;
        // Token lengths are public configuration; compare equal-length values without early exit.
        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(actual);
        int different = left.Length ^ right.Length;
        for (int index = 0; index < left.Length; index++)
            different |= left[index] ^ (index < right.Length ? right[index] : 0);
        return different == 0;
    }

    private void RecordDiagnostic(string category, string message)
    {
        message = message.Replace(token, "[redacted bridge token]");
        lock (diagnosticsLock)
        {
            while (diagnostics.Count >= 100) diagnostics.Dequeue();
            diagnostics.Enqueue(new JObject
            {
                ["timeUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["category"] = category,
                ["message"] = message.Length <= 4_096 ? message : message.Substring(0, 4_096)
            });
        }
    }

    private void OnApplicationExit(object? sender, EventArgs eventArgs) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Application.ApplicationExit -= OnApplicationExit;
        lifetime.Cancel();
        lock (pipeLock)
        {
            currentPipe?.Dispose();
            currentPipe = null;
        }
        ui.Dispose();
        // Do not wait synchronously on a worker that may be awaiting the UI thread.
        _ = worker.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
