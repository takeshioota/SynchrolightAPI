using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SynchrolightAPI.Transport;

/// <summary>
/// ポート死活監視 + 自動再接続 BackgroundService。
/// 切断されたポートに対し指数バックオフで再接続を試行する。
/// </summary>
public class PortHealthMonitor : BackgroundService
{
    private readonly MultiPortTransport _transport;
    private readonly ILogger<PortHealthMonitor> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);
    // BUG-20261002-01: USB/VM グリッチで 4 ポートが同時脱落する事象への復帰性改善。
    // 以前は再接続を最大 3 回で諦め、以降は約 30 秒待ってから再試行していたため、USB が復帰しても
    // 最大 30 秒ほど暗転が続いた。ライブ運用では「諦めず毎チェック（5 秒毎）試行し、復帰し次第すぐ復旧」
    // を優先する。ログはスパムを避けるため最初の数回＋以降は約 1 分毎のみ出す。
    private readonly Dictionary<string, int> _reconnectAttempts = new(StringComparer.OrdinalIgnoreCase);

    public PortHealthMonitor(
        MultiPortTransport transport,
        ILogger<PortHealthMonitor> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PortHealthMonitor開始 (チェック間隔: {Interval}秒)", _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_checkInterval, stoppingToken);

            // ハートビート: USB 抜去等で OS のポート一覧から消えたオープン中ポートを
            // 切断としてマークする。SerialPort.IsOpen は抜去後も true のままになるため、
            // 送信が無い待機中でも能動的に死活を確認する。
            _transport.ReconcilePhysicalPorts();

            var expectedPorts = _transport.ExpectedPortNames;
            var currentPorts = _transport.ListPorts();

            foreach (var expectedName in expectedPorts)
            {
                var portInfo = currentPorts.FirstOrDefault(p =>
                    string.Equals(p.Name, expectedName, StringComparison.OrdinalIgnoreCase));

                // 接続済みまたはリストにない → リトライカウントリセット
                if (portInfo != null && portInfo.IsConnected)
                {
                    _reconnectAttempts.Remove(expectedName);
                    continue;
                }

                // 切断検出 → 諦めずに毎チェック（5 秒毎）再接続を試行する（ライブ運用: USB 復帰後すぐ復旧）。
                _reconnectAttempts.TryGetValue(expectedName, out var attempts);

                // ログは最初の 3 回＋以降は約 1 分毎（12 回＝5 秒×12）のみ。復旧しない間のスパムを防ぐ。
                var shouldLog = attempts < 3 || attempts % 12 == 0;
                if (shouldLog)
                {
                    _logger.LogInformation("ポート {PortName} 再接続試行 ({Attempt} 回目)",
                        expectedName, attempts + 1);
                }

                var success = _transport.TryReconnectPort(expectedName);

                if (success)
                {
                    _logger.LogInformation("ポート {PortName} 再接続成功（{Attempt} 回目）",
                        expectedName, attempts + 1);
                    _reconnectAttempts.Remove(expectedName);
                }
                else
                {
                    _reconnectAttempts[expectedName] = attempts + 1;
                }
            }
        }
    }
}
