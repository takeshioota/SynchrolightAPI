using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Settings;

namespace SynchrolightAPI.Transport;

/// <summary>
/// BackgroundService: 2チャネル優先度制御 + ルーティング送信 + 再送制御
/// </summary>
public class TxWorkerService : BackgroundService
{
    private readonly MultiPortTransport _transport;
    private readonly ILogger<TxWorkerService> _logger;
    private readonly LatencyTracker? _latencyTracker;
    private readonly SettingsService? _settings;
    private readonly int _sendIntervalMs;

    // ── BUG-20261002-01: シリアル送信経路（USB-シリアル変換/ドライバ/VM USB パススルー）が
    //    WriteTimeout(500ms) を無視して sp.Write を数秒ブロックする事象への緩和。
    //    1 ポートのストールで TxWorker 全体（全ポート）が数秒フリーズ（＝レインボー FO のラグ・
    //    暗転→点灯の引っ掛かり）するのを防ぐため、
    //      ① 1 エンベロープの書き込み待ちを「ソフト書込タイムアウト」で縛り TxWorker を止めない
    //      ② ストール中のポートは同一 SerialPort への同時書き込みを避けるためゲート(Wait(0))で以降スキップ
    //    する。該当ポートの端末は復帰まで数フレーム欠落するが、全体の数秒フリーズは解消する。
    //    0 以下で従来動作（WhenAll を無制限に待機）に戻せる。既定 200ms。
    //
    //    ★追加対策（検証ログ api-20261002(3)）: 上記①が効かない事例を確認。原因はスレッドプール枯渇。
    //    ブロッキング sp.Write を Task.Run（＝スレッドプール）で実行していたため、4 ポートが同時にストール
    //    するとプールが飽和し、タイムアウト用の Task.Delay(200) の継続すら走れず 200ms で打ち切れなかった
    //    （ストール計上されるのに Write が数秒のまま）。対策として sp.Write を「ポート専用スレッド」
    //    （PortWriter）で実行し、プールを一切ブロックしない設計に変更。これで Task.Delay が確実に発火し①が
    //    実効化、かつ 1 ポートの詰まりが他ポート・Kestrel・UI 応答を巻き込まない。
    //    ※USB コントローラ/VM パススルー層が丸ごと固まる瞬間（4 ポート同時脱落）は環境側要因であり、
    //      本対策でも「全消え」は防げない（悪化抑制・復帰性改善まで）。
    private readonly int _writeSoftTimeoutMs;
    private readonly int _slowWriteWarnMs;   // 実 sp.Write がこれ以上かかったら WARN（送信経路ストールの可視化）。0 で無効。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _portGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PortWriter> _portWriters = new();
    private long _stallWindow;        // ソフト書込タイムアウトに達したエンベロープ数（直近ウィンドウ）
    private long _skipWindow;         // ストール中ポートへの書き込みをスキップした回数（直近ウィンドウ）
    private long _actualWriteMaxMs;   // 専用スレッド側で実測した sp.Write の最大所要（ms・直近ウィンドウ）

    public TxWorkerService(
        MultiPortTransport transport,
        ILogger<TxWorkerService> logger,
        IConfiguration configuration,
        LatencyTracker? latencyTracker = null,
        SettingsService? settings = null)
    {
        _transport = transport;
        _logger = logger;
        _latencyTracker = latencyTracker;
        _settings = settings;
        _sendIntervalMs = configuration.GetValue<int>("SerialPort:SendIntervalMs", 5);
        _writeSoftTimeoutMs = configuration.GetValue<int>("SerialPort:WriteTimeoutMs", 200);
        _slowWriteWarnMs = configuration.GetValue<int>("SerialPort:SlowWriteWarnMs", 500);
    }

