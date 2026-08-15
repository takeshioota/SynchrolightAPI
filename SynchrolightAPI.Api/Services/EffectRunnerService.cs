using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Services;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// エフェクト/シーケンスのバックグラウンド実行を管理するシングルトン。
/// HTTP リクエスト間で CancellationTokenSource と実行状態を保持する。
/// </summary>
public class EffectRunnerService
{
    private readonly EffectEngine _effectEngine;
    private readonly EffectScheduler _scheduler;
    private readonly SequencePlayer _sequencePlayer;
    private readonly SequenceStore _sequenceStore;
    private readonly ITransport _transport;
    private readonly ILogger<EffectRunnerService> _logger;
    private readonly object _lock = new();

    // Effect state
    private CancellationTokenSource? _effectCts;
    private EffectParams? _currentEffect;
    private Task? _effectTask;

    // Sequence state
    private CancellationTokenSource? _sequenceCts;
    private string? _currentSequenceName;
    private Task? _sequenceTask;

    // Inline sequence state (ジャンプ用に保持)
    private Sequence? _inlineSequence;

    // Paused sequence state (割り込み点灯からの再開用)
    private string? _pausedSequenceName;
    private Sequence? _pausedInlineSequence;
    private int _pausedStepIndex = -1;
    // 一時停止時に「現ステップで既に経過していた時間（ms）」を保存して、
    // 再開時にステップの途中位置から続きを再生できるようにする。
    private int _pausedElapsedMsInStep;


    public EffectRunnerService(
        EffectEngine effectEngine,
        EffectScheduler scheduler,
        SequencePlayer sequencePlayer,
        SequenceStore sequenceStore,
        ITransport transport,
        ILogger<EffectRunnerService> logger)
    {
        _effectEngine = effectEngine;
        _scheduler = scheduler;
        _sequencePlayer = sequencePlayer;
        _sequenceStore = sequenceStore;
        _transport = transport;
        _logger = logger;
    }

