using System.Collections.Specialized;
using System.Windows.Controls;

namespace SynchrolightAPI.Wpf.Views;

public partial class SendLogPanel : UserControl
{
    public SendLogPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (LogListView.ItemsSource is INotifyCollectionChanged col)
            {
                col.CollectionChanged += (_, _) =>
                {
                    if (LogListView.Items.Count > 0)
                        LogListView.ScrollIntoView(LogListView.Items[^1]);
                };
            }
        };
    }
}
