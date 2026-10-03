using System.Text.RegularExpressions;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// BUG-20261002-01（No.40 レインボー再生時の挙動不安定＝レインボー FO のラグ・暗転→点灯のフリーズ、
/// および Fade/Blink/Breathing の「一瞬つかえる」）対策の回帰ガード。
///
/// 対策＝シリアル送信経路（USB-シリアル変換/ドライバ/VM USB パススルー）が WriteTimeout(500ms) を無視して
/// sp.Write を数秒ブロックしても、1 ポートのストールで TxWorker 全体（全ポート）や Kestrel/UI を巻き込んで
/// 固めないためのハードニング。実挙動の検証には実シリアルポートが要る（WriteToPort は実 SerialPort.Write を
/// 呼ぶ）ため、ここでは TxWorkerService のソースを走査して構成要素が存続することを保証する。
///
/// 構成要素:
///   ① ソフト書込タイムアウト設定（SerialPort:WriteTimeoutMs）の読み込み
///   ② 書き込み待ちを Task.WhenAny でソフトタイムアウトに縛る
///   ③ ブロッキング sp.Write を「ポート専用スレッド」(PortWriter)で実行＝スレッドプールを枯渇させない
///      （検証ログ api-20261002(3) で、Task.Run＝プール実行だと 4 ポート同時ストールでプールが飽和し
///        Task.Delay が走れず①が無効化された事象の是正）
///   ④ 同一ポートの同時書き込みを避けるゲート（SemaphoreSlim.Wait(0) で即スキップ）
///   ⑤ 診断（ストール/スキップ計上＋専用スレッド側で実測した「実Write最大」＋遅い書き込みの WARN）
/// </summary>
public class TxWorkerStallGuardTests
{
    private static string ReadTxWorkerSource()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "SynchrolightAPI.Core", "Transport", "TxWorkerService.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("TxWorkerService.cs を見つけられませんでした（テスト基準ディレクトリから上位探索）。");
    }

    [Fact]
    public void ソフト書込タイムアウト設定を読み込む_BUG20261002_01()
    {
        var src = ReadTxWorkerSource();
        Assert.Contains("SerialPort:WriteTimeoutMs", src);
        Assert.Contains("_writeSoftTimeoutMs", src);
    }

    [Fact]
    public void 書き込み待ちはソフトタイムアウトで縛る_無制限のWhenAll直待ちに戻さない_BUG20261002_01()
    {
        var src = ReadTxWorkerSource();
        // WhenAny でタイムアウトと競争させる構造があること。
        Assert.Matches(new Regex(@"Task\.WhenAny\s*\(\s*all\s*,\s*Task\.Delay\(\s*_writeSoftTimeoutMs"), src);
        // ガード経由（WritePortGuarded）で書き込むこと。
        Assert.Contains("WritePortGuarded", src);
    }

    [Fact]
    public void ブロッキング書き込みはスレッドプールでなくポート専用スレッドで実行する_BUG20261002_01()
    {
        var src = ReadTxWorkerSource();
        // 専用スレッド型が存在し、バックグラウンドスレッドを起動すること。
        Assert.Contains("class PortWriter", src);
        Assert.Matches(new Regex(@"new\s+Thread\s*\("), src);
        Assert.Contains("IsBackground = true", src);
        // 書き込みは専用スレッドへ post する（TryPost 経由）。
        Assert.Contains("TryPost", src);
        // ★スレッドプール（Task.Run）で WriteToPort を実行しないこと（枯渇の原因なので禁止）。
        Assert.DoesNotMatch(new Regex(@"Task\.Run\([^)]*WriteToPort"), src);
    }

    [Fact]
    public void 同一ポートの同時書き込みをゲートで回避しストール中はスキップする_BUG20261002_01()
    {
        var src = ReadTxWorkerSource();
        Assert.Contains("SemaphoreSlim", src);
        // 非ブロッキングの即時取得（取れなければスキップ）。
        Assert.Contains("Wait(0)", src);
        // 書き込み完了時に必ず解放する。
        Assert.Contains("gate.Release()", src);
    }

    [Fact]
    public void 診断_ストールとスキップと実Write最大を送信要約へ計上する_BUG20261002_01()
    {
        var src = ReadTxWorkerSource();
        Assert.Contains("_stallWindow", src);
        Assert.Contains("_skipWindow", src);
        Assert.Contains("_actualWriteMaxMs", src);
        // 送信要約ログにストール/skip/実Write最大を出す。
        Assert.Contains("ストール=", src);
        Assert.Contains("skip=", src);
        Assert.Contains("実Write最大=", src);
        // 遅い実書き込みを WARN で可視化する（送信経路ストールの把握）。
        Assert.Contains("_slowWriteWarnMs", src);
    }
}
