using System;
using System.Windows;
using System.Windows.Controls;

namespace UntamedManager;

public partial class MainWindow
{
    // WPF uses device-independent pixels: a 1366px 150% DPI desktop may
    // expose fewer than 930 logical pixels. Stack the operational details
    // below the map table instead of compressing columns into unreadable text.
    private static StackPanel GraphiteField(string caption,UIElement control)
    {
        var stack=new StackPanel {Margin=new Thickness(0,0,10,5)};
        stack.Children.Add(new TextBlock {
            Text=caption,FontSize=10,FontWeight=FontWeights.SemiBold,
            Foreground=Soft,Margin=new Thickness(0,0,0,5)
        });
        stack.Children.Add(control);
        return stack;
    }

    private bool _dashboardCompactLayout;

    private void DashboardColumns_SizeChanged(object sender,SizeChangedEventArgs args)
    {
        if(sender is not Grid grid || DashboardServerPane is null ||
           DashboardSidePane is null)return;
        bool compact=args.NewSize.Width<950;
        if(compact==_dashboardCompactLayout)return;
        _dashboardCompactLayout=compact;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if(compact)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition {
                Width=new GridLength(1,GridUnitType.Star)
            });
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            Grid.SetColumn(DashboardServerPane,0);
            Grid.SetRow(DashboardServerPane,0);
            Grid.SetColumn(DashboardSidePane,0);
            Grid.SetRow(DashboardSidePane,1);
            DashboardServerPane.Margin=new Thickness(0,0,0,12);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition {
                Width=new GridLength(7,GridUnitType.Star),MinWidth=405
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition {
                Width=new GridLength(3,GridUnitType.Star),MinWidth=270
            });
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            Grid.SetColumn(DashboardServerPane,0);
            Grid.SetRow(DashboardServerPane,0);
            Grid.SetColumn(DashboardSidePane,1);
            Grid.SetRow(DashboardSidePane,0);
            DashboardServerPane.Margin=new Thickness(0,0,12,0);
        }
    }
}
