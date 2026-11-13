using System;
using Newtonsoft.Json.Linq;
using WinFormsMcp.Instrumented.Protocol;

namespace WinFormsMcp.Instrumented;

internal sealed class BridgeFailure : Exception
{
    internal BridgeFailure(string code, string message, JToken? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }

    internal string Code { get; }
    internal JToken? Details { get; }

    internal RpcError ToError() => new() { Code = Code, Message = Message, Details = Details };

    internal static RpcError Describe(Exception exception)
        => exception is BridgeFailure failure
            ? failure.ToError()
            : new RpcError { Code = "operation_failed", Message = exception.Message };
}
