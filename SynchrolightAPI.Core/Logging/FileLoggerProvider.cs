using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SynchrolightAPI.Logging;

/// <summary>
/// 依存パッケージ不要の簡易ファイルロガー（非同期・ノンブロッキング）。ILogger 出力を日次
/// ローテーションのファイル（既定 logs/api-YYYYMMDD.log）へ UTF-8 で追記する。
///
/// 目的：不具合発生時にこのログファイルをそのまま確認／共有すれば、原因究明・対策がスムーズに
/// なること。1 行に「時刻・レベル・コンポーネント・メッセージ」を含め、例外はスタックトレース
/// ごと残す。ON/OFF・出力先・レベルは appsettings.json の Logging:FileLog で切替（配線は Program.cs）。
///
/// 【BUG-20260928-01】以前は 1 行ごとに File.AppendAllText（open→write→close）を単一ロックで
/// 直列化していた。Debug 級（毎送信ログ＝約 60 行/秒）では送信スレッドがこのロックを通るため、
/// 仮想環境でディスク I/O が一瞬詰まると、ロックを握ったスレッドが数秒ブロックし、4 ポート分の
/// 送信スレッドごと数珠つなぎで数秒フリーズ＝「普通の演出でも遅延/停止」して見える事象があった
/// （api-20261002.log：約 4 秒の全停止が多発。シリアル WriteTimeout=500ms なのにタイムアウト 0 件
/// ＝原因は送信機側ではなくログ書き込み待ち）。
///
/// 本実装では ILogger 呼び出し側（送信 4 ポート等のアプリスレッド）は整形済みの 1 行をキューへ
/// 積むだけ（ノンブロッキング）。実際のファイル書き込みは専用の単一バックグラウンドスレッドが、
/// ファイルハンドルを開いたまま「溜まっている分をまとめて書き込み＋フラッシュ」で行う。これにより
/// ディスク遅延がアプリ動作（＝送信）を一切止めない。キューが満杯になった場合は投入をドロップし
/// （ブロックしない）、回復時にドロップ件数を記録する（ディスクが律速した証跡になる）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    // ノンブロッキング書き込みキュー。アプリスレッドは TryAdd で積むだけ（満杯でも待たない）。
    // 容量は Debug 約 60 行/秒で約 27 分分。通常のバースト（数秒のディスク遅延＝数百行）では溢れない。
    private readonly BlockingCollection<string> _queue;
    private readonly Thread _writerThread;

    // ドロップ計数（Interlocked で更新）。_droppedTotal は累積（テスト/診断用）、
    // _droppedReported は書込スレッドが「既に記録済み」の件数。
    private long _droppedTotal;
    private long _droppedReported;

    public FileLoggerProvider(string directory, LogLevel minLevel, int retainedDays, int queueCapacity = 100_000)
    {
        _dir = directory;
        _minLevel = minLevel;
        _queue = new BlockingCollection<string>(queueCapacity > 0 ? queueCapacity : 100_000);
        Directory.CreateDirectory(_dir);
        TryCleanup(retainedDays);

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "FileLoggerWriter",
        };
        _writerThread.Start();
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal LogLevel MinLevel => _minLevel;

    /// <summary>累積ドロップ件数（テスト・診断用）。</summary>
    internal long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    // 整形済み 1 行（末尾改行込み）をキューへ投入。満杯なら捨てて件数のみ加算（ブロックしない）。
    internal void Enqueue(string text)
    {
        try
        {
            if (_queue.IsAddingCompleted || !_queue.TryAdd(text))
                Interlocked.Increment(ref _droppedTotal);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding との競合（シャットダウン中）。投入できなかった分はドロップ扱い。
            Interlocked.Increment(ref _droppedTotal);
        }
    }

    // 単一バックグラウンドスレッド：ファイルを開いたまま、溜まった分をまとめて書き込み＋フラッシュ。
    private void WriterLoop()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        var sb = new StringBuilder(8 * 1024);
        try
        {
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                try
                {
                    // 日付ローテーション：都度その日のパスを計算し、変わっていれば開き直す。
                    var path = Path.Combine(_dir, $"api-{DateTime.Now:yyyyMMdd}.log");
                    if (writer == null || path != currentPath)
                    {
                        writer?.Flush();
                        writer?.Dispose();
                        writer = OpenWriter(path);
                        currentPath = path;
                    }

                    // 回復時：前回記録以降にドロップがあれば、まず 1 行記録する
                    //（ディスクが書き込みに追いつかなかった証跡）。
                    var total = Interlocked.Read(ref _droppedTotal);
                    var newDrops = total - _droppedReported;
                    if (newDrops > 0)
                    {
                        writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        writer.Write(" [WRN] FileLogger: ディスク書き込みが追いつかずログ ");
                        writer.Write(newDrops);
                        writer.Write(" 行をドロップしました");
                        writer.Write(Environment.NewLine);
                        _droppedReported = total;
                    }

                    // まとめ書き：今キューに溜まっている分を 1 回で吐き出す（syscall を削減）。
                    sb.Clear();
                    sb.Append(first);
                    while (sb.Length < 256 * 1024 && _queue.TryTake(out var more))
                        sb.Append(more);

                    writer.Write(sb.ToString());
                    writer.Flush();
                }
                catch
                {
                    // 一過性の I/O エラーでも書込スレッドを止めない。ハンドルを捨てて次回開き直す。
                    try { writer?.Dispose(); } catch { /* ignore */ }
                    writer = null;
                    currentPath = null;
                }
            }
        }
        catch { /* ログ書き込み失敗はアプリ動作に影響させない */ }
        finally
        {
            try { writer?.Flush(); writer?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        // 新規ファイル時のみ BOM 付き UTF-8（従来の File.AppendAllText(Encoding.UTF8) と同じ見え方）。
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        // 追記モード＋FileShare.ReadWrite：稼働中でも顧客がログを閲覧/コピーできるようにする。
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew);
        return new StreamWriter(fs, enc) { AutoFlush = false };
    }

    // retainedDays より古い api-*.log を起動時に削除（0 以下で無効）。
    private void TryCleanup(int retainedDays)
    {
        if (retainedDays <= 0) return;
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-retainedDays);
            foreach (var f in Directory.GetFiles(_dir, "api-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { /* クリーンアップ失敗は無視 */ }
    }

    public void Dispose()
    {
        // これ以上の投入を止め、バックグラウンドスレッドに残りを吐き出させてから終了する
        //（正常終了時にログを取りこぼさない）。
        try { _queue.CompleteAdding(); } catch { /* ignore */ }
        try { _writerThread.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _loggers.Clear();
        try { _queue.Dispose(); } catch { /* ignore */ }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _shortCategory;

        public FileLogger(FileLoggerProvider p, string category)
        {
            _p = p;
            // "SynchrolightAPI.Api.Services.EffectRunnerService" → "EffectRunnerService"
            _shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _p.MinLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var sb = new StringBuilder(128);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(LevelText(logLevel)).Append("] ")
              .Append(_shortCategory).Append(": ").Append(message)
              .Append(Environment.NewLine);
            if (exception != null)
                sb.Append(exception).Append(Environment.NewLine);

            _p.Enqueue(sb.ToString());
        }

        private static string LevelText(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
