using System.Windows;

namespace UntamedManager;

// The current page is a semantic, testable state; hover and keyboard focus
// remain orthogonal. Only the navigation method sets this property.
public static class NavSelection
{
    public static readonly DependencyProperty IsCurrentProperty=
        DependencyProperty.RegisterAttached("IsCurrent",typeof(bool),
            typeof(NavSelection),new FrameworkPropertyMetadata(false));

    public static bool GetIsCurrent(DependencyObject control)=>
        (bool)control.GetValue(IsCurrentProperty);

    public static void SetIsCurrent(DependencyObject control,bool value)=>
        control.SetValue(IsCurrentProperty,value);
}
