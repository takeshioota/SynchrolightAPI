using System.Threading.Channels;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// 送信ログのリングバッファ＋SSEブロードキャスト。
/// API側 ITransport デコレータからログを受け取り、
/// ポーリング(GET /log) と SSE(GET /log/stream) の両方に対応する。
/// </summary>
public class SendLogStore
{
    private const int MaxEntries = 500;
    private readonly LinkedList<SendLogEntry> _entries = new();
    private readonly List<Channel<SendLogEntry>> _subscribers = [];
    private readonly object _lock = new();
    private long _seq;

    public void Add(string direction, string hex)
    {
        lock (_lock)
        {
            var entry = new SendLogEntry(
                ++_seq,
                DateTime.UtcNow,
                direction,
                hex);
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveFirst();

            // 全SSE購読者へ配信
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(entry))
                {
                    // 書き込めない＝購読者切断済み → 除去
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>指定シーケンス番号以降のログを返す（ポーリング用）</summary>
    public IReadOnlyList<SendLogEntry> GetSince(long afterSeq)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Seq > afterSeq).ToList();
        }
    }

    /// <summary>直近N件のログを返す</summary>
    public IReadOnlyList<SendLogEntry> GetRecent(int count)
    {
        lock (_lock)
        {
            return _entries.TakeLast(count).ToList();
        }
    }

    /// <summary>SSE購読を開始する。呼び出し元は ChannelReader を読み続ける。</summary>
    public ChannelReader<SendLogEntry> Subscribe()
    {
        var ch = Channel.CreateBounded<SendLogEntry>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_lock)
        {
            _subscribers.Add(ch);
        }

        return ch.Reader;
    }

    /// <summary>購読を解除する</summary>
    public void Unsubscribe(ChannelReader<SendLogEntry> reader)
    {
        lock (_lock)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (_subscribers[i].Reader == reader)
                {
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                    break;
                }
            }
        }
    }
}

public record SendLogEntry(long Seq, DateTime Timestamp, string Direction, string Hex);
