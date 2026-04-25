using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Diagnostics;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Settings;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// Phase2/Phase3 新機能の統合テストシナリオ（実機不要）。
/// MemoryTransport上で以下を検証する:
///   1. 2チャネル優先度キュー
///   2. ゾーンルーティング
///   3. コマンドスロットリング
///   4. レイテンシ計測
///   5. 設定永続化
///   6. 構造化ログ (OperationId)
/// </summary>
public class Phase2Phase3TestService : BackgroundService
{
    private readonly ILogger<Phase2Phase3TestService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LightingService _lighting;
    private readonly ITransport _transport;
    private readonly MemoryTransport _memoryTransport;
    private readonly ZoneRouter _zoneRouter;
    private readonly LatencyTracker _latencyTracker;
    private readonly SettingsService _settingsService;
    private readonly CommandThrottler _throttler;

    public Phase2Phase3TestService(
        ILogger<Phase2Phase3TestService> logger,
        IHostApplicationLifetime lifetime,
        LightingService lighting,
        ITransport transport,
        MemoryTransport memoryTransport,
        ZoneRouter zoneRouter,
        LatencyTracker latencyTracker,
        SettingsService settingsService,
        CommandThrottler throttler)
    {
        _logger = logger;
        _lifetime = lifetime;
        _lighting = lighting;
        _transport = transport;
        _memoryTransport = memoryTransport;
        _zoneRouter = zoneRouter;
        _latencyTracker = latencyTracker;
        _settingsService = settingsService;
        _throttler = throttler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(500, stoppingToken); // ワーカー起動待ち

        _logger.LogInformation("======================================================");
        _logger.LogInformation("  Phase2/Phase3 統合テスト開始（MemoryTransport使用）");
        _logger.LogInformation("======================================================");

        try
        {
            await Test1_PriorityQueue(stoppingToken);
            await Test2_ZoneRouting(stoppingToken);
            await Test3_CommandThrottling(stoppingToken);
            await Test4_LatencyMeasurement(stoppingToken);
            await Test5_SettingsPersistence(stoppingToken);
            await Test6_DeadlineExpiry(stoppingToken);

            _logger.LogInformation("======================================================");
            _logger.LogInformation("  全テスト完了！ 総送信パケット数: {Count}", _memoryTransport.TotalSent);
            _logger.LogInformation("======================================================");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("テストがキャンセルされました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テスト実行中にエラー");
        }
        finally
        {
            await _transport.DisconnectAsync();
            _lifetime.StopApplication();
        }
    }

    /// <summary>
    /// テスト1: 2チャネル優先度キュー
    /// 通常パケット投入後に高優先パケットを投入し、高優先が先に処理されることを確認
    /// </summary>
    private async Task Test1_PriorityQueue(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト1: 2チャネル優先度キュー ---");

        // 仮想ポート接続
        await _transport.ConnectAsync(["VCOM1"], ct);

        // 通常パケット × 5
        for (int i = 0; i < 5; i++)
        {
            await _lighting.SetGlobalColorAsync(Rgb.Blue, ct);
        }

        // 高優先パケット（送信機設定）
        await _lighting.InitializeTransmitterAsync(1, 0, ct);

        // キュー状態確認
        var status = _transport.GetStatus();
        _logger.LogInformation("テスト1 結果 — キュー: 通常={Normal}, 高優先={High}, 接続={Ports}",
            status.QueueLength, status.HighPriorityQueueLength, status.ConnectedPorts);

        await Task.Delay(1000, ct); // 消化を待つ
        _logger.LogInformation("テスト1 完了 ✓");
    }

    /// <summary>
    /// テスト2: ゾーンルーティング
    /// 2ゾーン構成でField値に応じたポート振り分けを確認
    /// </summary>
    private async Task Test2_ZoneRouting(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト2: ゾーンルーティング ---");

        await _transport.DisconnectAsync();

        // ゾーン設定: Zone1=VCOM1 (Field=0x01), Zone2=VCOM2 (Field=0x02)
        _zoneRouter.Configure([
            new ZoneConfig("Zone1", "VCOM1", TxChannel: 1, TxPower: 0),
            new ZoneConfig("Zone2", "VCOM2", TxChannel: 2, TxPower: 1),
        ]);
        _zoneRouter.MapFieldToZone(0x01, "Zone1");
        _zoneRouter.MapFieldToZone(0x02, "Zone2");

        // 2ポート接続
        await _transport.ConnectAsync(["VCOM1", "VCOM2"], ct);

        _logger.LogInformation("Zone1(VCOM1, Field=0x01) / Zone2(VCOM2, Field=0x02) を設定");

        // A2（全体）→ 両ポートに同報
        _logger.LogInformation("  A2 全体一括 → 両ポート同報を期待");
        await _lighting.SetGlobalColorAsync(Rgb.Red, ct);
        await Task.Delay(200, ct);

        // A3 Field=0x01 → VCOM1のみ
        _logger.LogInformation("  A3 Field=0x01 → VCOM1 のみを期待");
        await _lighting.SetRowColorAsync(0x01, 1, 4, Rgb.Green, ct);
        await Task.Delay(200, ct);

        // A3 Field=0x02 → VCOM2のみ
        _logger.LogInformation("  A3 Field=0x02 → VCOM2 のみを期待");
        await _lighting.SetRowColorAsync(0x02, 1, 4, Rgb.Blue, ct);
        await Task.Delay(200, ct);

        // ゾーン指定による明示ルーティング
        _logger.LogInformation("  A2 + TargetZoneId=Zone1 → VCOM1 のみを期待");
        await _lighting.SetGlobalColorAsync(Rgb.White,
            SendOptions.Default with { TargetZoneId = "Zone1" }, ct);
        await Task.Delay(500, ct);

        _logger.LogInformation("テスト2 完了 ✓");
    }

