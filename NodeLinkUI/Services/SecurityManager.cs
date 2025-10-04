using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NodeComm;   // CommChannel
using NodeCore;   // NodeLinkSettings

namespace NodeLinkUI.Services
{
    public static class SecurityManager
    {
        public static void EnsureJoinCode(NodeLinkSettings settings, bool isMaster, Action<string>? log = null)
        {
            if (!string.IsNullOrWhiteSpace(settings.JoinCode)) return;
            if (!isMaster) return; // agents won’t auto-generate

            settings.JoinCode = GenerateJoinCode();
            try { settings.Save(); } catch { }
            log?.Invoke($"Generated Join Code: {settings.JoinCode}");
        }

        public static bool TrySetJoinCode(NodeLinkSettings settings, string? code, Action<string>? log = null)
        {
            var norm = NormalizeCode(code);
            if (string.IsNullOrEmpty(norm)) return false;
            settings.JoinCode = Group(norm, 4, '-').ToUpperInvariant();
            try { settings.Save(); } catch { }
            log?.Invoke("Join Code updated.");
            return true;
        }

        public static string RotateJoinCode(NodeLinkSettings settings, Action<string>? log = null)
        {
            settings.JoinCode = GenerateJoinCode();
            try { settings.Save(); } catch { }
            log?.Invoke("Join Code rotated.");
            return settings.JoinCode;
        }

        public static void ApplyToComm(CommChannel comm, NodeLinkSettings settings)
        {
            // Compiles even if your CommChannel doesn’t have HMAC yet.
            comm.SetSharedSecret(settings.JoinCode ?? "");
        }

        // ---------- helpers ----------

        public static string GenerateJoinCode(int bytes = 16)
        {
            var raw = RandomNumberGenerator.GetBytes(bytes); // 128-bit
            var b32 = Base32Crockford(raw);
            return Group(b32, 4, '-').ToUpperInvariant();    // e.g. 8J4M-K0RW-3YV9-P5DZ
        }

        public static string NormalizeCode(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var s = new string(input.Where(char.IsLetterOrDigit).ToArray());
            return s.ToUpperInvariant();
        }

        private static string Group(string s, int block, char sep)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && i % block == 0) sb.Append(sep);
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Base32Crockford(ReadOnlySpan<byte> data)
        {
            const string alpha = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I,L,O,U
            int outputLen = (int)Math.Ceiling(data.Length / 5d) * 8;
            var sb = new StringBuilder(outputLen);
            int buffer = 0, bitsLeft = 0;

            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;
                while (bitsLeft >= 5)
                {
                    int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                    bitsLeft -= 5;
                    sb.Append(alpha[index]);
                }
            }
            if (bitsLeft > 0)
            {
                int index = (buffer << (5 - bitsLeft)) & 0x1F;
                sb.Append(alpha[index]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// No-op extension so this compiles whether or not you’ve swapped in the HMAC-enabled CommChannel.
    /// Once you replace CommChannel with the secure version, this extension is ignored at compile.
    /// </summary>
    public static class CommAuthExtensions
    {
        public static void SetSharedSecret(this CommChannel _, string __) { /* no-op for now */ }
    }
}

