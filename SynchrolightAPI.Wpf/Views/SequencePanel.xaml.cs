using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf.Views;

public partial class SequencePanel : UserControl
{
    private bool _isEditingCell;

    public SequencePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (SeqLogListView.ItemsSource is INotifyCollectionChanged col)
            {
                col.CollectionChanged += (_, _) =>
                {
                    if (SeqLogListView.Items.Count > 0)
                        SeqLogListView.ScrollIntoView(SeqLogListView.Items[^1]);
                };
            }

            // 挿入モード中、選択行が変わったらスクロール追従
            StepDataGrid.SelectionChanged += (_, _) =>
            {
                if (DataContext is SequencePanelViewModel vm
                    && vm.IsInsertMode
                    && StepDataGrid.SelectedItem != null)
                {
                    StepDataGrid.ScrollIntoView(StepDataGrid.SelectedItem);
                }
            };
        };
    }

    private void StepDataGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        _isEditingCell = true;
    }

    private void StepDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        _isEditingCell = false;

        // 手動セル編集でペンディング行を確定（空行→実データになる）
        if (e.EditAction == DataGridEditAction.Commit
            && e.Row.DataContext is StepEditItem editedStep
            && editedStep.IsPendingInsert)
        {
            editedStep.IsPendingInsert = false;
        }
    }

    private async void StepDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isEditingCell)
        {
            e.Handled = true;
            if (DataContext is SequencePanelViewModel vm)
            {
                if (vm.IsPlayingInline)
                    await vm.StopEditorCommand.ExecuteAsync(null);
                else if (vm.IsPlaying)
                    await vm.StopCommand.ExecuteAsync(null);
            }
            return;
        }

        if (e.Key != Key.Enter) return;
        if (_isEditingCell) return;

        e.Handled = true;

        if (DataContext is SequencePanelViewModel vm2)
        {
            await vm2.PlayStepAndAdvanceCommand.ExecuteAsync(null);

            if (vm2.SelectedStep != null)
                StepDataGrid.ScrollIntoView(vm2.SelectedStep);
        }
    }

    private void ColorPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not System.Windows.Controls.Border border) return;
        if (border.DataContext is not StepEditItem step) return;

        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(step.R, step.G, step.B),
            FullOpen = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            step.R = dlg.Color.R;
            step.G = dlg.Color.G;
            step.B = dlg.Color.B;
        }

        e.Handled = true;
    }

    // =========================================================
    //  Esc停止（UserControlレベル）
    // =========================================================

    private async void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (DataContext is SequencePanelViewModel vm)
        {
            if (vm.IsPlayingInline)
            {
                e.Handled = true;
                await vm.StopEditorCommand.ExecuteAsync(null);
            }
            else if (vm.IsPlaying)
            {
                e.Handled = true;
                await vm.StopCommand.ExecuteAsync(null);
            }
        }
    }

    // =========================================================
    //  煽りボタン（割り込み点灯）
    // =========================================================

    private SequencePanelViewModel? Vm => DataContext as SequencePanelViewModel;

    // --- 煽り1 ---
    private async void Hype1_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm != null) await Vm.HypeDownAsync(1);
        (sender as UIElement)?.CaptureMouse();
    }

    private async void Hype1_MouseUp(object sender, MouseButtonEventArgs e)
    {
        (sender as UIElement)?.ReleaseMouseCapture();
        if (Vm != null) await Vm.HypeUpAsync();
    }

    // --- 煽り2 ---
    private async void Hype2_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm != null) await Vm.HypeDownAsync(2);
        (sender as UIElement)?.CaptureMouse();
    }

    private async void Hype2_MouseUp(object sender, MouseButtonEventArgs e)
    {
        (sender as UIElement)?.ReleaseMouseCapture();
        if (Vm != null) await Vm.HypeUpAsync();
    }

    // --- 煽りカラーピッカー ---
    private void Hype1Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(Vm.Hype1R, Vm.Hype1G, Vm.Hype1B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            Vm.Hype1R = dlg.Color.R;
            Vm.Hype1G = dlg.Color.G;
            Vm.Hype1B = dlg.Color.B;
        }
        e.Handled = true;
    }

    private void Hype2Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(Vm.Hype2R, Vm.Hype2G, Vm.Hype2B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            Vm.Hype2R = dlg.Color.R;
            Vm.Hype2G = dlg.Color.G;
            Vm.Hype2B = dlg.Color.B;
        }
        e.Handled = true;
    }

    // --- 煽りプリセット色 ---
    private void Hype1Preset_Red(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(1, "Red");
    private void Hype1Preset_Green(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(1, "Green");
    private void Hype1Preset_Blue(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(1, "Blue");
    private void Hype1Preset_White(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(1, "White");
    private void Hype2Preset_Red(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(2, "Red");
    private void Hype2Preset_Green(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(2, "Green");
    private void Hype2Preset_Blue(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(2, "Blue");
    private void Hype2Preset_White(object sender, System.Windows.RoutedEventArgs e) => Vm?.SetHypePresetColor(2, "White");
}
