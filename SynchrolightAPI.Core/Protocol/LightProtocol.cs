using System.IO.Ports;

namespace SynchrolightAPI.Protocol;

/// <summary>
/// シンクロライト送信コマンド・プロトコル
/// 仕様書「送信コマンド・プロトコル一覧」に基づく実装
/// </summary>
public static class LightProtocol
{
    // ----------------------------
    // 共通：チェックサム
    // ----------------------------

    /// <summary>
    /// 2.4G送信機設定コマンド(FA/FB): 最初の3バイト合計の下位8bit
    /// </summary>
    public static byte CalcChecksum4(byte b0, byte b1, byte b2)
        => (byte)((b0 + b1 + b2) & 0xFF);

    /// <summary>
    /// ライト制御コマンド(A0/A1/A2...): チェックサム以外(=先頭～末尾-1)の合計の下位8bit
    /// </summary>
    public static byte CalcChecksum32(byte[] frame32)
    {
        if (frame32 == null) throw new ArgumentNullException(nameof(frame32));
        if (frame32.Length != 32) throw new ArgumentException("frame32 must be 32 bytes.");

        int sum = 0;
        for (int i = 0; i < 31; i++) sum += frame32[i];
        return (byte)(sum & 0xFF);
    }

    // ----------------------------
    // レインボー：色フレーム番号（純関数）
    // ----------------------------

    /// <summary>
    /// レインボーの colorFrameNo を「経過時間の純関数」として算出する。
    /// frame = (elapsedMs / effectiveCycleMs) % colorCount。
    /// 色替えの間隔は壁時計時間に完全に固定される（送信ループのジッタや負荷の影響を受けない）。
    /// ＝ PC 側では色循環が速くなり得ないことを保証する不変条件（BUG-20260903-01 の切り分け根拠）。
    /// effectiveCycleMs&lt;=0 または colorCount&lt;=0 のときは 0 を返す。
    /// </summary>
    public static byte RainbowFrameAt(long elapsedMs, int effectiveCycleMs, int colorCount)
    {
        if (effectiveCycleMs <= 0 || colorCount <= 0) return 0;
        if (elapsedMs < 0) elapsedMs = 0;
        return (byte)(elapsedMs / effectiveCycleMs % colorCount);
    }

    // ----------------------------
    // 送信機設定：FA / FB（4バイト）
    // ----------------------------

    /// <summary>送信機の無線チャネル設定 (FA 01 ch cs), ch=1..4</summary>
    public static byte[] BuildTxSetChannel(byte ch)
    {
        if (ch < 1 || ch > 4) throw new ArgumentOutOfRangeException(nameof(ch), "ch must be 1..4");

        byte[] f = new byte[4];
        f[0] = 0xFA;
        f[1] = 0x01;
        f[2] = ch;
        f[3] = CalcChecksum4(f[0], f[1], f[2]);
        return f;
    }

    /// <summary>送信機の送信電力設定 (FB 01 pwr cs), pwr=0..3</summary>
    public static byte[] BuildTxSetPower(byte pwr)
    {
        if (pwr > 3) throw new ArgumentOutOfRangeException(nameof(pwr), "pwr must be 0..3");

        byte[] f = new byte[4];
        f[0] = 0xFB;
        f[1] = 0x01;
        f[2] = pwr;
        f[3] = CalcChecksum4(f[0], f[1], f[2]);
        return f;
    }

    // ----------------------------
    // ライト制御（32バイト）
    // ----------------------------

    /// <summary>
    /// 32バイトフレームを作って、最後にチェックサムを入れる共通ヘルパ
    /// </summary>
    private static byte[] Finalize32(byte[] frame32)
    {
        if (frame32.Length != 32) throw new ArgumentException("frame32 must be 32 bytes.");
        frame32[31] = CalcChecksum32(frame32);
        return frame32;
    }

    /// <summary>
    /// A2: 全体一括色 (A2 field R G B [0埋め] cs)
    /// field=0x01: ID書き込み済み端末, field=0x00: ID未書き込み端末
    /// </summary>
    public static byte[] BuildA2_GlobalColor(byte field, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xA2;
        f[1] = field;
        f[2] = r;
        f[3] = g;
        f[4] = b;
        return Finalize32(f);
    }

