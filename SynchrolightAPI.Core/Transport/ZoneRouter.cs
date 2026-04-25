namespace SynchrolightAPI.Transport;

/// <summary>
/// パケットのFieldバイトまたはSendOptions.TargetZoneIdからゾーンを解決し、
/// 送信先ポート名リストを返すルーター
/// </summary>
public class ZoneRouter
{
    private readonly Dictionary<string, ZoneConfig> _zoneById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<byte, List<ZoneConfig>> _zonesByField = [];

    /// <summary>ゾーン設定を読み込む</summary>
    public void Configure(IEnumerable<ZoneConfig> zones)
    {
        _zoneById.Clear();
        _zonesByField.Clear();

        foreach (var zone in zones)
        {
            _zoneById[zone.ZoneId] = zone;
        }
    }

    /// <summary>Field値とゾーンIDの紐付けを設定</summary>
    public void MapFieldToZone(byte field, string zoneId)
    {
        if (!_zoneById.TryGetValue(zoneId, out var zone)) return;

        if (!_zonesByField.TryGetValue(field, out var list))
        {
            list = [];
            _zonesByField[field] = list;
        }

        if (!list.Any(z => z.ZoneId.Equals(zoneId, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(zone);
        }
    }

    /// <summary>設定済みゾーン一覧を取得</summary>
    public IReadOnlyList<ZoneConfig> GetAllZones() => _zoneById.Values.ToList().AsReadOnly();

    /// <summary>
    /// パケットとオプションから送信先ポート名リストを解決する。
    /// null を返した場合は全ポート同報。
    /// </summary>
    public IReadOnlyList<string>? ResolveTargetPorts(byte[] packet, SendOptions options)
    {
        // ゾーン設定なし → 全ポート同報（Phase1互換）
        if (_zoneById.Count == 0)
            return null;

        // 明示的なゾーン指定
        if (options.TargetZoneId != null)
        {
            if (_zoneById.TryGetValue(options.TargetZoneId, out var zone))
                return [zone.PortName];

            return null; // 不明なゾーン → 全ポート同報
        }

        // パケットヘッダからコマンド種別を判定
        if (packet.Length < 2)
            return null;

        var cmdByte = packet[0];

        // A2（全体一括）→ 全ポート同報
        if (cmdByte == 0xA2)
            return null;

        // FA/FB（送信機設定）→ 全ポート同報
        if (cmdByte is 0xFA or 0xFB)
            return null;

        // Field付きコマンド (A0, A3, A4, A6, A8, AA, AC, AE)
        // packet[1] = field byte
        if (cmdByte is 0xA0 or 0xA3 or 0xA4 or 0xA6 or 0xA8 or 0xAA or 0xAC or 0xAE)
        {
            var field = packet[1];

            if (_zonesByField.TryGetValue(field, out var zones) && zones.Count > 0)
            {
                return zones.Select(z => z.PortName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // A1（シーケンス再生）→ 全ポート同報
        return null;
    }
}
