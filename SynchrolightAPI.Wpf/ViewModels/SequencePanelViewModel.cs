using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Services;
using SynchrolightAPI.Wpf.Services;

namespace SynchrolightAPI.Wpf.ViewModels;

/// <summary>
/// シーケンス管理パネルのViewModel。
/// 仕様: 3.4 シーケンス選択＆再生、ワンクリック操作
/// </summary>
public partial class SequencePanelViewModel : ObservableObject
{
    private readonly SynchrolightApiClient _apiClient;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string? _selectedSequenceName;

    public ObservableCollection<string> SequenceNames { get; } = [];

    // --- 新規シーケンス作成用 ---
    [ObservableProperty]
    private string _newSequenceName = "";

    public ObservableCollection<StepEditItem> EditSteps { get; } = [];

    [ObservableProperty]
    private StepEditItem? _selectedStep;

    public SequenceCommandType[] CommandTypes { get; } = Enum.GetValues<SequenceCommandType>();
    public EffectType[] EffectTypes { get; } = Enum.GetValues<EffectType>();

    // --- 統計表示 ---
    [ObservableProperty]
    private string _editorInfo = "0 ステップ";

    public SequencePanelViewModel(SynchrolightApiClient apiClient)
    {
        _apiClient = apiClient;
        _ = RefreshListAsync();
        EditSteps.CollectionChanged += (_, _) => UpdateEditorInfo();
    }

    private void UpdateEditorInfo()
    {
        if (EditSteps.Count == 0)
        {
            EditorInfo = "0 ステップ";
            return;
        }
        var maxMs = EditSteps.Max(s => s.TimeOffsetMs);
        var totalSec = maxMs / 1000.0;
        EditorInfo = $"{EditSteps.Count} ステップ / {totalSec:F1} 秒";
    }

    // =========================================================
    //  シーケンス一覧 & 再生
    // =========================================================

    [RelayCommand]
    private async Task RefreshListAsync()
    {
        try
        {
            var names = await _apiClient.ListSequencesAsync();
            SequenceNames.Clear();
            foreach (var name in names)
            {
                SequenceNames.Add(name);
            }
        }
        catch (HttpRequestException)
        {
            Status = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        if (string.IsNullOrEmpty(SelectedSequenceName)) return;

        await StopAsync();

        var ok = await _apiClient.PlaySequenceAsync(SelectedSequenceName);
        if (!ok)
        {
            Status = "シーケンスの再生に失敗しました";
            return;
        }

        IsPlaying = true;
        Status = $"再生中: {SelectedSequenceName}";

        // API 側の再生完了をポーリングで検出
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(500, token);
                    var status = await _apiClient.GetSequenceStatusAsync();
                    if (!status.IsPlaying)
                    {
                        App.Current?.Dispatcher.Invoke(() =>
                        {
                            IsPlaying = false;
                            Status = "再生完了";
                        });
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }, token);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        if (IsPlaying)
        {
            await _apiClient.StopSequenceAsync();
            Status = "停止しました";
        }

        IsPlaying = false;
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (string.IsNullOrEmpty(SelectedSequenceName)) return;
        await _apiClient.DeleteSequenceAsync(SelectedSequenceName);
        await RefreshListAsync();
        Status = "削除しました";
    }

    // =========================================================
    //  エディタ: ステップ追加・削除
    // =========================================================