    /// <summary>
    /// A1: シーケンス再生 (A1 01 frameNo(4bytes little endian?) [0埋め] cs)
    /// ※仕様書が「32bitフレーム番号」としているため、ここでは little-endian で格納。
    /// 実機で逆なら入替してください。
    /// </summary>
    public static byte[] BuildA1_PlaySequence(uint frameNo)
    {
        var f = new byte[32];
        f[0] = 0xA1;
        f[1] = 0x01;

        // 4バイト
        f[2] = (byte)(frameNo & 0xFF);
        f[3] = (byte)((frameNo >> 8) & 0xFF);
        f[4] = (byte)((frameNo >> 16) & 0xFF);
        f[5] = (byte)((frameNo >> 24) & 0xFF);

        return Finalize32(f);
    }

    /// <summary>
    /// A3: フィールドゾーン定義（水平操作）— 単色版
    /// フォーマット: A3 field startPosHi startPosLo rowLength R G B [23×0] cs
    /// </summary>
    public static byte[] BuildA3_Rows(byte field, ushort startPos, byte rowLength, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xA3;
        f[1] = field;

        // big-endian
        f[2] = (byte)((startPos >> 8) & 0xFF);
        f[3] = (byte)(startPos & 0xFF);

        f[4] = rowLength;
        f[5] = r;
        f[6] = g;
        f[7] = b;

        return Finalize32(f);
    }

    /// <summary>
    /// A3: フィールドゾーン定義（水平操作）— 行別色版
    /// フォーマット: A3 field startPosHi startPosLo len [RGB×len] [0埋め] cs
    /// len は最大8（32バイト制約: ヘッダ5 + RGB×8=24 + パディング2 + cs1 = 32）
    /// </summary>
    public static byte[] BuildA3_Rows(byte field, ushort startPos, byte len, (byte r, byte g, byte b)[] colors)
    {
        if (len == 0 || len > 8) throw new ArgumentOutOfRangeException(nameof(len), "len must be 1..8");
        if (colors == null || colors.Length != len) throw new ArgumentException("colors length must equal len.");

        var f = new byte[32];
        f[0] = 0xA3;
        f[1] = field;

        f[2] = (byte)((startPos >> 8) & 0xFF);
        f[3] = (byte)(startPos & 0xFF);

        f[4] = len;

        int idx = 5;
        for (int i = 0; i < len; i++)
        {
            f[idx++] = colors[i].r;
            f[idx++] = colors[i].g;
            f[idx++] = colors[i].b;
        }

        return Finalize32(f);
    }

    /// <summary>
    /// A4: 列データ（垂直操作）— 単色版
    /// フォーマット: A4 field startPosHi startPosLo colLength R G B [23×0] cs
    /// </summary>
    public static byte[] BuildA4_Cols(byte field, ushort startPos, byte colLength, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xA4;
        f[1] = field;

        f[2] = (byte)((startPos >> 8) & 0xFF);
        f[3] = (byte)(startPos & 0xFF);

        f[4] = colLength;
        f[5] = r;
        f[6] = g;
        f[7] = b;

        return Finalize32(f);
    }

    /// <summary>
    /// A4: 列データ（垂直操作）— 列別色版
    /// フォーマット: A4 field startPosHi startPosLo len [RGB×len] [0埋め] cs
    /// </summary>
    public static byte[] BuildA4_Cols(byte field, ushort startPos, byte len, (byte r, byte g, byte b)[] colors)
    {
        if (len == 0) throw new ArgumentOutOfRangeException(nameof(len));
        if (colors == null || colors.Length != len) throw new ArgumentException("colors length must equal len.");

        var f = new byte[32];
        f[0] = 0xA4;
        f[1] = field;

        f[2] = (byte)((startPos >> 8) & 0xFF);
        f[3] = (byte)(startPos & 0xFF);

        f[4] = len;

        int idx = 5;
        for (int i = 0; i < len; i++)
        {
            f[idx++] = colors[i].r;
            f[idx++] = colors[i].g;
            f[idx++] = colors[i].b;
        }

        return Finalize32(f);
    }

