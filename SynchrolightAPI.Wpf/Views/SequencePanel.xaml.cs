using System.Windows.Controls;
using System.Windows.Input;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf.Views;

public partial class SequencePanel : UserControl
{
    public SequencePanel()
    {
        InitializeComponent();
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
