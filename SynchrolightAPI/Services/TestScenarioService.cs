using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// FAB作業指示書に基づくテストシナリオ実行サービス
/// テスト1: A2全体制御（赤→緑→青→白→黒、5秒間隔）
/// テスト2: A3行制御（10行単位に赤/緑/青/白、10秒後消灯）
/// </summary>
public class TestScenarioService : BackgroundService
{
    private readonly ILogger<TestScenarioService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LightingService _lighting;
    private readonly ITransport _transport;

    private static readonly (string Name, Rgb Color)[] TestColors =
    [
        ("赤", Rgb.Red),
        ("緑", Rgb.Green),
        ("青", Rgb.Blue),
        ("白", Rgb.White),
        ("黒(消灯)", Rgb.Black),
    ];

    private static readonly (string Name, Rgb Color)[] RowColors =
    [
        ("赤", Rgb.Red),
        ("緑", Rgb.Green),
        ("青", Rgb.Blue),
        ("白", Rgb.White),
    ];

    private const byte Field = 0x00;
    private const int MaxRow = 200;
    private const int RowGroupSize = 10;

    public TestScenarioService(
        ILogger<TestScenarioService> logger,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        LightingService lighting,
        ITransport transport)
    {
        _logger = logger;
        _configuration = configuration;
        _lifetime = lifetime;
        _lighting = lighting;
        _transport = transport;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TxWorkerServiceとポート接続が完了するまで少し待機
        await Task.Delay(500, stoppingToken);

        var portNames = _configuration.GetSection("SerialPort:PortNames").Get<string[]>();
        if (portNames is null || portNames.Length == 0)
        {
            _logger.LogError("設定ファイルから SerialPort:PortNames を読み取れませんでした。appsettings.json を確認してください。");
            return;
        }
        var txChannel = _configuration.GetValue<byte>("SerialPort:TxChannel", 4);
        var txPower = _configuration.GetValue<byte>("SerialPort:TxPower", 0);

        _logger.LogInformation("=== シンクロライト送信テストプログラム ===");
        _logger.LogInformation("COMポート: {Ports}, チャネル: {Ch}, 電力: {Pwr}",
            string.Join(", ", portNames), txChannel, txPower);

        try
        {
            // マルチポート接続
            await _transport.ConnectAsync(portNames, stoppingToken);

            var status = _transport.GetStatus();
            _logger.LogInformation("接続ポート数: {Count}", status.ConnectedPorts);

            if (status.ConnectedPorts == 0)
            {
                _logger.LogWarning("接続可能なポートがありません。テストを中断します。");
                return;
            }

            // 送信機初期設定
            await _lighting.InitializeTransmitterAsync(txChannel, txPower, stoppingToken);

            // テスト1: A2 全体制御
            await RunTest1_GlobalColor(stoppingToken);

            // テスト2: A3 行制御
            await RunTest2_RowControl(stoppingToken);

            _logger.LogInformation("=== 全テスト完了 ===");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("テストがキャンセルされました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テスト実行中にエラーが発生しました");
        }
        finally
        {
            await _transport.DisconnectAsync();
            _lifetime.StopApplication();
        }
    }

    /// <summary>
    /// テスト1: A2 全体制御
    /// 全体を赤→緑→青→白→黒（消灯）、インターバル5秒
    /// </summary>
    private async Task RunTest1_GlobalColor(CancellationToken ct)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("テスト1: A2 全体制御（5秒間隔）");
        _logger.LogInformation("========================================");

        foreach (var (name, color) in TestColors)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("A2 全体→{Color}", name);
            await _lighting.SetGlobalColorAsync(color, ct);

            // 最後の色（黒/消灯）以外は5秒待機
            if (name != "黒(消灯)")
            {
                await Task.Delay(5000, ct);
            }
        }

        _logger.LogInformation("テスト1 完了");
        await Task.Delay(2000, ct);
    }

    /// <summary>
    /// テスト2: A3 行制御
    /// 10行単位に「赤」「緑」「青」「白」点灯、10秒後消灯（A2コマンド）
    /// </summary>
    private async Task RunTest2_RowControl(CancellationToken ct)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("テスト2: A3 行制御（10行単位、10秒後消灯）");
        _logger.LogInformation("========================================");

        int colorIndex = 0;

        for (ushort startRow = 1; startRow <= MaxRow; startRow += RowGroupSize)
        {
            ct.ThrowIfCancellationRequested();

            var (colorName, color) = RowColors[colorIndex % RowColors.Length];
            ushort endRow = (ushort)Math.Min(startRow + RowGroupSize - 1, MaxRow);
            int rowCount = endRow - startRow + 1;

            _logger.LogInformation("行 {Start}～{End} → {Color}", startRow, endRow, colorName);
            await _lighting.SetRowColorAsync(Field, startRow, (byte)rowCount, color, ct);

            // コマンド間の送信間隔（送信機の処理時間を考慮）
            await Task.Delay(50, ct);

            colorIndex++;
        }

        _logger.LogInformation("全行の点灯完了。10秒間維持します...");
        await Task.Delay(10000, ct);

        // 消灯（A2コマンド）
        _logger.LogInformation("A2 消灯");
        await _lighting.SetGlobalColorAsync(Rgb.Black, ct);

        _logger.LogInformation("テスト2 完了");
    }
}
