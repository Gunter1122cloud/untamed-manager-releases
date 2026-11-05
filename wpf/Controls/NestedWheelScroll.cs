using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UntamedManager.Core;

namespace UntamedManager;

/// <summary>
/// Explicit opt-in for nested lists in the premium workspaces. Does not intercept
/// the global mouse route, ComboBox popup or wheel input inside a scrollable list.
/// </summary>
public static class NestedWheelScroll
{
    public static readonly DependencyProperty ChainToParentProperty =
        DependencyProperty.RegisterAttached("ChainToParent", typeof(bool),
            typeof(NestedWheelScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetChainToParent(DependencyObject target) =>
        (bool)target.GetValue(ChainToParentProperty);

    public static void SetChainToParent(DependencyObject target, bool value) =>
        target.SetValue(ChainToParentProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        element.PreviewMouseWheel -= OnWheel;
        if (args.NewValue is true) element.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not FrameworkElement control)
            return;
        // The template-internal ScrollViewer is the only viewer belonging
        // to this list; the parent viewer is found strictly ABOVE the list.
        ScrollViewer? inner = FindChild<ScrollViewer>(control);
        if (inner is not null && !ScrollBoundaryPolicy.ShouldChain(
            inner.VerticalOffset, inner.ScrollableHeight, e.Delta)) return;

        ScrollViewer? outer = FindParent<ScrollViewer>(control);
        if (outer is null) return;
        double next = ScrollBoundaryPolicy.ForwardOffset(
            outer.VerticalOffset, outer.ScrollableHeight, outer.ViewportHeight,
            e.Delta, SystemParameters.WheelScrollLines);
        if (Math.Abs(next - outer.VerticalOffset) < 0.01) return;
        outer.ScrollToVerticalOffset(next);
        e.Handled = true;
    }

    // Rebinding rows should not reset a user's place in a long mod/plugin list.
    public static double? CaptureVerticalOffset(FrameworkElement control) =>
        FindChild<ScrollViewer>(control)?.VerticalOffset;

    public static void RestoreVerticalOffset(FrameworkElement control, double? offset)
    {
        if (!offset.HasValue) return;
        control.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (control.IsLoaded && FindChild<ScrollViewer>(control) is { } viewer)
                viewer.ScrollToVerticalOffset(Math.Clamp(offset.Value, 0,
                    viewer.ScrollableHeight));
        }), DispatcherPriority.Loaded);
    }

    private static T? FindParent<T>(DependencyObject control) where T : DependencyObject
    {
        DependencyObject? ancestor = VisualTreeHelper.GetParent(control);
        while (ancestor is not null)
        {
            if (ancestor is T found) return found;
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject control) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(control);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(control, i);
            if (child is T found) return found;
            T? nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
