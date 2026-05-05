using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Models;

namespace SynchrolightAPI.Services;

/// <summary>
/// シーケンスの永続化（JSON形式で保存/読み込み）。
/// 仕様: 3.4 複数シーケンスをPC側に保存
/// </summary>
public class SequenceStore
{
    private readonly string _directory;
    private readonly ILogger<SequenceStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SequenceStore(ILogger<SequenceStore> logger, string? directory = null)
    {
        _logger = logger;
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "sequences");
        if (!Directory.Exists(_directory))
            Directory.CreateDirectory(_directory);
    }

    /// <summary>保存済みシーケンス名一覧を取得</summary>
    public IReadOnlyList<string> ListNames()
    {
        return Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>シーケンスを保存</summary>
    public void Save(Sequence sequence)
    {
        var fileName = SanitizeFileName(sequence.Name) + ".json";
        var path = Path.Combine(_directory, fileName);
        var json = JsonSerializer.Serialize(sequence, _jsonOptions);
        File.WriteAllText(path, json);
        _logger.LogInformation("シーケンス保存: {Name} → {Path}", sequence.Name, path);
    }

    /// <summary>シーケンスを読み込み</summary>
    public Sequence? Load(string name)
    {
        var fileName = SanitizeFileName(name) + ".json";
        var path = Path.Combine(_directory, fileName);

        if (!File.Exists(path))
        {
            _logger.LogWarning("シーケンスが見つかりません: {Path}", path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Sequence>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "シーケンス読み込み失敗: {Path}", path);
            return null;
        }
    }

    /// <summary>シーケンスを削除</summary>
    public bool Delete(string name)
    {
        var fileName = SanitizeFileName(name) + ".json";
        var path = Path.Combine(_directory, fileName);

        if (!File.Exists(path)) return false;

        File.Delete(path);
        _logger.LogInformation("シーケンス削除: {Path}", path);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