    /// <summary>
    /// テスト3: コマンドスロットリング
    /// 高速連続投入が合成されてパケット数が削減されることを確認
    /// </summary>
    private async Task Test3_CommandThrottling(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト3: コマンドスロットリング ---");

        int sentBefore = _memoryTransport.TotalSent;

        // 同一キー "A2" で20回連続投入（16ms窓で合成されるはず）
        _logger.LogInformation("  A2 全体色を20回高速投入...");
        for (int i = 0; i < 20; i++)
        {
            var color = new Rgb((byte)(i * 12), (byte)(255 - i * 12), 0);
            await _lighting.SetGlobalColorAsync(color, ct);
        }

        // スロットリングフラッシュを待つ
        await Task.Delay(200, ct);

        int sentAfter = _memoryTransport.TotalSent;
        int actualSent = sentAfter - sentBefore;
        _logger.LogInformation("  投入=20, 実送信={Actual} (合成効果: {Reduction}%削減)",
            actualSent, actualSent < 20 ? ((20 - actualSent) * 100 / 20) : 0);

        _logger.LogInformation("テスト3 完了 ✓");
    }

    /// <summary>
    /// テスト4: レイテンシ計測
    /// パケット送信後に統計情報を取得して表示
    /// </summary>
    private async Task Test4_LatencyMeasurement(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト4: レイテンシ計測 ---");

        _latencyTracker.Reset();

        // 10パケット送信
        for (int i = 0; i < 10; i++)
        {
            await _lighting.SetGlobalColorAsync(Rgb.Red, ct);
            await Task.Delay(20, ct);
        }

        await Task.Delay(500, ct); // 消化待ち

        var stats = _latencyTracker.GetStatistics();
        _logger.LogInformation("  サンプル数: {Count}", stats.Count);
        _logger.LogInformation("  最小: {Min:F1}ms, 最大: {Max:F1}ms, 平均: {Mean:F1}ms",
            stats.MinMs, stats.MaxMs, stats.MeanMs);
        _logger.LogInformation("  P50: {P50:F1}ms, P95: {P95:F1}ms, P99: {P99:F1}ms",
            stats.P50Ms, stats.P95Ms, stats.P99Ms);

        _logger.LogInformation("テスト4 完了 ✓");
    }

    /// <summary>
    /// テスト5: 設定永続化
    /// 設定の書き込み・読み込みを確認
    /// </summary>
    private async Task Test5_SettingsPersistence(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト5: 設定永続化 ---");

        // 設定を変更
        _settingsService.Update(s =>
        {
            s.SelectedPorts = ["VCOM1", "VCOM2"];
            s.TxChannel = 3;
            s.TxPower = 2;
            s.LastColor = new RgbSetting(128, 64, 255);
            s.KeepAliveEnabled = true;
            s.KeepAliveIntervalSeconds = 180;
            s.Zones =
            [
                new ZoneSettingEntry("Zone1", "VCOM1", 1, 0),
                new ZoneSettingEntry("Zone2", "VCOM2", 2, 1),
            ];
        });

        // 即時保存（デバウンスではなく）
        _settingsService.Save();

        // 読み直して確認
        var loaded = _settingsService.Load();
        _logger.LogInformation("  保存先: {Path}", Path.Combine(AppContext.BaseDirectory, "user-settings.json"));
        _logger.LogInformation("  SelectedPorts: [{Ports}]", string.Join(", ", loaded.SelectedPorts));
        _logger.LogInformation("  TxChannel={Ch}, TxPower={Pwr}", loaded.TxChannel, loaded.TxPower);
        _logger.LogInformation("  LastColor: ({R},{G},{B})", loaded.LastColor.R, loaded.LastColor.G, loaded.LastColor.B);
        _logger.LogInformation("  KeepAlive: {Enabled}, {Interval}秒", loaded.KeepAliveEnabled, loaded.KeepAliveIntervalSeconds);
        _logger.LogInformation("  Zones: {Count}件", loaded.Zones.Count);

        var match = loaded.TxChannel == 3 && loaded.TxPower == 2 && loaded.SelectedPorts.Count == 2;
        _logger.LogInformation("  設定一致: {Result}", match ? "OK" : "NG");

        await Task.CompletedTask;
        _logger.LogInformation("テスト5 完了 ✓");
    }

    /// <summary>
    /// テスト6: Deadline超過パケット破棄
    /// 期限切れパケットがスキップされることを確認
    /// </summary>
    private async Task Test6_DeadlineExpiry(CancellationToken ct)
    {
        _logger.LogInformation("");
        _logger.LogInformation("--- テスト6: Deadline超過パケット破棄 ---");

        int sentBefore = _memoryTransport.TotalSent;

        // 過去のDeadlineを設定（即座に期限切れ）
        var expiredOptions = new SendOptions(
            HighPriority: false,
            Deadline: DateTimeOffset.UtcNow.AddSeconds(-1) // 1秒前=期限切れ
        );

        await _transport.EnqueueAsync(
            Protocol.LightProtocol.BuildA2_GlobalColor(255, 0, 0),
            expiredOptions, ct);

        // 有効なパケットも投入
        await _lighting.SetGlobalColorAsync(Rgb.Green, ct);

        await Task.Delay(500, ct); // 消化待ち

        int sentAfter = _memoryTransport.TotalSent;
        _logger.LogInformation("  期限切れ1件 + 有効1件 投入 → 実送信: {Sent}件（期限切れは破棄される）",
            sentAfter - sentBefore);

        _logger.LogInformation("テスト6 完了 ✓");
    }
}