    /// <summary>
    /// A0: ファイル照明座標コマンド：row/col開始からlen個へRGB×len
    /// フォーマット: A0 field rowHi rowLo colHi colLo len [RGB配列(24bytes)] cs
    /// </summary>
    public static byte[] BuildA0_Points(byte field, ushort startRow, ushort startCol, byte len, (byte r, byte g, byte b)[] colors)
    {
        if (len == 0) throw new ArgumentOutOfRangeException(nameof(len));
        if (colors == null || colors.Length != len) throw new ArgumentException("colors length must equal len.");

        var f = new byte[32];
        f[0] = 0xA0;
        f[1] = field;

        f[2] = (byte)((startRow >> 8) & 0xFF);
        f[3] = (byte)(startRow & 0xFF);

        f[4] = (byte)((startCol >> 8) & 0xFF);
        f[5] = (byte)(startCol & 0xFF);

        f[6] = len;

        int idx = 7;
        for (int i = 0; i < len; i++)
        {
            f[idx++] = colors[i].r;
            f[idx++] = colors[i].g;
            f[idx++] = colors[i].b;
        }

        return Finalize32(f);
    }

    /// <summary>
    /// A6: 受信端チャンネル設定（前後分区・行ベース）— 周波数変更 3.6【2026-07-24 訂正仕様 / Jacky回答】
    /// 全体ブロードキャストの固定フレーム: A6 00 FF FF 01 ch [0埋め] cs
    ///   場次=0x00 / 開始行=0xFFFF / 長さ=0x01 はいずれも固定値。可変は ch(0x01～0x04) のみ。
    ///   ※2026-07-24 更新: 場次を 0xFF → 0x00 に訂正（Jacky回答）。開始行=0xFFFF・長さ=0x01 は据置。
    ///     旧々実装は field=0x00 / 開始行=0x0000(0x0001) / 長さ=0xFF を送っており端末が受理しなかった。
    ///   cs=(0xA5+ch)&0xFF → ch2=0xA7 / ch3=0xA8 / ch4=0xA9。
    /// </summary>
    public static byte[] BuildA6_SetRxChannel(byte ch)
    {
        if (ch < 1 || ch > 4) throw new ArgumentOutOfRangeException(nameof(ch), "ch must be 1..4");

        var f = new byte[32];
        f[0] = 0xA6; // フレームヘッダ
        f[1] = 0x00; // 場次（2026-07-24 訂正: 0xFF→0x00）
        f[2] = 0xFF; // 開始行 Hi
        f[3] = 0xFF; // 開始行 Lo（開始行=0xFFFF）
        f[4] = 0x01; // 長さ
        f[5] = ch;   // チャンネル(0x01～0x04)

        return Finalize32(f);
    }

    /// <summary>
    /// AD: 受信端チャンネル設定（左右分区・列ベース）— 周波数変更 3.7【2026-07-24 訂正仕様 / Jacky回答】
    /// 全体ブロードキャストの固定フレーム: AD 00 FF FF 01 ch [0埋め] cs
    ///   場次=0x00 / 開始列=0xFFFF / 長さ=0x01 はいずれも固定値。可変は ch(0x01～0x04) のみ。
    ///   ※2026-07-24 更新: 場次を 0xFF → 0x00 に訂正（Jacky回答）。A6 と同一の固定値。
    ///   cs=(0xAC+ch)&0xFF → ch2=0xAE / ch3=0xAF / ch4=0xB0。
    /// </summary>
    public static byte[] BuildAD_SetRxChannel(byte ch)
    {
        if (ch < 1 || ch > 4) throw new ArgumentOutOfRangeException(nameof(ch), "ch must be 1..4");

        var f = new byte[32];
        f[0] = 0xAD; // フレームヘッダ
        f[1] = 0x00; // 場次（2026-07-24 訂正: 0xFF→0x00）
        f[2] = 0xFF; // 開始列 Hi
        f[3] = 0xFF; // 開始列 Lo（開始列=0xFFFF）
        f[4] = 0x01; // 長さ
        f[5] = ch;   // チャンネル(0x01～0x04)

        return Finalize32(f);
    }

