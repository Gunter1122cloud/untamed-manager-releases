using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

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
        // Content sizing is based on its measured DIPs below, not Window.Width.
        // Content sizing is based on its measured DIPs below, not Window.Width.
    }

    /// <summary>
    /// Use the actual card-area width: a CI screenshot can render the same
    /// live WPF visual tree off-screen at a viewport larger than the runner.
    /// More importantly, a 150% DPI laptop still receives usable card widths.
    /// </summary>
    private void DashboardKpis_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if(sender is not UniformGrid cards || e.NewSize.Width<=0)
            return;
        int columns=e.NewSize.Width>=1040?4:
            e.NewSize.Width>=560?2:1;
        if(cards.Columns!=columns)
            cards.Columns=columns;
    }

    private void DashboardHeroContent_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(e.NewSize.Width<=0)return;
        var visible=e.NewSize.Width>=840?Visibility.Visible:Visibility.Collapsed;
        if(DashboardHeroActions.Visibility!=visible)
            DashboardHeroActions.Visibility=visible;
    }

    /// <summary>
    /// The illustrated dashboard uses real WPF content; avoid narrow clipped
    /// image tiles at 1366x768 and 125-150% DPI by adapting to available DIPs.
    /// The containing Dashboard ScrollViewer owns the mouse wheel.
    /// </summary>
    private void DashboardMapTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not UniformGrid tiles || e.NewSize.Width <= 0)
            return;
        int columns = e.NewSize.Width >= 720 ? 2 : 1;
        if (tiles.Columns != columns)
            tiles.Columns = columns;
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

    // Server Center: the map inventory is a narrow sidebar, unlike the 70/30
    // Mod Center. The details must stack at 150% DPI instead of being clipped.
    private static void BindResponsiveServerDetails(Grid layout, UIElement inventory, UIElement details)
    {
        bool? stacked=null;
        void Apply()
        {
            if(layout.ActualWidth<=0)return;
            bool narrow=layout.ActualWidth<780;
            if(stacked==narrow)return;
            stacked=narrow;
            layout.ColumnDefinitions.Clear();
            layout.RowDefinitions.Clear();
            if(narrow)
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                Grid.SetRow(inventory,0);Grid.SetColumn(inventory,0);
                Grid.SetRow(details,1);Grid.SetColumn(details,0);
            }
            else
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(265)});
                layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                Grid.SetRow(inventory,0);Grid.SetColumn(inventory,0);
                Grid.SetRow(details,0);Grid.SetColumn(details,1);
            }
        }
        layout.Loaded+=(_,_)=>Apply();
        layout.SizeChanged+=(_,_)=>Apply();
    }

    private static void BindResponsiveOperationsHub(Grid layout)
    {
        int currentColumns=0;
        void Apply()
        {
            if(layout.ActualWidth<=0)return;
            int cols=layout.ActualWidth>=1050?3:layout.ActualWidth>=790?2:1;
            if(cols==currentColumns)return;
            currentColumns=cols;
            layout.ColumnDefinitions.Clear();
            layout.RowDefinitions.Clear();
            for(int c=0;c<cols;c++)
                layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            for(int r=0;r<(layout.Children.Count+cols-1)/cols;r++)
                layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            for(int i=0;i<layout.Children.Count;i++)
            {
                Grid.SetColumn(layout.Children[i],i%cols);
                Grid.SetRow(layout.Children[i],i/cols);
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
