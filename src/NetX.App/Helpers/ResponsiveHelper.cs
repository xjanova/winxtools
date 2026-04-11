using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetX.App.Helpers;

/// <summary>
/// Helper class for responsive layout behavior
/// </summary>
public static class ResponsiveHelper
{
    // Breakpoints
    public const double SmallWidth = 900;   // Switch to single column
    public const double MediumWidth = 1200; // Normal 2-column layout
    public const double LargeWidth = 1600;  // Full size

    // Font size limits
    public const double MinFontSize = 12;
    public const double MaxFontSize = 24;

    /// <summary>
    /// Calculate responsive font size based on available width
    /// </summary>
    public static double CalculateFontSize(double availableWidth, double baseFontSize, double minWidth, double maxWidth)
    {
        if (availableWidth >= maxWidth)
            return baseFontSize;

        if (availableWidth <= minWidth)
            return MinFontSize;

        // Linear interpolation between min and max
        double ratio = (availableWidth - minWidth) / (maxWidth - minWidth);
        double fontSize = MinFontSize + (baseFontSize - MinFontSize) * ratio;

        return Math.Max(MinFontSize, Math.Min(MaxFontSize, fontSize));
    }

    /// <summary>
    /// Determine if layout should use wrap mode (vertical stacking)
    /// </summary>
    public static bool ShouldWrap(double availableWidth, double itemCount, double minItemWidth)
    {
        double requiredWidth = itemCount * minItemWidth;
        return availableWidth < requiredWidth;
    }

    /// <summary>
    /// Calculate number of columns for uniform grid based on width
    /// </summary>
    public static int CalculateColumns(double availableWidth, double minColumnWidth, int maxColumns)
    {
        int cols = (int)(availableWidth / minColumnWidth);
        return Math.Max(1, Math.Min(maxColumns, cols));
    }

    /// <summary>
    /// Update UniformGrid columns based on available width
    /// </summary>
    public static void UpdateUniformGridColumns(UniformGrid grid, double availableWidth, double minColumnWidth)
    {
        int columns = CalculateColumns(availableWidth, minColumnWidth, grid.Children.Count);

        if (columns != grid.Columns)
        {
            grid.Columns = columns;
            grid.Rows = 0; // Auto-calculate rows
        }
    }

    /// <summary>
    /// Update Grid column definitions for responsive 2-column layout
    /// </summary>
    public static void UpdateTwoColumnLayout(Grid grid, double availableWidth, double breakpoint)
    {
        if (grid.ColumnDefinitions.Count < 3) return;

        if (availableWidth < breakpoint)
        {
            // Stack vertically - make both columns auto
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(0);
            grid.ColumnDefinitions[2].Width = new GridLength(0);

            // Move second column content to a new row
            foreach (UIElement child in grid.Children)
            {
                int col = Grid.GetColumn(child);
                if (col == 2)
                {
                    Grid.SetColumn(child, 0);
                    Grid.SetRow(child, Grid.GetRow(child) + 1);
                }
            }
        }
        else
        {
            // Restore 2-column layout
            grid.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(16);
            grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }
    }
}

/// <summary>
/// Attached behavior for responsive font size
/// </summary>
public class ResponsiveFontBehavior
{
    public static readonly DependencyProperty BaseFontSizeProperty =
        DependencyProperty.RegisterAttached(
            "BaseFontSize",
            typeof(double),
            typeof(ResponsiveFontBehavior),
            new PropertyMetadata(14.0));

    public static readonly DependencyProperty MinWidthProperty =
        DependencyProperty.RegisterAttached(
            "MinWidth",
            typeof(double),
            typeof(ResponsiveFontBehavior),
            new PropertyMetadata(600.0));

    public static readonly DependencyProperty MaxWidthProperty =
        DependencyProperty.RegisterAttached(
            "MaxWidth",
            typeof(double),
            typeof(ResponsiveFontBehavior),
            new PropertyMetadata(1200.0));

    public static double GetBaseFontSize(DependencyObject obj) => (double)obj.GetValue(BaseFontSizeProperty);
    public static void SetBaseFontSize(DependencyObject obj, double value) => obj.SetValue(BaseFontSizeProperty, value);

    public static double GetMinWidth(DependencyObject obj) => (double)obj.GetValue(MinWidthProperty);
    public static void SetMinWidth(DependencyObject obj, double value) => obj.SetValue(MinWidthProperty, value);

    public static double GetMaxWidth(DependencyObject obj) => (double)obj.GetValue(MaxWidthProperty);
    public static void SetMaxWidth(DependencyObject obj, double value) => obj.SetValue(MaxWidthProperty, value);
}