    /// <summary>エフェクトを開始する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartEffect(EffectParams p)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = p;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _effectEngine.RunAsync(p, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "エフェクトタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: エフェクト開始 {Type}", p.Type);
    }

    /// <summary>指定色を連続送信する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartColorHold(byte field, Rgb color)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _scheduler.SendContinuousColorAsync(field, color, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "カラーホールドタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: カラーホールド開始 ({R},{G},{B})", color.R, color.G, color.B);
    }

    /// <summary>
    /// from→to へ duration かけて線形補間フェードし、完了後は to を連続送信で保持する。
    /// 実行中のエフェクト/シーケンスは自動停止。色→色のスムーズ遷移（UI/シーケンス共通の堅牢経路）に使用。
    /// SendInterpolationAsync により ~20ms/フレーム(≈50fps) で送信し、各フレーム保持中も再送するため
    /// パケットロスに強い。旧来の「30ms・1発送信」方式のカクつき/不安定を解消するための統一実装。
    /// </summary>
    public void StartLinearFade(byte field, Rgb from, Rgb to, int durationMs, int stepCount)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    var effectCt = _scheduler.BeginEffect(cts.Token);
                    await _scheduler.SendInterpolationAsync(
                        field, from, to, stepCount,
                        TimeSpan.FromMilliseconds(Math.Max(1, durationMs)), effectCt);
                    // 遷移完了後は最終色を確実に発色させ、以後は連続送信で保持（セルフモード突入防止）。
                    await _scheduler.LatchColorHighPriorityAsync(field, to, effectCt);
                    await _scheduler.ContinuousSendWithoutResetAsync(field, to, effectCt);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "リニアフェードタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: リニアフェード開始 ({FR},{FG},{FB})→({TR},{TG},{TB}) {Ms}ms",
            from.R, from.G, from.B, to.R, to.G, to.B, durationMs);
    }

    /// <summary>任意パケットを連続送信する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartPacketHold(byte[] packet)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _scheduler.SendContinuousPacketAsync(packet, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "パケットホールドタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: パケットホールド開始");
    }

    /// <summary>
    /// SNO端末の内蔵プログラムを再生する（A1コマンド送信）。
    /// 実行中のエフェクト/シーケンスは自動停止。
    /// 内蔵プログラムは端末側で自律動作するため、PC側はA1パケットを連続送信して
    /// 端末がセルフモードに戻らないようにする。
    /// </summary>
    public void StartInternalProgram(uint frameNo)
    {
        var packet = LightProtocol.BuildA1_PlaySequence(frameNo);
        StartPacketHold(packet);
        _logger.LogInformation("API: 内蔵プログラム再生開始 frame={FrameNo}", frameNo);
    }

    // --- Rainbow（V4.5: 3.15-3.22）---

    /// <summary>
    /// レインボーエフェクトを開始する。
    /// 1. カラーパレット送信（0xA9 0x02）
    /// 2. モードに応じた 0xA9 0x03 を colorFrameNo をサイクルしながら継続送信
    /// </summary>
    public void StartRainbow(
        int mode,
        (byte r, byte g, byte b)[] colors,
        int cycleDurationMs,
        int? blinkPeriodMs = null,
        int? dutyRatio = null,
        int? fadeInMs = null,
        int? fadeOutMs = null)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await RunRainbowLoopAsync(
                        mode, colors, cycleDurationMs,
                        blinkPeriodMs, dutyRatio, fadeInMs, fadeOutMs,
                        cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Rainbowタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: Rainbow開始 mode={Mode}, colors={N}, cycle={Cycle}ms",
            mode, colors.Length, cycleDurationMs);
    }

    /// <summary>
    /// 色テーブルのみ送信する（0xA9 0x02: 3.15 色テーブル定義）。
    /// モード開始（0xA9 0x03）は送信しない。
    /// </summary>
    public async Task SendColorTableAsync(
        (byte r, byte g, byte b)[] colors,
        CancellationToken ct = default)
    {
        var packet = LightProtocol.BuildA9_SetRainbowColors(colors);
        await _transport.EnqueueAsync(packet, ct);
        _logger.LogInformation("API: 色テーブル送信 colors={N}", colors.Length);
    }

    /// <summary>
    /// 7色ランダム一時停止（0xA9 0x04）を継続送信する。
    /// </summary>
    public void PauseRainbow()
    {
        var packet = LightProtocol.BuildA9_RainbowPause();
        StartPacketHold(packet);
        _logger.LogInformation("API: Rainbow一時停止（前回の色を保持）");
    }

    /// <summary>
    /// レインボーの継続送信ループ。
    /// カラー設定 → colorFrameNo をサイクルしながらモード別コマンドを 20ms 間隔で送信。
    /// cycleDurationMs ごとに colorFrameNo を進める。
    /// </summary>
    private async Task RunRainbowLoopAsync(
        int mode,
        (byte r, byte g, byte b)[] colors,
        int cycleDurationMs,
        int? blinkPeriodMs,
        int? dutyRatio,
        int? fadeInMs,
        int? fadeOutMs,
        CancellationToken ct)
    {
        var effectCt = _scheduler.BeginEffect(ct);
        int colorCount = colors.Length;

        // Step 1: カラーパレット送信（0xA9 0x02）
        var colorSetupPacket = LightProtocol.BuildA9_SetRainbowColors(colors);
        await _transport.EnqueueAsync(colorSetupPacket, effectCt);
        await Task.Delay(50, effectCt); // パレット設定の反映待ち

        // Step 2: モード別コマンドを colorFrameNo サイクルしながら継続送信
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte currentFrame = 0;

        while (!effectCt.IsCancellationRequested)
        {
            // colorFrameNo を cycleDurationMs ごとに進める
            if (cycleDurationMs > 0)
            {
                var elapsed = sw.ElapsedMilliseconds;
                currentFrame = (byte)(elapsed / cycleDurationMs % colorCount);
            }

            // モード別パケット生成
            byte[] packet = mode switch
            {
                0 => LightProtocol.BuildA9_RainbowSolid(currentFrame),
                1 => LightProtocol.BuildA9_RainbowBlink(
                         currentFrame,
                         (ushort)Math.Clamp(blinkPeriodMs ?? 500, 100, 3600),
                         (byte)Math.Clamp(dutyRatio ?? 5, 1, 9)),
                2 => LightProtocol.BuildA9_RainbowFadeInOut(
                         currentFrame,
                         (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000),
                         (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                3 => LightProtocol.BuildA9_RainbowFadeIn(
                         currentFrame,
                         (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000)),
                4 => LightProtocol.BuildA9_RainbowFadeOut(
                         currentFrame,
                         (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                5 => LightProtocol.BuildA9_RainbowRandom(currentFrame),
                _ => LightProtocol.BuildA9_RainbowSolid(currentFrame),
            };

            await _transport.EnqueueAsync(packet, effectCt);
            await Task.Delay(20, effectCt); // 20ms 間隔
        }
    }

    // --- ファイル書き込み（2.4GHz: 3.13-3.14）---

    /// <summary>
    /// 2.4GHz経由でRGBファイルデータを端末に書き込む。
    /// 1. A9開始コマンド送信（データ長通知）
    /// 2. rgbDataを6アドレスずつ分割してA7で送信
    /// </summary>
    public async Task WriteFileVia24GAsync(byte[] rgbData, uint frameNo, CancellationToken ct = default)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();
        }

        _logger.LogInformation("API: 2.4Gファイル書き込み開始 frame={FrameNo}, size={Size}bytes",
            frameNo, rgbData.Length);

        // Step 1: A9 開始コマンド（データ長通知）
        var startPacket = LightProtocol.BuildA9_FileWriteStart((ushort)rgbData.Length);
        await _transport.EnqueueAsync(startPacket, ct);
        await Task.Delay(100, ct); // 端末の準備待ち

        // Step 2: A7 データ書き込み（6アドレスずつ分割）
        // RGBデータは3バイトずつ = 1アドレス分のRGBデータ
        int totalAddresses = rgbData.Length / 3;
        int packetsSent = 0;

        for (int addr = 0; addr < totalAddresses; addr += 6)
        {
            ct.ThrowIfCancellationRequested();

            int remaining = Math.Min(6, totalAddresses - addr);
            var colors = new (byte r, byte g, byte b)[remaining];

            for (int i = 0; i < remaining; i++)
            {
                int offset = (addr + i) * 3;
                colors[i] = (rgbData[offset], rgbData[offset + 1], rgbData[offset + 2]);
            }

            // アドレスから行・列を計算（仮: addr = row * maxCol + col）
            ushort row = (ushort)(addr / 256 + 1);
            ushort col = (ushort)(addr % 256 + 1);

            var dataPacket = LightProtocol.BuildA7_FileWriteData(
                row, col, (byte)remaining, frameNo, colors);
            await _transport.EnqueueAsync(dataPacket, ct);

            packetsSent++;

            // 送信間隔（端末のflash書き込み待ち）
            if (packetsSent % 20 == 0)
            {
                await Task.Delay(500, ct); // 20パケットごとに500ms待機
            }
            else
            {
                await Task.Delay(20, ct);
            }
        }

        _logger.LogInformation("API: 2.4Gファイル書き込み完了 frame={FrameNo}, packets={Packets}",
            frameNo, packetsSent);
    }

    /// <summary>実行中のエフェクトを停止する。</summary>
    public void StopEffect()
    {
        lock (_lock)
        {
            StopEffectInternal();
        }
    }

    /// <summary>エフェクトの実行状態を取得する。</summary>
    public (bool IsRunning, EffectParams? Current) GetEffectStatus()
    {
        lock (_lock)
        {
            bool running = _effectTask != null && !_effectTask.IsCompleted;
            return (running, running ? _currentEffect : null);
        }
    }

    /// <summary>シーケンスを開始する。実行中のエフェクト/シーケンスは自動停止。</summary>
    /// <param name="name">保存済みシーケンス名</param>
    /// <param name="startFromIndex">再生開始ステップインデックス（既定 0）</param>
    /// <param name="initialElapsedMsInStartStep">開始ステップでの初期経過時間（途中再開用、既定 0）</param>
    /// <returns>シーケンスが見つからない場合 false</returns>
    public bool StartSequence(string name, int startFromIndex = 0, int initialElapsedMsInStartStep = 0)
    {
        var sequence = _sequenceStore.Load(name);
        if (sequence == null) return false;

        lock (_lock)
        {
            StopEffectInternal();
            StopSequenceInternal();
            _inlineSequence = null;
            ClearPausedStateInternal();

            var cts = new CancellationTokenSource();
            _sequenceCts = cts;
            _currentSequenceName = name;

            _sequenceTask = Task.Run(async () =>
            {
                try
                {
                    await _sequencePlayer.PlayAsync(sequence, cts.Token, startFromIndex, loop: false, initialElapsedMsInStartStep);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "シーケンスタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_sequenceCts == cts)
                        {
                            _currentSequenceName = null;
                            _sequenceTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: シーケンス再生開始 {Name} (開始ステップ={Start}, 初期経過={Elapsed}ms)",
            name, startFromIndex, initialElapsedMsInStartStep);
        return true;
    }

    /// <summary>
    /// 実行中のシーケンスを一時停止する。停止位置（ステップインデックス・現ステップ内経過時間・
    /// シーケンス参照）を内部に保存して、後続の ResumeSequence で続きから再開できるようにする。
    /// </summary>
    /// <returns>一時停止できた場合 true、再生中シーケンスがない場合 false</returns>
    public bool PauseSequence()
    {
        lock (_lock)
        {
            if (_sequenceTask == null || _sequenceTask.IsCompleted)
            {
                return false;
            }

            // 現在の状態を保存（ステップ index と現ステップで経過した時間の両方）
            _pausedSequenceName = _currentSequenceName;
            _pausedInlineSequence = _inlineSequence;
            _pausedStepIndex = _sequencePlayer.CurrentStepIndex;
            _pausedElapsedMsInStep = _sequencePlayer.CurrentStepElapsedMs;

            // 再生を停止（保存した状態は残る）
            StopSequenceInternal();
        }

        _logger.LogInformation("API: シーケンス一時停止 (step={Index}, 経過={Elapsed}ms)",
            _pausedStepIndex, _pausedElapsedMsInStep);
        return true;
    }

    /// <summary>
    /// PauseSequence で保存した位置（ステップ index ＋ 現ステップ内経過時間）から
    /// シーケンス再生を再開する。再開対象がない場合は false を返す。
    /// </summary>
    public bool ResumeSequence()
    {
        string? name;
        Sequence? inlineSeq;
        int startIdx;
        int elapsedMs;

        lock (_lock)
        {
            name = _pausedSequenceName;
            inlineSeq = _pausedInlineSequence;
            startIdx = Math.Max(0, _pausedStepIndex);
            elapsedMs = Math.Max(0, _pausedElapsedMsInStep);

            _pausedSequenceName = null;
            _pausedInlineSequence = null;
            _pausedStepIndex = -1;
            _pausedElapsedMsInStep = 0;
        }

        if (name != null)
        {
            var ok = StartSequence(name, startIdx, elapsedMs);
            if (ok)
            {
                _logger.LogInformation("API: シーケンス再開 {Name} (step={Index}, 経過={Elapsed}ms)",
                    name, startIdx, elapsedMs);
            }
            return ok;
        }
        if (inlineSeq != null)
        {
            StartSequenceInline(inlineSeq, startIdx, loop: false, elapsedMs);
            _logger.LogInformation("API: インラインシーケンス再開 (step={Index}, 経過={Elapsed}ms)",
                startIdx, elapsedMs);
            return true;
        }

        return false;
    }

    /// <summary>エディタ内容をインライン再生する（連続再生）。</summary>
    public void StartSequenceInline(Sequence sequence, int startFromIndex = 0, bool loop = false, int initialElapsedMsInStartStep = 0)
    {
        lock (_lock)
        {
            StopEffectInternal();
            StopSequenceInternal();

            _inlineSequence = sequence;
            _currentSequenceName = null;
            ClearPausedStateInternal();

            var cts = new CancellationTokenSource();
            _sequenceCts = cts;

            _sequenceTask = Task.Run(async () =>
            {
                try
                {
                    await _sequencePlayer.PlayAsync(sequence, cts.Token, startFromIndex, loop, initialElapsedMsInStartStep);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "インラインシーケンスタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_sequenceCts == cts)
                        {
                            _sequenceTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: インラインシーケンス再生開始 (開始={Start})", startFromIndex);
    }

    /// <summary>単一ステップを即時実行する（ステップ再生）。</summary>
    public Task PlaySingleStepAsync(SequenceStep step)
    {
        if (step.CommandType == SequenceCommandType.Effect && step.EffectType.HasValue)
        {
            var effectParams = new EffectParams(
                Type: step.EffectType.Value,
                Color: new Rgb(step.R, step.G, step.B),
                Field: step.Field,
                CycleDuration: step.EffectCycleDurationMs.HasValue
                    ? TimeSpan.FromMilliseconds(step.EffectCycleDurationMs.Value)
                    : null,
                FlashInterval: step.EffectType.Value == EffectType.Flash && step.EffectCycleDurationMs.HasValue
                    ? TimeSpan.FromMilliseconds(step.EffectCycleDurationMs.Value / 2)
                    : null,
                FadeSteps: step.FadeSteps ?? 20,
                Continuous: true
            );
            StartEffect(effectParams);
        }
        else if (step.CommandType == SequenceCommandType.EffectStop)
        {
            lock (_lock)
            {
                StopEffectInternal();
                StopSequenceInternal();
                _inlineSequence = null;
            }
            _logger.LogDebug("ステップ再生: EffectStop");
        }
        else if (step.CommandType == SequenceCommandType.InternalProgram && step.FrameNo.HasValue)
        {
            StartInternalProgram(step.FrameNo.Value);
        }
        else if (step.CommandType is SequenceCommandType.Color or SequenceCommandType.Off)
        {
            var color = step.CommandType == SequenceCommandType.Off
                ? Rgb.Black
                : new Rgb(step.R, step.G, step.B);
            StartColorHold(step.Field, color);
        }
        else if (step.CommandType == SequenceCommandType.Rainbow)
        {
            var colors = (step.RainbowColors != null && step.RainbowColors.Count > 0)
                ? step.RainbowColors.Select(c => (c.R, c.G, c.B)).ToArray()
                : new (byte, byte, byte)[] { (0xFF, 0x00, 0x00), (0x00, 0x00, 0xFF) };
            StartRainbow(
                step.RainbowMode ?? 0, colors, step.RainbowCycleDurationMs ?? 1000,
                step.RainbowBlinkPeriodMs, step.RainbowDutyRatio,
                step.RainbowFadeInMs, step.RainbowFadeOutMs);
        }
        else if (step.CommandType == SequenceCommandType.RainbowStop)
        {
            StopEffect();
            _logger.LogDebug("ステップ再生: RainbowStop");
        }
        else if (step.CommandType == SequenceCommandType.RainbowPause)
        {
            PauseRainbow();
        }

        return Task.CompletedTask;
    }

    /// <summary>再生中にジャンプする。</summary>
    /// <returns>ジャンプ成功の場合 true</returns>
    public bool JumpToStep(int stepIndex)
    {
        lock (_lock)
        {
            if (_inlineSequence == null) return false;

            var sortedCount = _inlineSequence.Steps.Count;
            if (stepIndex < 0 || stepIndex >= sortedCount) return false;
        }

        // lock外で呼ぶ（StartSequenceInline内部でlockを取得するため）
        StartSequenceInline(_inlineSequence!, stepIndex);
        _logger.LogInformation("API: ジャンプ → ステップ {Index}", stepIndex);
        return true;
    }

    /// <summary>実行中のシーケンスを停止する。</summary>
    public void StopSequence()
    {
        lock (_lock)
        {
            StopSequenceInternal();
            _inlineSequence = null;
            ClearPausedStateInternal();
        }
    }

    /// <summary>シーケンスの再生状態を取得する（ステップ情報付き）。</summary>
    public (bool IsPlaying, string? Name, int CurrentStepIndex, int TotalStepCount) GetSequenceStatus()
    {
        lock (_lock)
        {
            bool playing = _sequenceTask != null && !_sequenceTask.IsCompleted;
            return (
                playing,
                playing ? _currentSequenceName : null,
                _sequencePlayer.CurrentStepIndex,
                _sequencePlayer.TotalStepCount
            );
        }
    }

    // --- internal helpers (caller must hold _lock) ---

    private void StopEffectInternal()
    {
        if (_effectCts != null)
        {
            _effectCts.Cancel();
            _effectCts.Dispose();
            _effectCts = null;
        }
        _scheduler.Abort();
        _currentEffect = null;
        _effectTask = null;
    }

    private void StopSequenceInternal()
    {
        if (_sequenceCts != null)
        {
            _sequenceCts.Cancel();
            _sequenceCts.Dispose();
            _sequenceCts = null;
        }
        _sequencePlayer.FlushAndStop();
        _currentSequenceName = null;
        _sequenceTask = null;
    }

    private void ClearPausedStateInternal()
    {
        _pausedSequenceName = null;
        _pausedInlineSequence = null;
        _pausedStepIndex = -1;
        _pausedElapsedMsInStep = 0;
    }
}
