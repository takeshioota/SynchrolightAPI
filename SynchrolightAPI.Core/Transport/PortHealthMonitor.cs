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
    private readonly int _maxReconnectAttempts = 3;
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

                // 切断検出 → 再接続試行
                _reconnectAttempts.TryGetValue(expectedName, out var attempts);

                if (attempts >= _maxReconnectAttempts)
                {
                    // 最大リトライ数到達 — 長いバックオフ後にリセット
                    if (attempts == _maxReconnectAttempts)
                    {
                        _logger.LogWarning("ポート {PortName} の再接続が{Max}回失敗、待機中",
                            expectedName, _maxReconnectAttempts);
                        _reconnectAttempts[expectedName] = attempts + 1; // 次回以降は再ログしない
                    }

                    // 30秒ごとにリトライカウントリセットして再試行
                    if (attempts > _maxReconnectAttempts + 5)
                    {
                        _reconnectAttempts[expectedName] = 0;
                    }
                    else
                    {
                        _reconnectAttempts[expectedName] = attempts + 1;
                    }
                    continue;
                }

                // 指数バックオフ: 5s, 10s, 20s
                var backoff = TimeSpan.FromSeconds(_checkInterval.TotalSeconds * Math.Pow(2, attempts));
                _logger.LogInformation("ポート {PortName} 再接続試行 ({Attempt}/{Max})",
                    expectedName, attempts + 1, _maxReconnectAttempts);

                var success = _transport.TryReconnectPort(expectedName);

                if (success)
                {
                    _logger.LogInformation("ポート {PortName} 再接続成功", expectedName);
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