    // ── 送信診断サマリ（BUG-20260928-01 切り分け: PC律速か送信機/シリアル律速か）──
    // Fade/Breathing で「演出が止まる」事象の切り分けに必要な
    //   ・キュー滞留（通常/高優先）… 滞留が常時高いなら消化<生産＝渋滞
    //   ・実ドレインレート（実際の送信/秒）… 生産(約50/s)を下回っていないか
    //   ・enqueue→送信の遅延 … 滞留で古いフレームが遅れて出ていないか
    //   ・Write 所要時間 … 小さいのに滞留大なら PC 側(送信間隔待ち等)律速、
    //                      大きいなら送信機/シリアル律速（この場合ドレイン高速化では直らない）
    // を INFO 級で定期的に残す（Debug に依存せず切り分け可能にする）。
    private const int DiagSummaryIntervalMs = 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TxWorker開始 (送信間隔: {Interval}ms)", _sendIntervalMs);

        var summarySw = System.Diagnostics.Stopwatch.StartNew();
        long sentInWindow = 0, droppedInWindow = 0;
        double sumLatencyMs = 0, maxLatencyMs = 0;
        double sumWriteMs = 0, maxWriteMs = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 診断サマリ（約2秒毎・直近ウィンドウに送信/破棄があった時のみ）
                if (summarySw.ElapsedMilliseconds >= DiagSummaryIntervalMs &&
                    (sentInWindow > 0 || droppedInWindow > 0))
                {
                    var sec = summarySw.ElapsedMilliseconds / 1000.0;
                    // BUG-20261002-01: ストール＝ソフト書込タイムアウトに達した回数、skip＝ストール中ポートへの
                    // 書き込みをスキップした回数。これらが増えていればシリアル送信経路（USB-シリアル/VM）がブロック
                    // している（Write 最大が大きいのに送信機/端末側ではなく、ソフトが肩代わりしてフリーズを回避）。
                    var stalls = System.Threading.Interlocked.Exchange(ref _stallWindow, 0);
                    var skips = System.Threading.Interlocked.Exchange(ref _skipWindow, 0);
                    // 実Write最大＝専用スレッドで実測した sp.Write の最大所要。ソフトタイムアウトで「Write最大」は
                    // 200ms 前後に頭打ちになるため、送信経路の真のストール時間はこちらで確認する。
                    var actualWriteMax = System.Threading.Interlocked.Exchange(ref _actualWriteMaxMs, 0);
                    _logger.LogInformation(
                        "TxWorker 送信要約: 送信={Sent}({Rate:F0}/s) 破棄={Dropped} ストール={Stall}(skip={Skip}) 滞留(通常={NQ}/連続={CQ}/高={HQ}) " +
                        "enqueue→送信[平均={AvgLat:F0}ms 最大={MaxLat:F0}ms] Write[平均={AvgW:F1}ms 最大={MaxW:F1}ms] 実Write最大={ActualW}ms",
                        sentInWindow, sentInWindow / sec, droppedInWindow, stalls, skips,
                        _transport.NormalReader.Count, _transport.ContinuousReader.Count, _transport.HighPriorityReader.Count,
                        sentInWindow > 0 ? sumLatencyMs / sentInWindow : 0, maxLatencyMs,
                        sentInWindow > 0 ? sumWriteMs / sentInWindow : 0, maxWriteMs, actualWriteMax);
                    summarySw.Restart();
                    sentInWindow = droppedInWindow = 0;
                    sumLatencyMs = maxLatencyMs = sumWriteMs = maxWriteMs = 0;
                }

                SendEnvelope? envelope = null;

