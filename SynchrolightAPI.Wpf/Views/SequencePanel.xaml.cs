using System.Collections.Specialized;
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
        };
    }

    private void StepDataGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        _isEditingCell = true;
    }

    private void StepDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        _isEditingCell = false;
    }

    private async void StepDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_isEditingCell) return;

        e.Handled = true;

        if (DataContext is SequencePanelViewModel vm)
        {
            await vm.PlayStepAndAdvanceCommand.ExecuteAsync(null);

            if (vm.SelectedStep != null)
                StepDataGrid.ScrollIntoView(vm.SelectedStep);
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
}
