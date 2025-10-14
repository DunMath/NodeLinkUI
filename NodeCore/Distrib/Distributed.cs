// NodeCore/Distrib/Distributed.cs
using System;

namespace NodeCore.Distrib
{
    // One “kernel” contract apps implement (pure compute, no UI).
    public interface IDistributedApp
    {
        string Id { get; }
        int PreferredChunkSize { get; }                 // optional hint
        byte[] PartitionJob(byte[] input, int partIndex, int partCount);
        byte[] ExecutePart(byte[] payload);             // runs on Agent
        byte[] MergeResults(System.Collections.Generic.IReadOnlyList<byte[]> partResults);
    }

    // Protocol helpers (shared, platform-neutral)
    public static class Wire
    {
        // Master→Agent header
        // AppRun:{AppId}|{CorrId}|{PartIndex}/{PartCount}|{AgentId}\n{base64-payload}
        public static string BuildAppRun(string appId, string corrId, int idx, int count, string agentId, byte[] payload) =>
            $"AppRun:{appId}|{corrId}|{idx}/{count}|{agentId}\n{Convert.ToBase64String(payload)}";

        // Agent→Master header
        // AppResult:{AppId}|{CorrId}|{PartIndex}/{PartCount}|OK|{ms}\n{base64}
        public static string BuildAppResultOk(string appId, string corrId, int idx, int count, int ms, byte[] data) =>
            $"AppResult:{appId}|{corrId}|{idx}/{count}|OK|{ms}\n{Convert.ToBase64String(data)}";

        public static string BuildAppResultFail(string appId, string corrId, int idx, int count, int ms, string error) =>
            $"AppResult:{appId}|{corrId}|{idx}/{count}|FAIL|{ms}\n{error}";
    }
}

}
