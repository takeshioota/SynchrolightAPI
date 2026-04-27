using System.IO.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Services;

/// <summary>
/// 診断テスト: 仕様書p.12「動作確認最小サンプル」を忠実に再現
/// ポートを1つずつ試し、ch=1/pwr=0で直接Send
/// </summary>
public class DiagnosticService : BackgroundService
{
    private readonly ILogger<DiagnosticService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public DiagnosticService(
        ILogger<DiagnosticService> logger,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(500, stoppingToken);

        string[] portNames = ["COM4", "COM5", "COM6", "COM7"];

        _logger.LogInformation("=== 仕様書「動作確認最小サンプル」再現テスト ===");
        _logger.LogInformation("各ポートを1つずつ開き、ch=1/pwr=0 → A2緑を送信");
        _logger.LogInformation("");

        try
        {
            foreach (var portName in portNames)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await TestMinimalSample(portName, stoppingToken);
            }

            _logger.LogInformation("");
            _logger.LogInformation("=== 全ポートテスト完了 ===");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("テストがキャンセルされました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テスト中にエラーが発生");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    /// <summary>
    /// 仕様書p.12の最小サンプルを忠実に再現
    /// </summary>
    private async Task TestMinimalSample(string portName, CancellationToken ct)
    {
        _logger.LogInformation("=== [{Port}] 最小サンプルテスト ===", portName);

        SerialPort? sp = null;
        try
        {
            // 仕様書と同じ: OpenPort相当
            sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            sp.Open();
            _logger.LogInformation("[{Port}] オープン成功", portName);

            // --- 仕様書サンプルと完全に同じ手順 ---

            // 送信機: ch=1, pwr=0（仕様書と同じ値）
            var fa = LightProtocol.BuildTxSetChannel(1);
            _logger.LogInformation("[{Port}] FA送信(ch=1): {Hex}", portName, LightProtocol.ToHex(fa));
            sp.Write(fa, 0, fa.Length);

            var fb = LightProtocol.BuildTxSetPower(0);
            _logger.LogInformation("[{Port}] FB送信(pwr=0): {Hex}", portName, LightProtocol.ToHex(fb));
            sp.Write(fb, 0, fb.Length);

            // 全体を緑（仕様書と同じ）
            var a2 = LightProtocol.BuildA2_GlobalColor(0x01, 0x00, 0xFF, 0x00);
            _logger.LogInformation("[{Port}] A2送信(緑): {Hex}", portName, LightProtocol.ToHex(a2));
            sp.Write(a2, 0, a2.Length);

            _logger.LogInformation("[{Port}] ★ 端末機は緑に点灯していますか？", portName);

            // 30秒間A2を連続送信し続ける（200ms間隔）
            _logger.LogInformation("[{Port}] 30秒間A2を連続送信中...", portName);
            for (int i = 0; i < 150; i++)
            {
                ct.ThrowIfCancellationRequested();
                sp.Write(a2, 0, a2.Length);
                await Task.Delay(200, ct);

                // 5秒ごとにレスポンス確認
                if (i % 25 == 0 && sp.BytesToRead > 0)
                {
                    var buf = new byte[sp.BytesToRead];
                    sp.Read(buf, 0, buf.Length);
                    _logger.LogInformation("[{Port}] レスポンス: {Hex}", portName, LightProtocol.ToHex(buf));
                }
            }

            // 消灯
            var off = LightProtocol.BuildA2_GlobalColor(0x01, 0x00, 0x00, 0x00);
            sp.Write(off, 0, off.Length);
            _logger.LogInformation("[{Port}] 消灯送信", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Port}] エラー: {Error}", portName, ex.Message);
        }
        finally
        {
            try { sp?.Close(); sp?.Dispose(); } catch { }
            _logger.LogInformation("[{Port}] ポートクローズ", portName);
            _logger.LogInformation("");
        }
    }
}
