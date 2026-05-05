using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Transport;

/// <summary>
/// トランスポート層インターフェース: 送信キュー＋マルチポート管理
/// </summary>
public interface ITransport
{
    /// <summary>接続中ポートの一覧を返す</summary>
    IReadOnlyList<PortInfo> ListPorts();

    /// <summary>指定COMポート群を開く</summary>
    Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default);

    /// <summary>全ポートを閉じる</summary>
    Task DisconnectAsync();

    /// <summary>送信キューにパケットを投入 (byte[])</summary>
    Task EnqueueAsync(byte[] packet, CancellationToken ct = default);

    /// <summary>送信キューにパケットを投入 (Packet32)</summary>
    Task EnqueueAsync(Packet32 packet, CancellationToken ct = default);

    /// <summary>送信キューにパケットを投入 (byte[] + SendOptions)</summary>
    Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default);

    /// <summary>送信キューにパケットを投入 (Packet32 + SendOptions)</summary>
    Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default);

    /// <summary>通常キューの未送信パケットを破棄する</summary>
    void FlushQueue();

    /// <summary>現在の送信状態を取得</summary>
    TransportStatus GetStatus();
}
