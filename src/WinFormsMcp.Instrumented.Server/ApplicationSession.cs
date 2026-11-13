using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Server;

internal sealed class ApplicationSession : IDisposable
{
    private readonly ServerOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentQueue<JObject> _logs = new();
    private readonly object _processLock = new();
    private Process? _process;

    public ApplicationSession(ServerOptions options)
    {
        _options = options;
        Bridge = new BridgeClient(options);
    }

    public BridgeClient Bridge { get; }

    public async Task<JObject> StatusAsync(CancellationToken cancellationToken)
    {
        JObject status;
        lock (_processLock)
        {
            status = new JObject
            {
                ["mode"] = _options.ApplicationPath == null ? "attach" : "launch",
                ["applicationPath"] = _options.ApplicationPath,
                ["ownsProcess"] = _process != null,
                ["running"] = _process == null ? JValue.CreateNull() : new JValue(!_process.HasExited),
                ["processId"] = _process == null ? JValue.CreateNull() : new JValue(_process.Id),
                ["exitCode"] = _process != null && _process.HasExited ? new JValue(_process.ExitCode) : JValue.CreateNull()
            };
        }
        RpcResponse hello = await Bridge.CallAsync("hello", null, cancellationToken).ConfigureAwait(false);
        status["bridgeAvailable"] = hello.Error == null;
        status["generation"] = hello.Generation;
        status["bridge"] = hello.Error == null ? hello.Result : JObject.FromObject(hello.Error);
        return status;
    }

    public async Task<JObject> LaunchAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LaunchCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async Task<JObject> StopAsync(bool force, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StopCoreAsync(force, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async Task<JObject> RestartAsync(bool force, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JObject stopped = await StopCoreAsync(force, cancellationToken).ConfigureAwait(false);
            if (stopped["error"] != null) return stopped;
            return await LaunchCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public JObject Logs(int limit)
    {
        if (limit < 1 || limit > 200) return Error("invalid_argument", "limit must be between 1 and 200.");
        JObject[] entries = _logs.ToArray();
        return new JObject
        {
            ["retainedLimit"] = 200,
            ["entries"] = new JArray(entries.Skip(Math.Max(0, entries.Length - limit)).Select(x => x.DeepClone()))
        };
    }

    private async Task<JObject> LaunchCoreAsync(CancellationToken cancellationToken)
    {
        if (_options.ApplicationPath == null) return Error("not_configured", "Launch requires the server's --app option.");
        if (!OperatingSystem.IsWindows()) return Error("platform_not_supported", "Launching a .NET Framework WinForms application requires Windows.");
        if (!File.Exists(_options.ApplicationPath)) return Error("application_not_found", "The configured application executable does not exist. Build it first.");
        lock (_processLock)
        {
            if (_process != null && !_process.HasExited)
                return Error("already_running", "The application owned by this server is already running. Use app_status or app_restart.");
            _process?.Dispose();
            _process = null;
            var start = new ProcessStartInfo(_options.ApplicationPath)
            {
                WorkingDirectory = _options.WorkingDirectory ?? Path.GetDirectoryName(_options.ApplicationPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in _options.ApplicationArguments) start.ArgumentList.Add(argument);
            start.Environment[BridgeProtocol.EnvironmentPipeName] = _options.PipeName;
            start.Environment[BridgeProtocol.EnvironmentToken] = _options.Token;
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => Record("stdout", args.Data);
            process.ErrorDataReceived += (_, args) => Record("stderr", args.Data);
            process.Exited += (_, _) => Record("lifecycle", "The application process exited.");
            try
            {
                if (!process.Start()) { process.Dispose(); return Error("launch_failed", "The application process could not be started."); }
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                Record("lifecycle", "Started application process " + process.Id + ".");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
            {
                process.Dispose();
                _process = null;
                return Error("launch_failed", exception.Message);
            }
        }

        // Readiness probes are read-only; mutations are never replayed.
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            RpcResponse hello = await Bridge.CallAsync("hello", null, cancellationToken).ConfigureAwait(false);
            if (hello.Error == null)
                return new JObject { ["launched"] = true, ["generation"] = hello.Generation, ["bridge"] = hello.Result };
            lock (_processLock)
            {
                if (_process == null || _process.HasExited)
                    return Error("application_exited", "The application exited before its bridge became available. Inspect app_logs.");
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return Error("bridge_not_ready", "The application is running, but its bridge is not available. Check that this is an instrumented build; inspect app_logs.");
    }

    private async Task<JObject> StopCoreAsync(bool force, CancellationToken cancellationToken)
    {
        if (_options.ApplicationPath == null) return Error("not_owned", "Attach mode does not terminate an externally owned application.");
        Process? process;
        lock (_processLock) process = _process;
        if (process == null || process.HasExited) return new JObject { ["stopped"] = true, ["alreadyExited"] = true };
        try
        {
            if (force) process.Kill();
            else if (!process.CloseMainWindow())
                return Error("close_not_accepted", "The app has no closeable main window. Use UI tools to resolve any dialog, or explicitly request force=true.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Error("close_pending", "The app did not exit. It may have a confirmation dialog. Inspect the UI or explicitly request force=true.");
            }
            return new JObject { ["stopped"] = true, ["exitCode"] = process.ExitCode, ["forced"] = force };
        }
        catch (InvalidOperationException)
        {
            return new JObject { ["stopped"] = true, ["alreadyExited"] = true };
        }
        catch (System.ComponentModel.Win32Exception exception) { return Error("stop_failed", exception.Message); }
    }

    private void Record(string source, string? text)
    {
        if (text == null) return;
        text = text.Replace(_options.Token, "[redacted bridge token]", StringComparison.Ordinal);
        if (text.Length > 4096) text = text.Substring(0, 4096) + " [truncated]";
        // Application output is untrusted diagnostic data, never a command or protocol input.
        _logs.Enqueue(new JObject { ["timeUtc"] = DateTime.UtcNow.ToString("O"), ["source"] = source, ["text"] = text });
        while (_logs.Count > 200) _logs.TryDequeue(out _);
    }

    internal static JObject Error(string code, string message) => new()
    {
        ["error"] = new JObject { ["code"] = code, ["message"] = message }
    };

    public void Dispose()
    {
        // Disconnecting an MCP client must not silently kill a user's application.
        lock (_processLock) { _process?.Dispose(); _process = null; }
        _lifecycle.Dispose();
    }
}
