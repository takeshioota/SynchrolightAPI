using Microsoft.Extensions.Logging;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// Bluetooth BLE経由でRGBファイルデータを端末に転送するサービス。
/// プロトコル仕様書 V4.5 セクション 2.1.3 に基づく。
/// </summary>
public class BleFileTransferService
{
    private const int SegmentSize = 256;
    private const int PacketsPerBatch = 20;
    private const int FlashWriteWaitMs = 3000;

    private readonly IBleTransport _ble;
    private readonly ILogger<BleFileTransferService> _logger;

    // 転送進捗状態
    private int _currentSegment;
    private int _totalSegments;
    private bool _isTransferring;

    public BleFileTransferService(IBleTransport ble, ILogger<BleFileTransferService> logger)
    {
        _ble = ble;
        _logger = logger;
    }

    /// <summary>転送中かどうか</summary>
    public bool IsTransferring => _isTransferring;

    /// <summary>現在の進捗（segment / total）</summary>
    public (int Current, int Total) Progress => (_currentSegment, _totalSegments);

    /// <summary>
    /// RGBファイルデータをBLE経由で端末に転送する。
    /// フロー:
    ///   1. E5 開始コマンド送信（分割長さ + 総分割数）
    ///   2. 256byte × 20パケットずつ送信 → flash書き込み待ち → 次の20パケット
    ///   3. E6 終了コマンド送信
    /// </summary>
    /// <param name="rgbData">RGBファイルデータ（バイト配列）</param>
    /// <param name="progress">進捗通知（オプション）</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task TransferFileAsync(
        byte[] rgbData,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!_ble.IsConnected)
            throw new InvalidOperationException("BLEデバイスに接続されていません。");

        if (rgbData == null || rgbData.Length == 0)
            throw new ArgumentException("転送データが空です。", nameof(rgbData));

        _isTransferring = true;
        try
        {
            _totalSegments = (rgbData.Length + SegmentSize - 1) / SegmentSize;
            _currentSegment = 0;

            _logger.LogInformation(
                "BLE ファイル転送開始: size={Size}bytes, segments={Segments}",
                rgbData.Length, _totalSegments);

            // Step 1: E5 開始コマンド
            var startCmd = BleProtocol.BuildE5_FileTransferStart(
                (ushort)SegmentSize, (uint)_totalSegments);
            var response = await _ble.WriteAndWaitResponseAsync(startCmd, 5000, ct);

            if (response == null || !BleProtocol.IsSuccessResponse(response, 0xE5))
            {
                throw new IOException("E5 開始コマンドの応答が失敗またはタイムアウト");
            }

            _logger.LogDebug("E5 開始コマンド応答OK");

            // Step 2: データ送信（256byte × 20パケットずつ）
            for (int seg = 0; seg < _totalSegments; seg++)
            {
                ct.ThrowIfCancellationRequested();

                int offset = seg * SegmentSize;
                int len = Math.Min(SegmentSize, rgbData.Length - offset);

                // 256バイトのセグメントを作成（不足分はゼロ埋め）
                var segment = new byte[SegmentSize];
                Array.Copy(rgbData, offset, segment, 0, len);

                await _ble.WriteAsync(segment, ct);

                _currentSegment = seg + 1;
                progress?.Report((_currentSegment, _totalSegments));

                // 20パケットごとにflash書き込み待ち
                if (_currentSegment % PacketsPerBatch == 0 && _currentSegment < _totalSegments)
                {
                    _logger.LogDebug(
                        "BLE 転送: {Current}/{Total} パケット送信完了、flash書き込み待ち...",
                        _currentSegment, _totalSegments);
                    await Task.Delay(FlashWriteWaitMs, ct);
                }
            }

            // Step 3: E6 終了コマンド
            var endCmd = BleProtocol.BuildE6_FileTransferEnd();
            response = await _ble.WriteAndWaitResponseAsync(endCmd, 5000, ct);

            if (response != null && BleProtocol.IsSuccessResponse(response, 0xE6))
            {
                _logger.LogInformation("BLE ファイル転送完了: {Total}パケット", _totalSegments);
            }
            else
            {
                _logger.LogWarning("E6 終了コマンドの応答が不正（転送自体は完了した可能性あり）");
            }
        }
        finally
        {
            _isTransferring = false;
        }
    }

    /// <summary>
    /// 指定セグメントのデータを検証する（E4 照会コマンド）。
    /// </summary>
    /// <param name="segmentNo">検証するセグメント番号</param>
    /// <returns>端末から返された先頭16バイト（失敗時はnull）</returns>
    public async Task<byte[]?> VerifySegmentAsync(ushort segmentNo, CancellationToken ct = default)
    {
        if (!_ble.IsConnected)
            throw new InvalidOperationException("BLEデバイスに接続されていません。");

        var verifyCmd = BleProtocol.BuildE4_FileVerify(segmentNo);
        var response = await _ble.WriteAndWaitResponseAsync(verifyCmd, 5000, ct);
        return response;
    }
}
