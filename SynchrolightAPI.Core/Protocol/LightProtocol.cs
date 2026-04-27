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
    /// A6: 受信ライトのチャンネル設定（兼 ハートビート）
    /// フォーマット: A6 field startRowHi startRowLo len ch [0埋め] cs
    /// </summary>
    public static byte[] BuildA6_SetRxChannel(byte field, ushort startRow, byte len, byte ch)
    {
        if (startRow < 1) throw new ArgumentOutOfRangeException(nameof(startRow));
        if (len == 0) throw new ArgumentOutOfRangeException(nameof(len));

        var f = new byte[32];
        f[0] = 0xA6;
        f[1] = field;
        f[2] = (byte)((startRow >> 8) & 0xFF);
        f[3] = (byte)(startRow & 0xFF);
        f[4] = len;
        f[5] = ch;

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
