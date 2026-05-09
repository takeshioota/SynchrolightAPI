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
    private CancellationTokenSource? _recordPollCts;
    private CancellationTokenSource? _editorPollCts;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingInfo = "";

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

    // --- 簡単登録: 色+OFF設定 ---
    [ObservableProperty]
    private int _colorDurationMs = 1000;

    [ObservableProperty]
    private int _gapDurationMs = 500;

    // --- エディタ再生状態 ---
    [ObservableProperty]
    private bool _isPlayingInline;

    private bool _suppressJump;
    private bool _isJumping;
    private int _highlightedStepIndex = -1;

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
    //  シーケンス一覧 & 再生（左ペイン — 既存機能）
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
    //  エディタ再生: 連続再生 / ステップ再生 / ジャンプ
    // =========================================================

    [RelayCommand]
    private Task PlayEditorAsync() => PlayEditorInternalAsync(loop: false);

    [RelayCommand]
    private Task PlayEditorLoopAsync() => PlayEditorInternalAsync(loop: true);

    private async Task PlayEditorInternalAsync(bool loop)
    {
        if (EditSteps.Count == 0) return;

        // 既存再生を停止
        await StopEditorAsync();
        await StopAsync();

        // 時刻順ソート（ソート後のインデックスがAPIのステップインデックスと一致する）
        _suppressJump = true;
        SortByTime();
        _suppressJump = false;

        // SequenceStep配列に変換
        var steps = EditSteps.Select(e => new SequenceStep
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
        }).ToList();

        try
        {
            var ok = await _apiClient.PlaySequenceInlineAsync(steps, loop);
            if (!ok)
            {
                Status = "エディタ再生に失敗しました";
                return;
            }

            IsPlayingInline = true;
            Status = loop ? "繰り返し再生中..." : "エディタ連続再生中...";

            // ハイライト用ポーリング開始（200ms間隔）
            _editorPollCts = new CancellationTokenSource();
            var token = _editorPollCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(200, token);
                        var status = await _apiClient.GetSequenceStatusAsync();
                        App.Current?.Dispatcher.Invoke(() =>
                        {
                            if (!status.IsPlaying)
                            {
                                IsPlayingInline = false;
                                ClearHighlight();
                                Status = "再生完了";
                                _editorPollCts?.Cancel();
                                return;
                            }
                            UpdateHighlight(status.CurrentStepIndex);
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
            }, token);
        }
        catch (HttpRequestException)
        {
            Status = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task PlayStepAsync()
    {
        if (SelectedStep == null) return;

        // インライン再生中は停止する
        if (IsPlayingInline) await StopEditorAsync();

        var step = new SequenceStep
        {
            TimeOffsetMs = SelectedStep.TimeOffsetMs,
            CommandType = SelectedStep.CommandType,
            R = SelectedStep.R,
            G = SelectedStep.G,
            B = SelectedStep.B,
            Field = SelectedStep.Field,
            RetransmitCount = SelectedStep.RetransmitCount > 0 ? SelectedStep.RetransmitCount : null,
            EffectType = SelectedStep.EffectType,
            EffectCycleDurationMs = SelectedStep.EffectCycleDurationMs > 0 ? SelectedStep.EffectCycleDurationMs : null,
            FadeSteps = SelectedStep.FadeSteps > 0 ? SelectedStep.FadeSteps : null,
        };

        try
        {
            await _apiClient.PlaySingleStepAsync(step);
            Status = $"ステップ実行: {step.CommandType}";
        }
        catch (HttpRequestException)
        {
            Status = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task StopEditorAsync()
    {
        _editorPollCts?.Cancel();
        _editorPollCts?.Dispose();
        _editorPollCts = null;

        if (IsPlayingInline)
        {
            await _apiClient.StopSequenceAsync();
            Status = "停止しました";
        }

        IsPlayingInline = false;
        _isJumping = false;
        ClearHighlight();
    }

    // --- ジャンプ: 連続再生中にステップをクリックするとそこから再生再開 ---
    partial void OnSelectedStepChanged(StepEditItem? value)
    {
        if (!IsPlayingInline || _suppressJump || value == null) return;

        var idx = EditSteps.IndexOf(value);
        if (idx >= 0 && idx != _highlightedStepIndex)
        {
            _isJumping = true;
            _ = JumpToStepInternalAsync(idx);
        }
    }

    private async Task JumpToStepInternalAsync(int stepIndex)
    {
        try
        {
            var ok = await _apiClient.JumpToStepAsync(stepIndex);
            if (ok)
            {
                Status = $"ジャンプ → ステップ {stepIndex}";
            }
        }
        catch (HttpRequestException)
        {
            Status = "API接続エラー";
        }
        finally
        {
            _isJumping = false;
        }
    }

    // --- ハイライト管理 ---
    private void UpdateHighlight(int stepIndex)
    {
        if (_isJumping) return;

        // 前のハイライトをクリア
        if (_highlightedStepIndex >= 0 && _highlightedStepIndex < EditSteps.Count)
            EditSteps[_highlightedStepIndex].IsHighlighted = false;

        // 新しいハイライトを設定
        if (stepIndex >= 0 && stepIndex < EditSteps.Count)
            EditSteps[stepIndex].IsHighlighted = true;

        _highlightedStepIndex = stepIndex;
    }

    private void ClearHighlight()
    {
        foreach (var step in EditSteps)
            step.IsHighlighted = false;
        _highlightedStepIndex = -1;
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
        _suppressJump = true;
        SelectedStep = copy;
        _suppressJump = false;
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
        _suppressJump = true;
        EditSteps.Clear();
        foreach (var s in sorted) EditSteps.Add(s);
        _suppressJump = false;
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _suppressJump = true;
        EditSteps.Clear();
        _suppressJump = false;
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
    private void AddTemplateColorWithOff()
    {
        // 前のステップの末尾から間隔を空けて開始
        var baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + GapDurationMs : 0;

        // Color ON（ダブルクリックで色変更可能）
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs,
            CommandType = SequenceCommandType.Color,
            R = 0xFF, G = 0, B = 0
        });

        // 表示時間後に自動OFF
        EditSteps.Add(new StepEditItem
        {
            TimeOffsetMs = baseMs + ColorDurationMs,
            CommandType = SequenceCommandType.Off
        });
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
    //  シーケンス記録
    // =========================================================

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        try
        {
            var ok = await _apiClient.StartRecordingAsync();
            if (!ok)
            {
                Status = "記録の開始に失敗しました";
                return;
            }

            IsRecording = true;
            RecordingInfo = "0 ステップ / 0.0秒";
            Status = "記録中...";

            // ポーリング開始（1秒間隔でステータス取得）
            _recordPollCts = new CancellationTokenSource();
            var token = _recordPollCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, token);
                        var status = await _apiClient.GetRecordingStatusAsync();
                        App.Current?.Dispatcher.Invoke(() =>
                        {
                            RecordingInfo = $"{status.StepCount} ステップ / {status.ElapsedMs / 1000.0:F1}秒";
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
            }, token);
        }
        catch (HttpRequestException)
        {
            Status = "API接続エラー";
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        _recordPollCts?.Cancel();
        _recordPollCts?.Dispose();
        _recordPollCts = null;

        try
        {
            var steps = await _apiClient.StopRecordingAsync();
            IsRecording = false;

            if (steps == null || steps.Count == 0)
            {
                RecordingInfo = "";
                Status = "記録なし（操作が記録されませんでした）";
                return;
            }

            // エディタに自動展開
            _suppressJump = true;
            EditSteps.Clear();
            foreach (var step in steps)
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
            _suppressJump = false;

            // 自動命名
            NewSequenceName = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}";
            RecordingInfo = "";
            Status = $"記録完了: {steps.Count} ステップをエディタに展開しました";
        }
        catch (HttpRequestException)
        {
            IsRecording = false;
            Status = "API接続エラー";
        }
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
        _suppressJump = true;
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
        _suppressJump = false;
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
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>時刻の秒表示（読み取り専用）</summary>
    public string TimeLabel => $"{TimeOffsetMs / 1000.0:F1}s";

    partial void OnTimeOffsetMsChanged(int value)
    {
        OnPropertyChanged(nameof(TimeLabel));
    }
}