                // 高優先 → 連続(最新値) → 通常 の順に消化。
                // 連続チャネルはフェード等のライブ補間フレーム（最新値のみ保持）で、通常チャネルより
                // 先に消化してアニメーションを滞りなく送る。連続は容量が小さく約20ms毎にしか補充されない
                // ため、消化に余裕があれば自然に空となり通常チャネルも読まれる（通常の恒常的な飢餓は生じない）。
                if (_transport.HighPriorityReader.TryRead(out envelope))
                {
                    // high priority packet available
                }
                else if (_transport.ContinuousReader.TryRead(out envelope))
                {
                    // continuous (latest-value) frame available
                }
                else if (_transport.NormalReader.TryRead(out envelope))
                {
                    // normal packet available
                }
                else
                {
                    // 全チャネル空 → いずれかにデータが来るまで待機
                    var highTask = _transport.HighPriorityReader.WaitToReadAsync(stoppingToken).AsTask();
                    var contTask = _transport.ContinuousReader.WaitToReadAsync(stoppingToken).AsTask();
                    var normalTask = _transport.NormalReader.WaitToReadAsync(stoppingToken).AsTask();
                    await Task.WhenAny(highTask, contTask, normalTask);
                    continue; // ループ先頭に戻ってTryReadで取得
                }

                // Deadline超過チェック
                if (envelope.IsExpired)
                {
                    _logger.LogWarning("Deadline超過パケット破棄 [{OpId}]", envelope.OperationId ?? "-");
                    droppedInWindow++;
                    continue;
                }

                var writeSw = System.Diagnostics.Stopwatch.StartNew();
                var sentPorts = await SendToTargetPorts(envelope);
                writeSw.Stop();

                // 診断カウンタ更新（enqueue→送信の遅延は SendToTargetPorts 内で記録済みの GetElapsedMs と同義）
                var latMs = envelope.GetElapsedMs();
                var writeMs = writeSw.Elapsed.TotalMilliseconds;
                sentInWindow++;
                sumLatencyMs += latMs; if (latMs > maxLatencyMs) maxLatencyMs = latMs;
                sumWriteMs += writeMs; if (writeMs > maxWriteMs) maxWriteMs = writeMs;

                // 送信完了ログ（Debug）は Write 計測窓の外で出す。
                // 以前は WriteToPort 内（＝計測窓の内側）でポート毎に LogDebug しており、Debug 級では
                // この同期ログ I/O が Write 所要に混入して「Write 時間」を汚染していた（BUG-20260928-01）。
                // ロガーは非同期化済みだが、計測の純度を保つため送信完了ログはここで 1 エンベロープ 1 行に集約する。
                if (sentPorts.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("送信完了 [{OpId}] → {Ports} (遅延={Latency:F1}ms, Write={Write:F1}ms)",
                        envelope.OperationId ?? "-",
                        string.Join(",", sentPorts.Select(p => p.PortName)), latMs, writeMs);
                }

