namespace SynchrolightAPI.Protocol;

/// <summary>
/// Bluetooth BLE通信プロトコル
/// 仕様書「コンサートライト通信プロトコル V4.5」セクション2.1-2.5に基づく実装。
/// BLE特性 FFF5 への書き込みコマンドを生成する。
/// </summary>
public static class BleProtocol
{
    // --- UUID定数 ---
    public const string ServiceUuid = "0000fff0-0000-1000-8000-00805f9b34fb";
    public const string WriteCharUuid = "0000fff5-0000-1000-8000-00805f9b34fb";
    public const string NotifyCharUuid = "0000fff4-0000-1000-8000-00805f9b34fb";

    /// <summary>デフォルトBluetooth名</summary>
    public const string DefaultDeviceName = "BLELight";

    // --- チェックサム ---

    /// <summary>
    /// BLEコマンドのチェックサム計算（最終バイト以外の合計の下位8ビット）
    /// </summary>
    public static byte CalcChecksum(byte[] data, int length)
    {
        int sum = 0;
        for (int i = 0; i < length - 1; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }

    // --- ファイル転送コマンド（セクション 2.1.3）---

    /// <summary>
    /// E5: 伝送開始コマンド（2.3.1.1）
    /// フォーマット: FB E5 [分割長さHi][分割長さLo] [総分割数(4byte)] cs
    /// </summary>
    /// <param name="segmentSize">分割長さ（通常256 = 0x0100）</param>
    /// <param name="totalSegments">総分割数（ファイルサイズ / segmentSize）</param>
    public static byte[] BuildE5_FileTransferStart(ushort segmentSize, uint totalSegments)
    {
        var data = new byte[8];
        data[0] = 0xFB;
        data[1] = 0xE5;
        data[2] = (byte)((segmentSize >> 8) & 0xFF);
        data[3] = (byte)(segmentSize & 0xFF);
        data[4] = (byte)((totalSegments >> 24) & 0xFF);
        data[5] = (byte)((totalSegments >> 16) & 0xFF);
        data[6] = (byte)((totalSegments >> 8) & 0xFF);
        data[7] = (byte)(totalSegments & 0xFF);
        // チェックサムはプロトコル仕様では0xFFとされているが、実際は計算する
        // 仕様書の例: FB E5 01 00 04 B0 95 → 最後のバイトがチェックサム
        // ここでは配列を拡張してチェックサムを付加
        var result = new byte[data.Length + 1];
        Array.Copy(data, result, data.Length);
        result[^1] = CalcChecksum(result, result.Length);
        return result;
    }

    /// <summary>
    /// E6: 終了コマンド（2.3.1.3）
    /// フォーマット: FB E6 cs
    /// </summary>
    public static byte[] BuildE6_FileTransferEnd()
    {
        var data = new byte[3];
        data[0] = 0xFB;
        data[1] = 0xE6;
        data[2] = CalcChecksum(data, 3);
        return data;
    }

    /// <summary>
    /// E4: ファイルデータ照会コマンド（2.1.4）
    /// フォーマット: FB E4 [分割番号Hi][分割番号Lo] cs
    /// 応答: 指定分割の先頭16バイトを返す（正しさの確認用）
    /// </summary>
    public static byte[] BuildE4_FileVerify(ushort segmentNo)
    {
        var data = new byte[5];
        data[0] = 0xFB;
        data[1] = 0xE4;
        data[2] = (byte)((segmentNo >> 8) & 0xFF);
        data[3] = (byte)(segmentNo & 0xFF);
        data[4] = CalcChecksum(data, 5);
        return data;
    }

    // --- 応答解析 ---

    /// <summary>
    /// BLE応答が成功かどうかを判定する。
    /// 応答フォーマット: FC [コマンド番号] [成功/失敗] cs
    /// 成功=0x01、失敗=0x00
    /// </summary>
    public static bool IsSuccessResponse(byte[] response, byte expectedCommand)
    {
        if (response == null || response.Length < 4) return false;
        return response[0] == 0xFC
            && response[1] == expectedCommand
            && response[2] == 0x01;
    }
}
