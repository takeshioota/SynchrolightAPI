using System.Collections.Concurrent;
using System.Text;

namespace SynchrolightAPI.Api.Logging;

/// <summary>
/// 依存パッケージ不要の簡易ファイルロガー。ILogger 出力を日次ローテーションのファイル
/// （既定 logs/api-YYYYMMDD.log）へ UTF-8 で追記する。
///
/// 目的：不具合発生時にこのログファイルをそのまま確認／共有すれば、原因究明・対策が
/// スムーズになること。1 行に「時刻・レベル・コンポーネント・メッセージ」を含め、例外は
/// スタックトレースごと残す。ON/OFF・出力先・レベルは appsettings.json の
/// Logging:FileLog で切り替える（配線は Program.cs）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory, LogLevel minLevel, int retainedDays)
    {
        _dir = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_dir);
        TryCleanup(retainedDays);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal LogLevel MinLevel => _minLevel;

    internal void Append(string text)
    {
        // 日付でローテーション。書き込みは軽量なので都度 AppendAllText で十分（ロックで直列化）。
        var path = Path.Combine(_dir, $"api-{DateTime.Now:yyyyMMdd}.log");
        lock (_gate)
        {
            try { File.AppendAllText(path, text, Encoding.UTF8); }
            catch { /* ログ書き込み失敗はアプリ動作に影響させない */ }
        }
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

    public void Dispose() => _loggers.Clear();

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

            _p.Append(sb.ToString());
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