    /// <summary>
    /// A8: 左右エリア色制御
    /// フォーマット: A8 field colHi colLo colLenHi colLenLo R G B [0埋め] cs
    /// </summary>
    public static byte[] BuildA8_MultiColsSameColor(byte field, ushort startCol, ushort colLen, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xA8;
        f[1] = field;
        f[2] = (byte)((startCol >> 8) & 0xFF);
        f[3] = (byte)(startCol & 0xFF);
        f[4] = (byte)((colLen >> 8) & 0xFF);
        f[5] = (byte)(colLen & 0xFF);
        f[6] = r;
        f[7] = g;
        f[8] = b;

        return Finalize32(f);
    }

    /// <summary>
    /// AA: 複数行同色制御
    /// フォーマット: AA field rowHi rowLo rowLenHi rowLenLo R G B [0埋め] cs
    /// </summary>
    public static byte[] BuildAA_MultiRowsSameColor(byte field, ushort startRow, ushort rowLen, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xAA;
        f[1] = field;
        f[2] = (byte)((startRow >> 8) & 0xFF);
        f[3] = (byte)(startRow & 0xFF);
        f[4] = (byte)((rowLen >> 8) & 0xFF);
        f[5] = (byte)(rowLen & 0xFF);
        f[6] = r;
        f[7] = g;
        f[8] = b;

        return Finalize32(f);
    }

    /// <summary>
    /// AC: ユーザーブロック番号コマンド（セクター無効）
    /// フォーマット: AC progNo blockNo R G B [25×0] cs
    /// </summary>
    public static byte[] BuildAC_BlockColor(byte progNo, byte blockNo, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xAC;
        f[1] = progNo;
        f[2] = blockNo;
        f[3] = r;
        f[4] = g;
        f[5] = b;

        return Finalize32(f);
    }

    /// <summary>
    /// AE: ユーザーブロック番号コマンド（セクター有効）
    /// フォーマット: AE progNo blockNo R G B [25×0] cs
    /// </summary>
    public static byte[] BuildAE_BlockColorSector(byte progNo, byte blockNo, byte r, byte g, byte b)
    {
        var f = new byte[32];
        f[0] = 0xAE;
        f[1] = progNo;
        f[2] = blockNo;
        f[3] = r;
        f[4] = g;
        f[5] = b;

        return Finalize32(f);
    }

    // ----------------------------
    // ファイル書き込み（V4.5: 3.13-3.14）
    // ----------------------------

