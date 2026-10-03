using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Logging;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// BUG-20260928-01（No.35「普通の演出でも遅延」）の恒久対策＝ファイルログの非同期化の回帰ガード。
/// 旧実装は 1 行ごとに File.AppendAllText を単一ロックで直列化していたため、Debug 級（毎送信ログ）では
/// 送信スレッドがログのディスク I/O 待ちで数秒ブロック＝演出フリーズに見える事象があった。
/// 非同期実装では「呼び出し側は決してブロックしない」「正常終了時に取りこぼさない」ことを保証する。
/// </summary>
public class FileLoggerProviderTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl_filelog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ReadLogFile(string dir)
    {
        var files = Directory.GetFiles(dir, "api-*.log");
        Assert.Single(files);
        return File.ReadAllText(files[0]);
    }

    [Fact]
    public void 正常終了時に全行を取りこぼさずフラッシュする_BUG20260928_01()
    {
        var dir = NewTempDir();
        try
        {
            const int n = 1000;
            using (var provider = new FileLoggerProvider(dir, LogLevel.Debug, retainedDays: 0))
            {
                var logger = provider.CreateLogger("SynchrolightAPI.Transport.TxWorkerService");
                for (int i = 0; i < n; i++)
                    logger.LogInformation("line-{I}", i);
            } // Dispose で CompleteAdding → 残りを書き出してから終了

            var text = ReadLogFile(dir);
            // 全行が、かつ投入順で記録されている
            for (int i = 0; i < n; i++)
                Assert.Contains($"line-{i}", text);
            Assert.True(text.IndexOf("line-0", StringComparison.Ordinal)
                        < text.IndexOf($"line-{n - 1}", StringComparison.Ordinal),
                "ログは投入順に記録されるべき");
            // ドロップ無し（容量に余裕があるため）
            Assert.DoesNotContain("ドロップしました", text);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void 大量投入でも呼び出し側は決してブロックしない_満杯時はドロップして件数を記録する_BUG20260928_01()
    {
        var dir = NewTempDir();
        try
        {
            const int flood = 50_000;
            var sw = Stopwatch.StartNew();
            using (var provider = new FileLoggerProvider(dir, LogLevel.Debug, retainedDays: 0, queueCapacity: 5))
            {
                var logger = provider.CreateLogger("SynchrolightAPI.Transport.TxWorkerService");
                // 容量 5 に対し 5 万行を即時投入。旧来の同期実装やブロッキング Add なら
                // ディスク待ちで呼び出し側が長時間止まるが、非同期＋TryAdd では止まらない。
                for (int i = 0; i < flood; i++)
                    logger.LogDebug("flood-{I}", i);
                sw.Stop();

                // 投入（5 万回）は数秒もかからない＝呼び出し側がブロックしていない証跡。
                Assert.True(sw.ElapsedMilliseconds < 5000,
                    $"ログ投入が {sw.ElapsedMilliseconds}ms かかった（ブロックしている疑い）");
                // 容量 5 に 5 万行なので必ず溢れてドロップが発生する（＝ブロックせずに捨てている）。
                Assert.True(provider.DroppedTotal > 0, "満杯時はドロップされるはず");
            }

            // 回復時にドロップ件数が記録されている（ディスクが律速した証跡）。
            var text = ReadLogFile(dir);
            Assert.Contains("ドロップしました", text);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void MinLevel未満は出力しない_以上は出力する()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = new FileLoggerProvider(dir, LogLevel.Information, retainedDays: 0))
            {
                var logger = provider.CreateLogger("SynchrolightAPI.Transport.TxWorkerService");
                logger.LogDebug("これは出ない-DBG");
                logger.LogInformation("これは出る-INF");
                logger.LogWarning("これは出る-WRN");
            }

            var text = ReadLogFile(dir);
            Assert.DoesNotContain("これは出ない-DBG", text);
            Assert.Contains("これは出る-INF", text);
            Assert.Contains("これは出る-WRN", text);
            // レベル表記も確認
            Assert.Contains("[INF]", text);
            Assert.DoesNotContain("[DBG]", text);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void 例外はスタックトレースごと記録する()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = new FileLoggerProvider(dir, LogLevel.Debug, retainedDays: 0))
            {
                var logger = provider.CreateLogger("SynchrolightAPI.Transport.MultiPortTransport");
                try { throw new InvalidOperationException("テスト例外XYZ"); }
                catch (Exception ex) { logger.LogError(ex, "ポート送信失敗"); }
            }

            var text = ReadLogFile(dir);
            Assert.Contains("ポート送信失敗", text);
            Assert.Contains("テスト例外XYZ", text);
            Assert.Contains(nameof(InvalidOperationException), text);
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
