// NodeMaster/Services/RegistrationClient.cs
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using NodeCore.Protocol;

namespace MasterApp;

public class RegistrationClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(Proto.RegisterTimeoutMs) };

    public async Task<bool> TryRegisterAsync(
        string agentIp,
        int agentPort,
        string masterId,
        string masterIp,
        string? bearerToken,
        Action<string> log)
    {
        var url = $"http://{agentIp}:{agentPort}/nl/register";
        var corr = Guid.NewGuid();

        for (int attempt = 1; attempt <= Proto.RegisterMaxAttempts; attempt++)
        {
            try
            {
                var req = new RegisterRequest(Proto.Version, masterId, masterIp, corr, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(req) };
                if (!string.IsNullOrEmpty(bearerToken))
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                log($"RegisterRequest -> {agentIp}:{agentPort} (attempt {attempt}/{Proto.RegisterMaxAttempts}, corr {corr})");
                var res = await _http.SendAsync(msg);
                if (!res.IsSuccessStatusCode)
                {
                    log($"Register HTTP {(int)res.StatusCode} {res.ReasonPhrase}");
                    await Task.Delay(250 * attempt);
                    continue;
                }

                var ack = await res.Content.ReadFromJsonAsync<RegisterAck>();
                if (ack is null) { log("RegisterAck parse failed."); continue; }
                if (ack.CorrelationId != corr) { log("CorrelationId mismatch."); continue; }
                if (!ack.Accepted) { log($"Agent rejected: {ack.Reason ?? "(no reason)"}"); return false; }

                log($"Registered {ack.AgentName} ({ack.AgentId}) at {ack.AgentIp} ✅");
                return true;
            }
            catch (TaskCanceledException) { log($"Register timeout after {Proto.RegisterTimeoutMs}ms (attempt {attempt})."); }
            catch (HttpRequestException ex) { log($"Register network error: {ex.Message}"); }
            catch (Exception ex) { log($"Register unexpected error: {ex.Message}"); }

            await Task.Delay(400 * attempt);
        }

        log("Register failed after all retries.");
        return false;
    }
}

