using Newtonsoft.Json.Linq;

namespace WinFormsMcp.Instrumented.Protocol
{
    /// <summary>A private bridge request, distinct from the MCP wire protocol.</summary>
    public sealed class RpcRequest
    {
        public string Id { get; set; } = string.Empty;
        public int ProtocolVersion { get; set; } = BridgeProtocol.Version;
        public string Token { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public JObject Parameters { get; set; } = new JObject();
    }

    /// <summary>A correlated bridge response. Exactly one of Result or Error is expected.</summary>
    public sealed class RpcResponse
    {
        public string Id { get; set; } = string.Empty;
        public int ProtocolVersion { get; set; } = BridgeProtocol.Version;
        public string Generation { get; set; } = string.Empty;
        public JToken? Result { get; set; }
        public RpcError? Error { get; set; }
    }

    /// <summary>A bounded, serializable failure without an arbitrary exception graph.</summary>
    public sealed class RpcError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public JToken? Details { get; set; }
    }
}
