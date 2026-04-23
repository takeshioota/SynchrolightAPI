using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynchrolightAPI.Wpf.Models;
using SynchrolightAPI.Wpf.Services;

namespace SynchrolightAPI.Wpf.ViewModels;

public partial class BleIdPanelViewModel : ObservableObject
{
    private readonly BleIdService _ble;

    public ObservableCollection<BleDeviceItem> Devices { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private BleDeviceItem? _selectedDevice;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "待機中";

    // 書き込みパラメータ
    [ObservableProperty] private int _writeSession = 1;
    [ObservableProperty] private int _writeStartRow = 1;
    [ObservableProperty] private int _writeStartCol = 1;
    [ObservableProperty] private int _writeIncrement = 1;
    [ObservableProperty] private bool _incrementByRow = true; // true=行増分, false=列増分

    public BleIdPanelViewModel(BleIdService ble)
    {
        _ble = ble;
    }

    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Log.Add(line);
        else
            Application.Current?.Dispatcher.BeginInvoke(() => Log.Add(line));
    }

    // ── スキャン ──
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusText = "スキャン中...";
        Devices.Clear();
        AddLog("BLEスキャン開始 (8秒)");

        try
        {
            var found = await _ble.ScanAsync(TimeSpan.FromSeconds(8), ct);
            foreach (var d in found)
            {
                Devices.Add(new BleDeviceItem
                {
                    Address = d.Address,
                    Name = d.Name,
                    Rssi = d.Rssi,
                    Status = "検出済"
                });
            }
            AddLog($"{found.Count}台のBLELight検出");
            StatusText = $"{found.Count}台 検出";
        }
        catch (OperationCanceledException)
        {
            AddLog("スキャンをキャンセルしました");
            StatusText = "キャンセル";
        }
        catch (Exception ex)
        {
            AddLog($"スキャンエラー: {ex.Message}");
            StatusText = "スキャンエラー";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 一括ID読み取り ──
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ReadAllAsync(CancellationToken ct)
    {
        if (Devices.Count == 0) { AddLog("先にスキャンしてください"); return; }

        IsBusy = true;
        StatusText = "一括読み取り中...";
        AddLog($"一括ID読み取り開始 ({Devices.Count}台)");

        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var dev = Devices[i];
                dev.Status = "読み取り中...";

                try
                {
                    var id = await _ble.ReadIdAsync(dev.Address, ct);
                    if (id != null)
                    {
                        dev.Session = id.Session;
                        dev.Row = id.Row;
                        dev.Col = id.Col;
                        dev.Status = "読み取りOK";
                        ok++;
                        AddLog($"  [{i + 1}/{Devices.Count}] {dev.AddressHex} -> セッション={id.Session} 行={id.Row} 列={id.Col}");
                    }
                    else
                    {
                        dev.Status = "応答なし";
                        fail++;
                        AddLog($"  [{i + 1}/{Devices.Count}] {dev.AddressHex} -> 応答なし");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    dev.Status = "エラー";
                    fail++;
                    AddLog($"  [{i + 1}/{Devices.Count}] {dev.AddressHex} -> エラー: {ex.Message}");
                }

                if (i < Devices.Count - 1)
                    await Task.Delay(500, CancellationToken.None);
            }
        }
        finally
        {
            AddLog($"一括読み取り完了: {ok}台成功 / {fail}台失敗");
            StatusText = $"読み取り完了: {ok}/{Devices.Count}";
            IsBusy = false;
        }
    }

    // ── 一括書き込み ──
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task WriteAllAsync(CancellationToken ct)
    {
        if (Devices.Count == 0) { AddLog("先にスキャンしてください"); return; }

        IsBusy = true;
        var count = Devices.Count;
        StatusText = "一括書き込み中...";
        var direction = IncrementByRow ? "行" : "列";
        AddLog($"一括書き込み開始: セッション={WriteSession} 開始行={WriteStartRow} 開始列={WriteStartCol} {direction}増分={WriteIncrement} ({count}台)");

        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var dev = Devices[i];
                var row = IncrementByRow ? WriteStartRow + i * WriteIncrement : WriteStartRow;
                var col = IncrementByRow ? WriteStartCol : WriteStartCol + i * WriteIncrement;
                StatusText = $"書き込み中... [{i + 1}/{count}]";
                dev.Status = $"書き込み中 (行={row} 列={col})...";

                try
                {
                    var (writeOk, readBack) = await _ble.WriteIdAsync(dev.Address, WriteSession, row, col, ct);
                    if (writeOk && readBack != null &&
                        readBack.Session == WriteSession && readBack.Row == row && readBack.Col == col)
                    {
                        dev.Session = readBack.Session;
                        dev.Row = readBack.Row;
                        dev.Col = readBack.Col;
                        dev.Status = "書き込みOK";
                        ok++;
                        AddLog($"  [{i + 1}/{count}] {dev.AddressHex} -> 行={row} 列={col} 成功");
                    }
                    else if (writeOk)
                    {
                        dev.Session = WriteSession;
                        dev.Row = row;
                        dev.Col = col;
                        dev.Status = "応答OK (照会未確認)";
                        ok++;
                        AddLog($"  [{i + 1}/{count}] {dev.AddressHex} -> 行={row} 列={col} 応答OK");
                    }
                    else
                    {
                        dev.Status = "書き込み失敗";
                        fail++;
                        AddLog($"  [{i + 1}/{count}] {dev.AddressHex} -> 行={row} 列={col} 書き込み応答なし");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    dev.Status = "エラー";
                    fail++;
                    AddLog($"  [{i + 1}/{count}] {dev.AddressHex} -> 行={row} 列={col} エラー: {ex.Message}");
                }

                if (i < count - 1)
                    await Task.Delay(800, CancellationToken.None);
            }
        }
        finally
        {
            AddLog($"一括書き込み完了: {ok}台成功 / {fail}台失敗");
            StatusText = $"書き込み完了: {ok}/{count}";
            IsBusy = false;
        }
    }

    // ── 赤色点灯 ──
    [RelayCommand]
    private async Task LightRedAsync()
    {
        if (SelectedDevice == null) { AddLog("デバイスを選択してください"); return; }

        var dev = SelectedDevice;
        AddLog($"赤色点灯: {dev.AddressHex}");

        try
        {
            var ok = await _ble.LightRedAsync(dev.Address);
            if (ok)
            {
                dev.Status = "赤色点灯";
                AddLog($"  {dev.AddressHex} -> 赤色点灯OK");
            }
            else
            {
                AddLog($"  {dev.AddressHex} -> 接続失敗");
            }
        }
        catch (Exception ex)
        {
            AddLog($"  {dev.AddressHex} -> エラー: {ex.Message}");
        }
    }

    // ── ログクリア ──
    [RelayCommand]
    private void ClearLog() => Log.Clear();
}
