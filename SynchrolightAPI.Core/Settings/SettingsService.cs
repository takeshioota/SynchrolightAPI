using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SynchrolightAPI.Settings;

/// <summary>
/// ユーザー設定の永続化サービス。
/// JSONファイルへの読み込み/保存をスレッドセーフに管理する。
/// </summary>
public class SettingsService
{
    private readonly string _filePath;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private UserSettings _current;
    private Timer? _debounceTimer;

    public SettingsService(ILogger<SettingsService> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "user-settings.json");
        _current = Load();
    }

    /// <summary>現在の設定</summary>
    public UserSettings Current => _current;

    /// <summary>設定ファイルからロード（ファイルがなければデフォルト値）</summary>
    public UserSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions);
                if (settings != null)
                {
                    _logger.LogInformation("設定ファイルを読み込みました: {Path}", _filePath);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定ファイルの読み込みに失敗: {Path}", _filePath);
        }

        _logger.LogInformation("デフォルト設定を使用します");
        return new UserSettings();
    }

    /// <summary>設定を即時保存</summary>
    public void Save()
    {
        _saveLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(_current, _jsonOptions);
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_filePath, json);
            _logger.LogDebug("設定ファイルを保存しました: {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定ファイルの保存に失敗: {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>500msデバウンス付き保存（高頻度の設定変更に対応）</summary>
    public void SaveDebounced()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => Save(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    /// <summary>設定をスレッドセーフに更新し、デバウンス保存</summary>
    public void Update(Action<UserSettings> mutator)
    {
        _saveLock.Wait();
        try
        {
            mutator(_current);
        }
        finally
        {
            _saveLock.Release();
        }
        SaveDebounced();
    }
}
