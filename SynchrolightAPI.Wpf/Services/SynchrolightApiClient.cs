using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SynchrolightAPI.Models;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Wpf.Services;

/// <summary>
/// SynchrolightAPI の HTTP エンドポイントを呼び出すクライアント。
/// WPF の全操作を API 経由で実行する。
/// </summary>
public class SynchrolightApiClient
{
    private readonly HttpClient _http;
    private readonly HttpClient _sseHttp;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SynchrolightApiClient(HttpClient httpClient)
    {
        _http = httpClient;
        // SSE用: タイムアウト無制限（長時間接続を維持するため）
        _sseHttp = new HttpClient { BaseAddress = httpClient.BaseAddress, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    // =========================================================
    //  Transport
    // =========================================================

    public async Task<string[]> ScanPortsAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>("api/transport/scan", JsonOptions);
        var ports = resp.GetProperty("data").GetProperty("ports");
        return ports.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    public async Task<bool> ConnectAsync(IEnumerable<string> portNames)
    {
        var body = new { portNames = portNames.ToArray() };
        var resp = await _http.PostAsJsonAsync("api/transport/connect", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DisconnectAsync()
    {
        var resp = await _http.PostAsync("api/transport/disconnect", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<TransportStatusResult> GetTransportStatusAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>("api/transport/status", JsonOptions);
        var data = resp.GetProperty("data");
        return new TransportStatusResult(
            data.GetProperty("queueLength").GetInt32(),
            data.TryGetProperty("highPriorityQueueLength", out var hpq) ? hpq.GetInt32() : 0,
            data.GetProperty("connectedPorts").GetInt32(),
            data.TryGetProperty("disconnectedPorts", out var dp) ? dp.GetInt32() : 0,
            data.TryGetProperty("lastError", out var le) && le.ValueKind != JsonValueKind.Null
                ? le.GetString() : null);
    }

    public async Task<bool> SendKeepAliveAsync(string base64Packet)
    {
        var body = new { base64Packet };
        var resp = await _http.PostAsJsonAsync("api/transport/keepalive", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    // =========================================================
    //  Transmitter
    // =========================================================

    public async Task<bool> InitTransmitterAsync(int channel, int power)
    {
        var body = new { channel, power };
        var resp = await _http.PostAsJsonAsync("api/transmitter/init", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetChannelAsync(int channel)
    {
        var body = new { channel };
        var resp = await _http.PostAsJsonAsync("api/transmitter/set-channel", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetPowerAsync(int power)
    {
        var body = new { power };
        var resp = await _http.PostAsJsonAsync("api/transmitter/set-power", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    // =========================================================
    //  Light — 全コマンド
    // =========================================================

    public async Task<bool> SetGlobalColorAsync(int field, byte r, byte g, byte b)
    {
        var body = new { color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/global", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetPointsAsync(int field, int startRow, int startCol, byte len, byte r, byte g, byte b)
    {
        var body = new { field, startRow, startCol, len, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/points", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetRowsAsync(int field, int startRow, int rowLen, byte r, byte g, byte b)
    {
        var body = new { field, startRow, rowLen, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/rows", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetRowsEachAsync(int field, int startRow, int len, byte r, byte g, byte b)
    {
        var body = new { field, startRow, len, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/rows/each", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetColsAsync(int field, int startCol, int colLen, byte r, byte g, byte b)
    {
        var body = new { field, startCol, colLen, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/cols", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetColsEachAsync(int field, int startCol, int len, byte r, byte g, byte b)
    {
        var body = new { field, startCol, len, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/cols/each", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetMultiColsAsync(int field, int startCol, int colLen, byte r, byte g, byte b)
    {
        var body = new { field, startCol, colLen, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/cols", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetMultiRowsAsync(int field, int startRow, int rowLen, byte r, byte g, byte b)
    {
        var body = new { field, startRow, rowLen, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/rows", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> PlayHwSequenceAsync(uint frameNo)
    {
        var body = new { frameNo };
        var resp = await _http.PostAsJsonAsync("api/light/sequence", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetBlockColorAsync(int progNo, int blockNo, byte r, byte g, byte b)
    {
        var body = new { progNo, blockNo, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/block", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetBlockSectorAsync(int progNo, int blockNo, byte r, byte g, byte b)
    {
        var body = new { progNo, blockNo, color = new { r, g, b } };
        var resp = await _http.PostAsJsonAsync("api/light/block-sector", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetRxChannelAsync(int field, int startRow, int len, int channel)
    {
        var body = new { field, startRow, len, channel };
        var resp = await _http.PostAsJsonAsync("api/light/rx-channel", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AllOffAsync()
    {
        var resp = await _http.PostAsync("api/light/off", null);
        return resp.IsSuccessStatusCode;
    }

    // =========================================================
    //  Effect
    // =========================================================

    public async Task<bool> StartEffectAsync(
        EffectType type, byte r, byte g, byte b,
        int? field = null, int? cycleDurationMs = null,
        int? flashIntervalMs = null, int? fadeSteps = null,
        bool? continuous = null)
    {
        var body = new
        {
            type = type.ToString(),
            color = new { r, g, b },
            field,
            cycleDurationMs,
            flashIntervalMs,
            fadeSteps,
            continuous
        };
        var resp = await _http.PostAsJsonAsync("api/effect/start", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> StopEffectAsync()
    {
        var resp = await _http.PostAsync("api/effect/stop", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<EffectStatusResult> GetEffectStatusAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>("api/effect/status", JsonOptions);
        var data = resp.GetProperty("data");
        return new EffectStatusResult(
            data.GetProperty("isRunning").GetBoolean(),
            data.TryGetProperty("effectType", out var et) && et.ValueKind != JsonValueKind.Null
                ? et.GetString() : null);
    }

    // =========================================================
    //  Sequence CRUD
    // =========================================================

    public async Task<IReadOnlyList<string>> ListSequencesAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>("api/sequence", JsonOptions);
        var names = resp.GetProperty("data").GetProperty("names");
        return names.EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    public async Task<Sequence?> GetSequenceAsync(string name)
    {
        var resp = await _http.GetAsync($"api/sequence/{Uri.EscapeDataString(name)}");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var data = json.GetProperty("data");
        return JsonSerializer.Deserialize<Sequence>(data.GetRawText(), JsonOptions);
    }

    public async Task<bool> SaveSequenceAsync(Sequence sequence)
    {
        var body = new { name = sequence.Name, steps = sequence.Steps };
        var resp = await _http.PostAsJsonAsync("api/sequence", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteSequenceAsync(string name)
    {
        var resp = await _http.DeleteAsync($"api/sequence/{Uri.EscapeDataString(name)}");
        return resp.IsSuccessStatusCode;
    }

    // =========================================================
    //  Sequence Playback
    // =========================================================

    public async Task<bool> PlaySequenceAsync(string name)
    {
        var body = new { name };
        var resp = await _http.PostAsJsonAsync("api/sequence/play", body, JsonOptions);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> StopSequenceAsync()
    {
        var resp = await _http.PostAsync("api/sequence/stop", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<SequenceStatusResult> GetSequenceStatusAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>("api/sequence/play/status", JsonOptions);
        var data = resp.GetProperty("data");
        return new SequenceStatusResult(
            data.GetProperty("isPlaying").GetBoolean(),
            data.TryGetProperty("sequenceName", out var sn) && sn.ValueKind != JsonValueKind.Null
                ? sn.GetString() : null);
    }
    // =========================================================
    //  Send Log — SSEストリーム
    // =========================================================

    /// <summary>
    /// SSEストリームに接続し、受信したログを callback で通知し続ける。
    /// CancellationToken でキャンセルするまで継続する。
    /// </summary>
    public async Task StreamSendLogAsync(Action<SendLogEntryDto> onEntry, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/transport/log/stream");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _sseHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break; // ストリーム終了

            if (line.StartsWith("data: "))
            {
                var json = line.Substring("data: ".Length);
                var entry = JsonSerializer.Deserialize<SendLogEntryDto>(json, JsonOptions);
                if (entry != null) onEntry(entry);
            }
        }
    }
}

// --- Result types ---

public record EffectStatusResult(bool IsRunning, string? EffectType);
public record SequenceStatusResult(bool IsPlaying, string? SequenceName);
public record TransportStatusResult(
    int QueueLength, int HighPriorityQueueLength,
    int ConnectedPorts, int DisconnectedPorts, string? LastError);
public record SendLogEntryDto(long Seq, DateTime Timestamp, string Direction, string Hex);