    [RelayCommand]
    private void AddStep()
    {
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0
        });
    }

    [RelayCommand]
    private void RemoveStep(StepEditItem? step)
    {
        if (step != null)
            EditSteps.Remove(step);
    }

    // =========================================================
    //  エディタ: 複製・移動・ソート・クリア
    // =========================================================

    [RelayCommand]
    private void DuplicateStep()
    {
        if (SelectedStep == null) return;
        var src = SelectedStep;
        var copy = new StepEditItem
        {
            TimeOffsetMs = src.TimeOffsetMs + 500,
            CommandType = src.CommandType,
            R = src.R,
            G = src.G,
            B = src.B,
            Field = src.Field,
            RetransmitCount = src.RetransmitCount,
            EffectType = src.EffectType,
            EffectCycleDurationMs = src.EffectCycleDurationMs,
            FadeSteps = src.FadeSteps,
        };
        var idx = EditSteps.IndexOf(src);
        EditSteps.Insert(idx + 1, copy);
        SelectedStep = copy;
    }

    [RelayCommand]
    private void MoveStepUp()
    {
        if (SelectedStep == null) return;
        var idx = EditSteps.IndexOf(SelectedStep);
        if (idx <= 0) return;
        EditSteps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveStepDown()
    {
        if (SelectedStep == null) return;
        var idx = EditSteps.IndexOf(SelectedStep);
        if (idx < 0 || idx >= EditSteps.Count - 1) return;
        EditSteps.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void SortByTime()
    {
        var sorted = EditSteps.OrderBy(s => s.TimeOffsetMs).ToList();
        EditSteps.Clear();
        foreach (var s in sorted) EditSteps.Add(s);
    }

    [RelayCommand]
    private void ClearSteps()
    {
        EditSteps.Clear();
        Status = "ステップをクリアしました";
    }

    // =========================================================
    //  エディタ: テンプレート追加
    // =========================================================

    [RelayCommand]
    private void AddTemplateColor()
    {
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0;
        EditSteps.Add(new StepEditItem { TimeOffsetMs = baseMs, CommandType = SequenceCommandType.Color, R = 0xFF, G = 0, B = 0 });
    }

    [RelayCommand]
    private void AddTemplateFadeInOut()
    {
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0;
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs,
            CommandType = SequenceCommandType.Effect,
            EffectType = SynchrolightAPI.Services.EffectType.Breathing,
            R = 0xFF, G = 0, B = 0,
            EffectCycleDurationMs = 2000,
            FadeSteps = 20,
        });
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs + 5000,
            CommandType = SequenceCommandType.EffectStop,
        });
    }

    [RelayCommand]
    private void AddTemplateFlash()
    {
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0;
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs,
            CommandType = SequenceCommandType.Effect,
            EffectType = SynchrolightAPI.Services.EffectType.Flash,
            R = 0xFF, G = 0xFF, B = 0xFF,
            EffectCycleDurationMs = 500,
        });
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs + 3000,
            CommandType = SequenceCommandType.EffectStop,
        });
    }

    [RelayCommand]
    private void AddTemplateSevenColor()
    {
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0;
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs,
            CommandType = SequenceCommandType.Effect,
            EffectType = SynchrolightAPI.Services.EffectType.SevenColor,
            EffectCycleDurationMs = 7000,
            FadeSteps = 20,
        });
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs + 14000,
            CommandType = SequenceCommandType.EffectStop,
        });
    }

    [RelayCommand]
    private void AddTemplateCountdown()
    {
        // 赤→黄→緑→消灯の3秒カウントダウン
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + 1000 : 0;
        EditSteps.Add(new StepEditItem { TimeOffsetMs = baseMs, CommandType = SequenceCommandType.Color, R = 0xFF, G = 0x00, B = 0x00 });
        EditSteps.Add(new StepEditItem { TimeOffsetMs = baseMs + 1000, CommandType = SequenceCommandType.Color, R = 0xFF, G = 0xFF, B = 0x00 });
        EditSteps.Add(new StepEditItem { TimeOffsetMs = baseMs + 2000, CommandType = SequenceCommandType.Color, R = 0x00, G = 0xFF, B = 0x00 });
        EditSteps.Add(new StepEditItem { TimeOffsetMs = baseMs + 3000, CommandType = SequenceCommandType.Off });
    }

    // =========================================================
    //  保存・読み込み
    // =========================================================

    [RelayCommand]
    private async Task SaveSequenceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSequenceName))
        {
            Status = "シーケンス名を入力してください";
            return;
        }

        var seq = new Sequence
        {
            Name = NewSequenceName.Trim(),
            Steps = EditSteps.Select(e => new SequenceStep
            {
                TimeOffsetMs = e.TimeOffsetMs,
                CommandType = e.CommandType,
                R = e.R,
                G = e.G,
                B = e.B,
                Field = e.Field,
                RetransmitCount = e.RetransmitCount > 0 ? e.RetransmitCount : null,
                EffectType = e.EffectType,
                EffectCycleDurationMs = e.EffectCycleDurationMs > 0 ? e.EffectCycleDurationMs : null,
                FadeSteps = e.FadeSteps > 0 ? e.FadeSteps : null,
            }).ToList()
        };

        await _apiClient.SaveSequenceAsync(seq);
        await RefreshListAsync();
        Status = $"保存しました: {seq.Name}";
    }

    [RelayCommand]
    private async Task LoadToEditorAsync()
    {
        if (string.IsNullOrEmpty(SelectedSequenceName)) return;

        var seq = await _apiClient.GetSequenceAsync(SelectedSequenceName);
        if (seq == null) return;

        NewSequenceName = seq.Name;
        EditSteps.Clear();
        foreach (var step in seq.Steps)
        {
            EditSteps.Add(new StepEditItem
            {
                TimeOffsetMs = step.TimeOffsetMs,
                CommandType = step.CommandType,
                R = step.R,
                G = step.G,
                B = step.B,
                Field = step.Field,
                RetransmitCount = step.RetransmitCount ?? 0,
                EffectType = step.EffectType,
                EffectCycleDurationMs = step.EffectCycleDurationMs ?? 0,
                FadeSteps = step.FadeSteps ?? 0,
            });
        }
        Status = $"読み込みました: {seq.Name}";
    }
}

/// <summary>編集用ステップアイテム</summary>
public partial class StepEditItem : ObservableObject
{
    [ObservableProperty] private int _timeOffsetMs;
    [ObservableProperty] private SequenceCommandType _commandType;
    [ObservableProperty] private byte _r = 0xFF;
    [ObservableProperty] private byte _g;
    [ObservableProperty] private byte _b;
    [ObservableProperty] private byte _field;
    [ObservableProperty] private int _retransmitCount;
    [ObservableProperty] private EffectType? _effectType;
    [ObservableProperty] private int _effectCycleDurationMs;
    [ObservableProperty] private int _fadeSteps;

    /// <summary>時刻の秒表示（読み取り専用）</summary>
    public string TimeLabel => $"{TimeOffsetMs / 1000.0:F1}s";

    partial void OnTimeOffsetMsChanged(int value)
    {
        OnPropertyChanged(nameof(TimeLabel));
    }
}
