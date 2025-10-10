// NodeCore/Protocol/Proto.cs
namespace NodeCore.Protocol
{
    public static class Proto
    {
        public const string Version = "1.0.1";
        public const int DefaultControlPort = 50555;
        public const string MdnsService = "_nodelink._tcp";
        public const int RegisterTimeoutMs = 3000;
        public const int RegisterMaxAttempts = 4;
    }

    public sealed record RegisterRequest(
        string Version,
        string MasterId,
        string MasterIp,
        System.Guid CorrelationId,
        long UnixTs
    );

    public sealed record RegisterAck(
        string Version,
        string AgentId,
        string AgentName,
        string AgentIp,
        System.Guid CorrelationId,
        bool Accepted,
        string? Reason = null
    );

    // WS envelopes we’ll use later for signaling
    public sealed record WsEnvelope(string Type, object Payload);
    public sealed record SdpOffer(string SessionId, string Sdp);
    public sealed record SdpAnswer(string SessionId, string Sdp);
    public sealed record IceCandidate(string SessionId, string Candidate, string SdpMid, int SdpMLineIndex);
}

