using System;
using System.Windows;
using System.Windows.Controls;

namespace UntamedManager;

public partial class MainWindow
{
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // WPF widths here are logical DIPs. Do not hide content off-screen at 150% DPI.
        double width=e.NewSize.Width;
        SidebarColumn.Width=new GridLength(width<1080?184:224);
        VersionContainer.Visibility=width<940?Visibility.Collapsed:Visibility.Visible;
        SupervisorContainer.Visibility=width<1180?Visibility.Collapsed:Visibility.Visible;
        DashboardHeroActions.Visibility=width<1160?Visibility.Collapsed:Visibility.Visible;
        DashboardKpis.Columns=width<1180?2:4;
    }

    /// <summary>
    /// Live Mod and Plugin Centers share one responsive 70/30 work area.
    /// At tablet/150%-DPI widths the existing details panel moves below the
    /// scrollable inventory instead of being clipped or forced off screen.
    /// </summary>
    private static void BindResponsiveDetails(
        Grid layout, UIElement inventory, UIElement details, double breakpoint=850)
    {
        bool? compact=null;
        void Apply()
        {
            if(layout.ActualWidth<=0)return;
            bool narrow=layout.ActualWidth<breakpoint;
            if(compact==narrow)return;
            compact=narrow;
            layout.ColumnDefinitions.Clear();
            layout.RowDefinitions.Clear();
            if(narrow)
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition {
                    Width=new GridLength(1,GridUnitType.Star)
                });
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                Grid.SetColumn(inventory,0);Grid.SetRow(inventory,0);
                Grid.SetColumn(details,0);Grid.SetRow(details,1);
            }
            else
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition {
                    Width=new GridLength(7,GridUnitType.Star),MinWidth=340
                });
                layout.ColumnDefinitions.Add(new ColumnDefinition {
                    Width=new GridLength(3,GridUnitType.Star),MinWidth=225
                });
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                Grid.SetColumn(inventory,0);Grid.SetRow(inventory,0);
                Grid.SetColumn(details,1);Grid.SetRow(details,0);
            }
        }
        layout.Loaded+=(_,_)=>Apply();
        layout.SizeChanged+=(_,_)=>Apply();
    }

    // WPF uses device-independent pixels: a 1366px 150% DPI desktop may
    // expose fewer than 930 logical pixels. Stack the operational details
    // below the map table instead of compressing columns into unreadable text.
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