    /// <summary>
    /// A9 (3.13): 一括ファイル書き込み開始コマンド
    /// フォーマット: A9 01 00 01 [fileLenHi] [fileLenLo] [0埋め→32byte] cs
    /// ファイル書き込みコマンドA7実行前に、データ長通知の開始コマンドを先に送信する。
    /// </summary>
    public static byte[] BuildA9_FileWriteStart(ushort fileLength)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x01;
        f[2] = 0x00;
        f[3] = 0x01;
        f[4] = (byte)((fileLength >> 8) & 0xFF);
        f[5] = (byte)(fileLength & 0xFF);
        return Finalize32(f);
    }

    /// <summary>
    /// A7 (3.14): 2.4Gファイル書き込みデータ
    /// フォーマット: A7 field rowHi rowLo colHi colLo len frameNo(4byte) [RGB×len(max6)] [0埋め→32byte] cs
    /// ライトは自身のアドレスが範囲内なら保存し、範囲外なら無視する。
    /// </summary>
    public static byte[] BuildA7_FileWriteData(
        ushort row, ushort col, byte len, uint frameNo,
        (byte r, byte g, byte b)[] colors)
    {
        if (len == 0 || len > 6) throw new ArgumentOutOfRangeException(nameof(len), "len must be 1..6");
        if (colors == null || colors.Length != len) throw new ArgumentException("colors length must equal len.");

        var f = new byte[32];
        f[0] = 0xA7;
        f[1] = 0xFF; // 場次

        f[2] = (byte)((row >> 8) & 0xFF);
        f[3] = (byte)(row & 0xFF);

        f[4] = (byte)((col >> 8) & 0xFF);
        f[5] = (byte)(col & 0xFF);

        f[6] = len;

        // フレーム番号（4byte）
        f[7] = (byte)(frameNo & 0xFF);
        f[8] = (byte)((frameNo >> 8) & 0xFF);
        f[9] = (byte)((frameNo >> 16) & 0xFF);
        f[10] = (byte)((frameNo >> 24) & 0xFF);

        // RGB データ（最大 6 × 3 = 18 byte）
        int idx = 11;
        for (int i = 0; i < len; i++)
        {
            f[idx++] = colors[i].r;
            f[idx++] = colors[i].g;
            f[idx++] = colors[i].b;
        }

        return Finalize32(f);
    }

    // ----------------------------
    // Rainbow（V4.5: 3.15-3.22）
    // ----------------------------

    /// <summary>
    /// A9 0x02: レインボーカラー設定（3.15）
    /// 色リスト（2〜7色）を端末に送信する。3.16-3.22 の前に必ず送信すること。
    /// フォーマット: A9 02 N [RGB×N] [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_SetRainbowColors((byte r, byte g, byte b)[] colors)
    {
        if (colors == null || colors.Length < 2 || colors.Length > 7)
            throw new ArgumentException("colors must have 2-7 items.", nameof(colors));

        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x02;
        f[2] = (byte)colors.Length;

        int idx = 3;
        for (int i = 0; i < colors.Length; i++)
        {
            f[idx++] = colors[i].r;
            f[idx++] = colors[i].g;
            f[idx++] = colors[i].b;
        }

        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x00: レインボー常時点灯（3.16）
    /// フォーマット: A9 03 00 colorFrameNo [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowSolid(byte colorFrameNo)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x00;
        f[3] = colorFrameNo;
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x01: レインボー点滅（3.17）
    /// フォーマット: A9 03 01 colorFrameNo periodHi periodLo dutyRatio [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowBlink(byte colorFrameNo, ushort periodMs, byte dutyRatio)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x01;
        f[3] = colorFrameNo;
        f[4] = (byte)((periodMs >> 8) & 0xFF);
        f[5] = (byte)(periodMs & 0xFF);
        f[6] = dutyRatio;
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x02: レインボー FI/FO（3.18）
    /// フォーマット: A9 03 02 colorFrameNo fiHi fiLo foHi foLo [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowFadeInOut(byte colorFrameNo, ushort fadeInMs, ushort fadeOutMs)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x02;
        f[3] = colorFrameNo;
        f[4] = (byte)((fadeInMs >> 8) & 0xFF);
        f[5] = (byte)(fadeInMs & 0xFF);
        f[6] = (byte)((fadeOutMs >> 8) & 0xFF);
        f[7] = (byte)(fadeOutMs & 0xFF);
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x03: レインボー フェードイン（3.19）
    /// フォーマット: A9 03 03 colorFrameNo timeHi timeLo [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowFadeIn(byte colorFrameNo, ushort timeMs)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x03;
        f[3] = colorFrameNo;
        f[4] = (byte)((timeMs >> 8) & 0xFF);
        f[5] = (byte)(timeMs & 0xFF);
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x04: レインボー フェードアウト（3.20）
    /// フォーマット: A9 03 04 colorFrameNo timeHi timeLo [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowFadeOut(byte colorFrameNo, ushort timeMs)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x04;
        f[3] = colorFrameNo;
        f[4] = (byte)((timeMs >> 8) & 0xFF);
        f[5] = (byte)(timeMs & 0xFF);
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x03 mode=0x05: 7色ランダム点滅（3.21）
    /// フォーマット: A9 03 05 colorFrameNo [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowRandom(byte colorFrameNo)
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x03;
        f[2] = 0x05;
        f[3] = colorFrameNo;
        return Finalize32(f);
    }

    /// <summary>
    /// A9 0x04: 7色ランダム一時停止（3.22）— 前回の色を保持して継続送信
    /// フォーマット: A9 04 [0埋め] cs
    /// </summary>
    public static byte[] BuildA9_RainbowPause()
    {
        var f = new byte[32];
        f[0] = 0xA9;
        f[1] = 0x04;
        return Finalize32(f);
    }

    // ----------------------------
    // 送信：SerialPort
    // ----------------------------

    public static SerialPort OpenPort(string comName)
    {
        var sp = new SerialPort(comName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        sp.Open();
        return sp;
    }

    public static void Send(SerialPort sp, byte[] data)
    {
        if (sp == null) throw new ArgumentNullException(nameof(sp));
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (!sp.IsOpen) throw new InvalidOperationException("SerialPort is not open.");

        sp.Write(data, 0, data.Length);
    }

    // デバッグ用（HEX表示）
    public static string ToHex(byte[] bytes)
        => string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
