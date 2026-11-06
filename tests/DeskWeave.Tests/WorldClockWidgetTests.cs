using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

public class WorldClockWidgetTests
{
    [Fact]
    public void SetAvailableWidth_KeepsWingsBalancedAcrossStripWrappedAndStackedLayouts()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application();
            WorldClockWidget? widget = null;
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/DeskWeave;component/Theme/NeonIntrusion.xaml", UriKind.Relative),
                });
                widget = new WorldClockWidget();
                var left = (ItemsControl)widget.FindName("LeftWing");
                var right = (ItemsControl)widget.FindName("RightWing");
                var center = (Border)widget.FindName("Center");

                foreach (var (width, columns, stacked) in new[]
                {
                    (1920.0, 8, false),
                    (1100.0, 4, false),
                    (800.0, 2, false),
                    (320.0, 2, true),
                    (1920.0, 8, false),
                })
                {
                    widget.SetAvailableWidth(width);
                    widget.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    widget.Arrange(new Rect(widget.DesiredSize));
                    widget.UpdateLayout();

                    Assert.Equal(8, left.Items.Count);
                    Assert.Equal(8, right.Items.Count);
                    Assert.Equal(columns, widget.WingColumns);
                    Assert.Equal(left.ActualWidth, right.ActualWidth);
                    Assert.Equal(left.ActualHeight, right.ActualHeight);
                    Assert.True(widget.DesiredSize.Width <= width);
                    Assert.Equal(stacked ? 0 : 1, Grid.GetColumn(center));
                    Assert.Equal(stacked ? 1 : 0, Grid.GetRow(left));
                    Assert.Equal(stacked ? 2 : 0, Grid.GetRow(right));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                widget?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                app.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
