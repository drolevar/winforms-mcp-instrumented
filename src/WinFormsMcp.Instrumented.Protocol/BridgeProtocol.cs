namespace WinFormsMcp.Instrumented.Protocol
{
    /// <summary>Constants shared by the app bridge and its external sidecar.</summary>
    public static class BridgeProtocol
    {
        public const int Version = 1;
        public const int MaxFrameBytes = 16 * 1024 * 1024;
        public const int MaxJsonDepth = 64;
        public const string EnvironmentPipeName = "WINFORMS_MCP_PIPE";
        public const string EnvironmentToken = "WINFORMS_MCP_TOKEN";
    }
}