                if (_sendIntervalMs > 0)
                {
                    await Task.Delay(_sendIntervalMs, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TxWorker停止");
        }
        finally
        {
            // ポート専用スレッドを終了（各 PortWriter の内部スレッドは Dispose で CompleteAdding → 自然終了）。
            foreach (var w in _portWriters.Values)
            {
                try { w.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    private async Task<IReadOnlyList<SerialPort>> SendToTargetPorts(SendEnvelope envelope)
    {
        var allPorts = _transport.ConnectedPorts;
        var targetPorts = FilterPorts(allPorts, envelope.TargetPortNames);

        // 再送回数: コマンド単位のオーバーライド → グローバル設定 → デフォルト3回
        var retransmitCount = envelope.Options.RetransmitCount
            ?? _settings?.Current.RetransmitCount
            ?? 3;
        var retransmitIntervalMs = _settings?.Current.RetransmitIntervalMs ?? 5;

        for (int attempt = 0; attempt < retransmitCount; attempt++)
        {
            // 各ポートへ並列・ガード付きで書き込む（ポートは独立した COM なので同時書き込みは安全）。
            // 逐次 foreach では 4 ポート分の書き込み時間が直列に積み上がり、1 エンベロープの処理時間を
            // 押し上げてキュー消化の律速になっていた（BUG-20260928-01）。並列化で 1 エンベロープあたりの
            // 実処理をおよそ 1 ポート分まで短縮する。
            // さらに BUG-20261002-01: 1 ポートが数秒ストール（VM USB パススルー等で sp.Write が WriteTimeout を
            // 無視してブロック）しても、ソフト書込タイムアウトで待ちを縛って TxWorker 全体を止めない。
            // ストールしたポートの書き込みはバックグラウンドで継続し、ゲートが解放されるまで次回以降スキップされる。
            if (targetPorts.Count >= 1)
            {
                var tasks = new Task[targetPorts.Count];
                for (int i = 0; i < targetPorts.Count; i++)
                {
                    tasks[i] = WritePortGuarded(targetPorts[i], envelope, attempt);
                }
                var all = Task.WhenAll(tasks);
                if (_writeSoftTimeoutMs > 0)
                {
                    var finished = await Task.WhenAny(all, Task.Delay(_writeSoftTimeoutMs));
                    if (!ReferenceEquals(finished, all))
                    {
                        System.Threading.Interlocked.Increment(ref _stallWindow);
                        // タイムアウトで取りこぼした WhenAll は放置せず、万一フォルトした場合の未観測例外だけ回収する。
                        _ = all.ContinueWith(static t => { _ = t.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                else
                {
                    await all; // 従来動作（無制限待機）
                }
            }

            // 最終回は待機不要
            if (attempt < retransmitCount - 1 && retransmitIntervalMs > 0)
            {
                await Task.Delay(retransmitIntervalMs);
            }
        }

        return targetPorts;
    }

    /// <summary>
    /// 1 ポートへのガード付き書き込み（BUG-20261002-01）。前回の書き込みがまだストール中（ゲートが取れない）
    /// 場合は、同一 SerialPort への同時書き込み（未定義動作）を避けて即スキップし、スキップ数を計上する。
    /// 取得できた場合は <b>ポート専用スレッド</b>（PortWriter）へ書き込みを委譲し、完了時にゲートを解放する。
    /// ブロッキング sp.Write をスレッドプール（Task.Run）で実行しないことで、4 ポット同時ストール時にも
    /// プールが枯渇せず Task.Delay によるソフトタイムアウトが実効化する（検証ログ api-20261002(3) の是正）。
    /// 返す Task はポート専用スレッドの書き込み完了で完了する（継続は非同期実行＝専用スレッドを巻き込まない）。
    /// </summary>
    private Task WritePortGuarded(SerialPort sp, SendEnvelope envelope, int attempt)
    {
        var gate = _portGates.GetOrAdd(sp.PortName, static _ => new System.Threading.SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            System.Threading.Interlocked.Increment(ref _skipWindow);
            return Task.CompletedTask;
        }

        var writer = _portWriters.GetOrAdd(sp.PortName, static n => new PortWriter(n));
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = writer.TryPost(() =>
        {
            try { WriteToPort(sp, envelope, attempt); }
            finally { gate.Release(); tcs.TrySetResult(); }
        });
        if (!posted)
        {
            // 専用スレッドのキューが詰まっている/終了済み → スキップ（ゲートを戻す）。
            gate.Release();
            System.Threading.Interlocked.Increment(ref _skipWindow);
            return Task.CompletedTask;
        }
        return tcs.Task;
    }

    /// <summary>
    /// 1 ポートへの書き込み＋計測＋エラー処理。各ポート専用スレッド（PortWriter）から呼ばれるため、
    /// ここで触れる共有状態はスレッドセーフなものに限る：
    /// ILogger（スレッドセーフ）／ConcurrentQueue ベースの LatencyTracker／lock 済みの
    /// MarkPortDisconnected／参照代入のみの SetLastError（最後勝ちで無害）／Interlocked 更新の実測値。
    /// 送信完了（成功）ログは呼び出し側が Write 計測窓の外でまとめて出す（BUG-20260928-01：
    /// 同期ログ I/O が Write 所要に混入するのを避けるため、ここでは出さない）。
    /// </summary>
    private void WriteToPort(SerialPort sp, SendEnvelope envelope, int attempt)
    {
        try
        {
            // 実 sp.Write の所要を専用スレッド側で実測（ソフトタイムアウトで「Write最大」が頭打ちになるため、
            // 送信経路の真のストール時間はここで把握する）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sp.Write(envelope.Packet, 0, envelope.Packet.Length);
            sw.Stop();

            var ms = (long)sw.Elapsed.TotalMilliseconds;
            InterlockedMax(ref _actualWriteMaxMs, ms);
            if (_slowWriteWarnMs > 0 && ms >= _slowWriteWarnMs)
            {
                _logger.LogWarning("ポート {Port} の書き込みに {Ms}ms（送信経路のストール疑い＝USB-シリアル/VM 側）",
                    sp.PortName, ms);
            }

            // 初回成功時のみポート毎のレイテンシを記録（送信完了ログ自体は計測窓の外＝呼び出し側で集約）。
            if (attempt == 0)
            {
                _latencyTracker?.Record(sp.PortName, envelope.GetElapsedMs());
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "ポート {Port} 切断検出 [{OpId}]", sp.PortName, envelope.OperationId ?? "-");
            _transport.MarkPortDisconnected(sp.PortName);
            _transport.SetLastError($"{sp.PortName}: disconnected - {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ポート {Port} への送信失敗 [{OpId}]", sp.PortName, envelope.OperationId ?? "-");
            _transport.SetLastError($"{sp.PortName}: {ex.Message}");
        }
    }

    private static IReadOnlyList<SerialPort> FilterPorts(
        IReadOnlyList<SerialPort> allPorts,
        IReadOnlyList<string>? targetPortNames)
    {
        if (targetPortNames == null || targetPortNames.Count == 0)
            return allPorts; // 全ポート同報

        return allPorts
            .Where(p => targetPortNames.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>location を value が上回る場合のみ value に更新する（ロックフリーの最大値更新）。</summary>
    private static void InterlockedMax(ref long location, long value)
    {
        long cur;
        while (value > (cur = System.Threading.Interlocked.Read(ref location)))
        {
            if (System.Threading.Interlocked.CompareExchange(ref location, value, cur) == cur) break;
        }
    }

    /// <summary>
    /// ポート専用の書き込みスレッド（BUG-20261002-01）。ブロッキング sp.Write を <b>スレッドプールではなく</b>
    /// 専用スレッドで実行するための最小のワークキュー。これにより、ある COM が数秒ストールしても
    /// ・スレッドプールが枯渇しない（Task.Delay によるソフトタイムアウトが実効化）
    /// ・他ポート／Kestrel／UI 応答が巻き込まれない
    /// を保証する。1 ポート 1 書き込みはゲート(SemaphoreSlim)で直列化されるため、キューは実質 1 件で滞留しない。
    /// </summary>
    private sealed class PortWriter : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue =
            new(boundedCapacity: 16);
        private readonly Thread _thread;

        public PortWriter(string portName)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "PortWriter-" + portName
            };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try { work(); }
                    catch { /* 個々の書き込みは work 内で処理済み（念のため握りつぶす） */ }
                }
            }
            // ObjectDisposedException は InvalidOperationException の派生＝1 つで両方捕捉（Dispose/CompleteAdding 競合時）。
            catch (InvalidOperationException) { /* 列挙終了・破棄時 */ }
        }

        /// <summary>書き込みを投入。キュー満杯/終了済みなら false（呼び出し側はスキップ扱い）。</summary>
        public bool TryPost(Action work)
        {
            // ObjectDisposedException は InvalidOperationException の派生なので 1 つで両方を捕捉する。
            try { return _queue.TryAdd(work); }
            catch (InvalidOperationException) { return false; } // CompleteAdding 済み／破棄済み
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch { /* best effort */ }
        }
    }
}
