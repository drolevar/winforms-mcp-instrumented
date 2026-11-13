using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented.Server;

internal sealed class BridgeClient(ServerOptions options)
{
    public async Task<RpcResponse> CallAsync(string method, JObject? parameters, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        bool mayHaveBeenDelivered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var pipe = new NamedPipeClientStream(".", options.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(1500, deadline.Token).ConfigureAwait(false);
            // Every short-lived connection negotiates first, even after the app restarts.
            var handshake = new RpcRequest
            {
                Id = method == "hello" ? id : Guid.NewGuid().ToString("N"),
                ProtocolVersion = BridgeProtocol.Version,
                Token = options.Token,
                Method = "hello",
                Parameters = new JObject()
            };
            await FrameCodec.WriteAsync(pipe, handshake, deadline.Token).ConfigureAwait(false);
            RpcResponse hello = await ReadExpectedAsync(pipe, handshake.Id, deadline.Token).ConfigureAwait(false);
            if (hello.Error != null || method == "hello") return hello;
            var request = new RpcRequest
            {
                Id = id,
                ProtocolVersion = BridgeProtocol.Version,
                Token = options.Token,
                Method = method,
                Parameters = parameters ?? new JObject()
            };
            // A failed write may still have reached the peer. Never automatically replay a mutation.
            mayHaveBeenDelivered = true;
            await FrameCodec.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            return await ReadExpectedAsync(pipe, id, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(id, "request_timeout", "The bridge did not respond before the request deadline.", mayHaveBeenDelivered);
        }
        catch (TimeoutException)
        {
            return Failure(id, "app_unavailable", "No bridge accepted the connection. Launch an instrumented application or check its activation settings.", mayHaveBeenDelivered);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(id, "access_denied", "The current process cannot access the application's named pipe.", mayHaveBeenDelivered);
        }
        catch (IOException exception)
        {
            return Failure(id, "bridge_io_error", exception.Message, mayHaveBeenDelivered);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Failure(id, "app_unavailable", "The local pipe transport is unavailable on this platform or in this execution environment.", mayHaveBeenDelivered);
        }
    }

    private static async Task<RpcResponse> ReadExpectedAsync(Stream pipe, string id, CancellationToken cancellationToken)
    {
        RpcResponse response = await FrameCodec.ReadAsync<RpcResponse>(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The bridge closed the pipe before returning a response.");
        if (response.Id != id || response.ProtocolVersion != BridgeProtocol.Version)
            throw new InvalidDataException("The bridge response has an unexpected request ID or protocol version.");
        return response;
    }

    private static RpcResponse Failure(string id, string code, string message, bool mayHaveBeenDelivered) => new()
    {
        Id = id,
        Error = new RpcError
        {
            Code = code,
            Message = message,
            Details = new JObject
            {
                ["delivery"] = mayHaveBeenDelivered ? "unknown" : "not_sent",
                ["automaticRetry"] = false
            }
        }
    };
}
