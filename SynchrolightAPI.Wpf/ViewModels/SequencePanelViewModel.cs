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

    partial void OnSelectedSequenceNameChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _ = LoadToEditorAsync();
    }

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

    // --- 簡単登録設定 ---
    [ObservableProperty]
    private int _gapDurationMs = 500;

    // --- エディタ再生状態 ---
    [ObservableProperty]
    private bool _isPlayingInline;

    // --- 挿入モード ---
    [ObservableProperty]
    private bool _isInsertMode;

    // --- 煽りボタン ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype1Brush))]
    private byte _hype1R = 255;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype1Brush))]
    private byte _hype1G = 255;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype1Brush))]
    private byte _hype1B = 255;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype2Brush))]
    private byte _hype2R = 255;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype2Brush))]
    private byte _hype2G = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hype2Brush))]
    private byte _hype2B = 0;

    public System.Windows.Media.SolidColorBrush Hype1Brush
        => new(System.Windows.Media.Color.FromRgb(Hype1R, Hype1G, Hype1B));

    public System.Windows.Media.SolidColorBrush Hype2Brush
        => new(System.Windows.Media.Color.FromRgb(Hype2R, Hype2G, Hype2B));

    private bool _suppressJump;
    private bool _isJumping;
    private int _highlightedStepIndex = -1;
    private CancellationTokenSource? _autoPlayCts;

    public SendLogViewModel SendLog { get; }

    public SequencePanelViewModel(SynchrolightApiClient apiClient, SendLogViewModel sendLog)
    {
        _apiClient = apiClient;
        SendLog = sendLog;
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

        // シーケンス・エフェクトをすべて停止
        try
        {
            await _apiClient.StopSequenceAsync();
            await _apiClient.StopEffectAsync();
        }
        catch (HttpRequestException) { }

        IsPlaying = false;
        Status = "停止しました";
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (string.IsNullOrEmpty(SelectedSequenceName)) return;

        var result = System.Windows.MessageBox.Show(
            $"シーケンス「{SelectedSequenceName}」を削除しますか？",
            "削除の確認",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

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
            Continuous = e.Continuous,
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
                        await Task.Delay(50, token);
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
            Continuous = SelectedStep.Continuous,
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

    /// <summary>
    /// Enterキーによるステップ順次再生: 現在のステップを再生し、選択を次の行へ移動する。
    /// </summary>
    [RelayCommand]
    private async Task PlayStepAndAdvanceAsync()
    {
        if (SelectedStep == null || EditSteps.Count == 0) return;

        var currentIndex = EditSteps.IndexOf(SelectedStep);
        if (currentIndex < 0) return;

        // 現在のステップを再生
        await PlayStepAsync();

        // 次の行へ移動（最終行では移動しない）
        if (currentIndex + 1 < EditSteps.Count)
        {
            _suppressJump = true;
            SelectedStep = EditSteps[currentIndex + 1];
            _suppressJump = false;
        }
    }

    [RelayCommand]
    private async Task StopEditorAsync()
    {
        _editorPollCts?.Cancel();
        _editorPollCts?.Dispose();
        _editorPollCts = null;

        try
        {
            // シーケンス・エフェクト・カラーホールドをすべて停止
            await _apiClient.StopSequenceAsync();
            await _apiClient.StopEffectAsync();
        }
        catch (HttpRequestException) { }

        IsPlayingInline = false;
        _isJumping = false;
        ClearHighlight();
        Status = "停止しました";
    }

    // --- ジャンプ: 連続再生中にステップをクリックするとそこから再生再開 ---
    partial void OnSelectedStepChanged(StepEditItem? value)
    {
        if (_suppressJump || value == null) return;

        if (IsPlayingInline)
        {
            var idx = EditSteps.IndexOf(value);
            if (idx >= 0 && idx != _highlightedStepIndex)
            {
                _isJumping = true;
                _ = JumpToStepInternalAsync(idx);
            }
        }
        else
        {
            // 前回の自動再生をキャンセルして最新のみ実行
            _autoPlayCts?.Cancel();
            _autoPlayCts?.Dispose();
            var cts = new CancellationTokenSource();
            _autoPlayCts = cts;
            _ = AutoPlayAsync(cts.Token);
        }
    }

    private async Task AutoPlayAsync(CancellationToken ct)
    {
        try
        {
            // 短いデバウンスで高速連打時の多重呼び出しを防止
            await Task.Delay(30, ct);
            await PlayStepAsync();
        }
        catch (OperationCanceledException) { }
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
        if (stepIndex == _highlightedStepIndex) return;

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
        int timeOffsetMs;

        if (SelectedStep != null)
        {
            var idx = EditSteps.IndexOf(SelectedStep);
            if (idx + 1 < EditSteps.Count)
            {
                timeOffsetMs = (SelectedStep.TimeOffsetMs + EditSteps[idx + 1].TimeOffsetMs) / 2;
            }
            else
            {
                timeOffsetMs = SelectedStep.TimeOffsetMs + GapDurationMs;
            }
        }
        else
        {
            timeOffsetMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + GapDurationMs : 0;
        }

        // 空行（ペンディング）として挿入（白・エフェクト無しで初期化）
        var newStep = new StepEditItem
        {
            TimeOffsetMs = timeOffsetMs,
            IsPendingInsert = true,
            R = 0xFF, G = 0xFF, B = 0xFF,
        };

        if (SelectedStep != null)
        {
            var idx = EditSteps.IndexOf(SelectedStep);
            EditSteps.Insert(idx + 1, newStep);
        }
        else
        {
            EditSteps.Add(newStep);
        }

        _suppressJump = true;
        SelectedStep = newStep;
        _suppressJump = false;
        IsInsertMode = true;
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
        _suppressJump = true;
        EditSteps.Move(idx, idx - 1);
        _suppressJump = false;
    }

    [RelayCommand]
    private void MoveStepDown()
    {
        if (SelectedStep == null) return;
        var idx = EditSteps.IndexOf(SelectedStep);
        if (idx < 0 || idx >= EditSteps.Count - 1) return;
        _suppressJump = true;
        EditSteps.Move(idx, idx + 1);
        _suppressJump = false;
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
        IsInsertMode = false;
        _suppressJump = true;
        EditSteps.Clear();
        _suppressJump = false;
        Status = "ステップをクリアしました";
    }

    // =========================================================
    //  挿入モード: ペンディング行チェーン
    // =========================================================

    /// <summary>
    /// 選択中のペンディング行にコマンドデータを設定し、次のペンディング行を自動追加する。
    /// ペンディング行が選択されていなければ false を返す。
    /// </summary>
    private bool TryFillPendingRow(Action<StepEditItem> fill,
        Func<int, StepEditItem[]>? createAdditional = null)
    {
        if (SelectedStep == null || !SelectedStep.IsPendingInsert) return false;

        var baseMs = SelectedStep.TimeOffsetMs;
        fill(SelectedStep);
        SelectedStep.IsPendingInsert = false;

        var idx = EditSteps.IndexOf(SelectedStep);
        var additional = createAdditional?.Invoke(baseMs) ?? [];

        for (int i = 0; i < additional.Length; i++)
            EditSteps.Insert(idx + 1 + i, additional[i]);

        // 次のペンディング行を追加
        var lastIdx = idx + additional.Length;
        var lastStep = EditSteps[lastIdx];
        var timeOffsetMs = lastStep.TimeOffsetMs + GapDurationMs;
        var pendingRow = new StepEditItem
        {
            TimeOffsetMs = timeOffsetMs,
            IsPendingInsert = true,
            R = 0xFF, G = 0xFF, B = 0xFF,
        };
        EditSteps.Insert(lastIdx + 1, pendingRow);

        _suppressJump = true;
        SelectedStep = pendingRow;
        _suppressJump = false;

        return true;
    }

    [RelayCommand]
    private async Task InsertEndAsync()
    {
        IsInsertMode = false;

        // ペンディング行をすべて削除
        for (int i = EditSteps.Count - 1; i >= 0; i--)
        {
            if (EditSteps[i].IsPendingInsert)
                EditSteps.RemoveAt(i);
        }

        if (EditSteps.Count == 0)
        {
            Status = "挿入完了";
            return;
        }

        Status = "挿入完了 — 繰り返し再生開始";
        await PlayEditorInternalAsync(loop: true);
    }

    // =========================================================
    //  エディタ: 簡単登録（色・OFF）
    // =========================================================

    /// <summary>
    /// 選択行がある場合はその後ろに挿入、なければ末尾に追加する。
    /// 挿入後、最後に追加した行を選択状態にする。
    /// </summary>
    private void InsertAfterSelectedOrAppend(int gapMs, params StepEditItem[] steps)
    {
        int baseMs;
        int insertAt;

        if (SelectedStep != null && !SelectedStep.IsPendingInsert)
        {
            var idx = EditSteps.IndexOf(SelectedStep);
            baseMs = SelectedStep.TimeOffsetMs + gapMs;
            insertAt = idx + 1;
        }
        else
        {
            baseMs = EditSteps.Count > 0 ? EditSteps[^1].TimeOffsetMs + gapMs : 0;
            insertAt = -1; // append
        }

        // 各ステップの時刻を baseMs 基準で調整（先頭ステップの時刻を baseMs とし、差分を維持）
        if (steps.Length > 0)
        {
            var firstMs = steps[0].TimeOffsetMs;
            foreach (var step in steps)
                step.TimeOffsetMs = baseMs + (step.TimeOffsetMs - firstMs);
        }

        _suppressJump = true;
        if (insertAt >= 0)
        {
            for (int i = 0; i < steps.Length; i++)
                EditSteps.Insert(insertAt + i, steps[i]);
        }
        else
        {
            foreach (var step in steps)
                EditSteps.Add(step);
        }
        SelectedStep = steps[^1];
        _suppressJump = false;
    }

    [RelayCommand]
    private void AddQuickColor()
    {
        if (TryFillPendingRow(step =>
        {
            step.CommandType = SequenceCommandType.Color;
            step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
        })) return;

        InsertAfterSelectedOrAppend(GapDurationMs,
            new StepEditItem { CommandType = SequenceCommandType.Color, R = 0xFF, G = 0xFF, B = 0xFF });
    }

    [RelayCommand]
    private void AddQuickOff()
    {
        if (TryFillPendingRow(step =>
        {
            step.CommandType = SequenceCommandType.Off;
            step.R = 0; step.G = 0; step.B = 0;
        })) return;

        InsertAfterSelectedOrAppend(GapDurationMs,
            new StepEditItem { CommandType = SequenceCommandType.Off, R = 0, G = 0, B = 0 });
    }

    // =========================================================
    //  エディタ: テンプレート追加
    // =========================================================

    [RelayCommand]
    private void AddTemplateColor()
    {
        if (TryFillPendingRow(step =>
        {
            step.CommandType = SequenceCommandType.Color;
            step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
        })) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem { CommandType = SequenceCommandType.Color, R = 0xFF, G = 0xFF, B = 0xFF });
    }

    [RelayCommand]
    private void AddTemplateFadeIn()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Effect;
                step.EffectType = SynchrolightAPI.Services.EffectType.FadeIn;
                step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
                step.EffectCycleDurationMs = 3000;
                step.FadeSteps = 20;
                step.Continuous = false;
            }
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem
            {
                CommandType = SequenceCommandType.Effect,
                EffectType = SynchrolightAPI.Services.EffectType.FadeIn,
                R = 0xFF, G = 0xFF, B = 0xFF,
                EffectCycleDurationMs = 3000,
                FadeSteps = 20,
                Continuous = false,
            });
    }

    [RelayCommand]
    private void AddTemplateFadeOut()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Effect;
                step.EffectType = SynchrolightAPI.Services.EffectType.FadeOut;
                step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
                step.EffectCycleDurationMs = 3000;
                step.FadeSteps = 20;
                step.Continuous = false;
            }
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem
            {
                CommandType = SequenceCommandType.Effect,
                EffectType = SynchrolightAPI.Services.EffectType.FadeOut,
                R = 0xFF, G = 0xFF, B = 0xFF,
                EffectCycleDurationMs = 3000,
                FadeSteps = 20,
                Continuous = false,
            });
    }

    [RelayCommand]
    private void AddTemplateFadeInOut()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Effect;
                step.EffectType = SynchrolightAPI.Services.EffectType.Breathing;
                step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
                step.EffectCycleDurationMs = 2000;
                step.FadeSteps = 20;
            },
            baseMs => [new StepEditItem
            {
                TimeOffsetMs = baseMs + 5000,
                CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0,
            }]
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem
            {
                CommandType = SequenceCommandType.Effect,
                EffectType = SynchrolightAPI.Services.EffectType.Breathing,
                R = 0xFF, G = 0xFF, B = 0xFF,
                EffectCycleDurationMs = 2000,
                FadeSteps = 20,
            },
            new StepEditItem { TimeOffsetMs = 5000, CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0 });
    }

    [RelayCommand]
    private void AddTemplateFlash()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Effect;
                step.EffectType = SynchrolightAPI.Services.EffectType.Flash;
                step.R = 0xFF; step.G = 0xFF; step.B = 0xFF;
                step.EffectCycleDurationMs = 500;
            },
            baseMs => [new StepEditItem
            {
                TimeOffsetMs = baseMs + 3000,
                CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0,
            }]
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem
            {
                CommandType = SequenceCommandType.Effect,
                EffectType = SynchrolightAPI.Services.EffectType.Flash,
                R = 0xFF, G = 0xFF, B = 0xFF,
                EffectCycleDurationMs = 500,
            },
            new StepEditItem { TimeOffsetMs = 3000, CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0 });
    }

    [RelayCommand]
    private void AddTemplateSevenColor()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Effect;
                step.EffectType = SynchrolightAPI.Services.EffectType.SevenColor;
                step.EffectCycleDurationMs = 7000;
                step.FadeSteps = 20;
            },
            baseMs => [new StepEditItem
            {
                TimeOffsetMs = baseMs + 14000,
                CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0,
            }]
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem
            {
                CommandType = SequenceCommandType.Effect,
                EffectType = SynchrolightAPI.Services.EffectType.SevenColor,
                EffectCycleDurationMs = 7000,
                FadeSteps = 20,
            },
            new StepEditItem { TimeOffsetMs = 14000, CommandType = SequenceCommandType.EffectStop, R = 0, G = 0, B = 0 });
    }

    [RelayCommand]
    private void AddTemplateCountdown()
    {
        if (TryFillPendingRow(
            step =>
            {
                step.CommandType = SequenceCommandType.Color;
                step.R = 0xFF; step.G = 0x00; step.B = 0x00;
            },
            baseMs =>
            [
                new StepEditItem { TimeOffsetMs = baseMs + 1000, CommandType = SequenceCommandType.Color, R = 0xFF, G = 0xFF, B = 0x00 },
                new StepEditItem { TimeOffsetMs = baseMs + 2000, CommandType = SequenceCommandType.Color, R = 0x00, G = 0xFF, B = 0x00 },
                new StepEditItem { TimeOffsetMs = baseMs + 3000, CommandType = SequenceCommandType.Off, R = 0, G = 0, B = 0 },
            ]
        )) return;

        InsertAfterSelectedOrAppend(1000,
            new StepEditItem { CommandType = SequenceCommandType.Color, R = 0xFF, G = 0x00, B = 0x00 },
            new StepEditItem { TimeOffsetMs = 1000, CommandType = SequenceCommandType.Color, R = 0xFF, G = 0xFF, B = 0x00 },
            new StepEditItem { TimeOffsetMs = 2000, CommandType = SequenceCommandType.Color, R = 0x00, G = 0xFF, B = 0x00 },
            new StepEditItem { TimeOffsetMs = 3000, CommandType = SequenceCommandType.Off, R = 0, G = 0, B = 0 });
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
                    Continuous = step.Continuous,
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
    //  煽りボタン（割り込み点灯）
    // =========================================================

    /// <summary>煽りボタン押下: Pause → 指定色で点灯</summary>
    public async Task HypeDownAsync(int index)
    {
        try
        {
            await _apiClient.PauseSequenceAsync();
            var (r, g, b) = index == 1
                ? (Hype1R, Hype1G, Hype1B)
                : (Hype2R, Hype2G, Hype2B);
            await _apiClient.SetGlobalColorAsync(0, r, g, b);
        }
        catch (HttpRequestException) { }
    }

    /// <summary>煽りボタン解放: Resume（一時停止していた再生を再開）</summary>
    public async Task HypeUpAsync()
    {
        try
        {
            await _apiClient.ResumeSequenceAsync();
        }
        catch (HttpRequestException) { }
    }

    /// <summary>煽りボタンのプリセット色を設定</summary>
    public void SetHypePresetColor(int index, string preset)
    {
        var (r, g, b) = preset switch
        {
            "Red" => ((byte)255, (byte)0, (byte)0),
            "Green" => ((byte)0, (byte)255, (byte)0),
            "Blue" => ((byte)0, (byte)0, (byte)255),
            "White" => ((byte)255, (byte)255, (byte)255),
            _ => ((byte)255, (byte)255, (byte)255),
        };
        if (index == 1) { Hype1R = r; Hype1G = g; Hype1B = b; }
        else { Hype2R = r; Hype2G = g; Hype2B = b; }
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
                Continuous = e.Continuous,
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
                Continuous = step.Continuous,
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
    [ObservableProperty] private bool _continuous = true;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isPendingInsert;

    /// <summary>時刻の秒表示（読み取り専用）</summary>
    public string TimeLabel => $"{TimeOffsetMs / 1000.0:F1}s";

    partial void OnTimeOffsetMsChanged(int value)
    {
        OnPropertyChanged(nameof(TimeLabel));
    }
}
