using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Services;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class TopProcessesWidgetTests(WpfFixture fixture)
{
    [Fact]
    public void Update_ComparisonBarsUseSectionLeaderWithoutChangingDisplayedUsage()
    {
        fixture.Run(() =>
        {
            var widget = new TopProcessesWidget();
            widget.Update(
            [
                new(1, "first", 8, 1024 * 1024, 2),
                new(2, "second", 4, 4 * 1024 * 1024, 8),
                new(3, "third", 2, 2 * 1024 * 1024, 4),
            ]);

            var sections = ((ItemsControl)widget.FindName("ProcessSections")).Items
                .Cast<TopProcessesWidget.ProcessSection>().ToArray();

            Assert.Equal(["CPU", "MEMORY", "GPU"], sections.Select(s => s.Label));
            Assert.Equal(["8%", "4%", "2%"], sections[0].Rows.Select(r => r.Value));
            Assert.Equal(["4.0 MB", "2.0 MB", "1.0 MB"], sections[1].Rows.Select(r => r.Value));
            Assert.Equal(["8%", "4%", "2%"], sections[2].Rows.Select(r => r.Value));
            foreach (var section in sections)
            {
                Assert.Equal(["01", "02", "03"], section.Rows.Select(r => r.Rank));
                Assert.Equal([100.0, 50.0, 25.0], section.Rows.Select(r => r.RelativeUsage));
            }

            widget.Update([new(1, "first", 0, 0, 0)]);
            sections = ((ItemsControl)widget.FindName("ProcessSections")).Items
                .Cast<TopProcessesWidget.ProcessSection>().ToArray();
            Assert.Equal("0%", Assert.Single(sections[0].Rows).Value);
            Assert.Equal(0, Assert.Single(sections[0].Rows).RelativeUsage);
            Assert.Equal("0 B", Assert.Single(sections[1].Rows).Value);
            Assert.Equal(0, Assert.Single(sections[1].Rows).RelativeUsage);
            var idleGpu = Assert.Single(sections[2].Rows);
            Assert.Equal("No active GPU load", idleGpu.Name);
            Assert.Equal(Visibility.Collapsed, idleGpu.MeterVisibility);
        });
    }

    [Fact]
    public void Update_UnavailableMetricsHaveNoRankOrComparisonBar()
    {
        fixture.Run(() =>
        {
            var widget = new TopProcessesWidget();
            widget.Update([new(1, "unknown", null, null, null)]);

            var sections = ((ItemsControl)widget.FindName("ProcessSections")).Items
                .Cast<TopProcessesWidget.ProcessSection>().ToArray();
            Assert.Equal(["Sampling", "N/A", "N/A"], sections.Select(s => Assert.Single(s.Rows).Name));
            foreach (var section in sections)
            {
                var row = Assert.Single(section.Rows);
                Assert.Empty(row.Rank);
                Assert.Empty(row.Value);
                Assert.Equal(Visibility.Collapsed, row.MeterVisibility);
            }
        });
    }

    [Fact]
    public void Layout_LongProcessNameWrapsWithoutClippingValuesAtNarrowWidths()
    {
        fixture.Run(() =>
        {
            const string name = "A process with an unusually long descriptive application name";
            foreach (var width in new[] { 320.0, 340.0 })
            {
                var widget = new TopProcessesWidget { Width = width };
                widget.Update([new(1, name, 100, 1024L * 1024 * 1024 * 1024, 100)]);
                widget.Measure(new Size(width, double.PositiveInfinity));
                widget.Arrange(new Rect(widget.DesiredSize));
                widget.UpdateLayout();

                var textBlocks = Descendants<TextBlock>(widget).ToArray();
                var names = textBlocks.Where(t => t.Text == name).ToArray();
                Assert.Equal(3, names.Length);
                foreach (var text in names)
                {
                    Assert.True(text.ActualHeight > text.FontSize * 2);
                    var row = Assert.IsType<Grid>(text.Parent);
                    var value = row.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2);
                    var nameBounds = text.TransformToAncestor(widget).TransformBounds(new Rect(text.RenderSize));
                    var valueBounds = value.TransformToAncestor(widget).TransformBounds(new Rect(value.RenderSize));
                    Assert.True(nameBounds.Right <= valueBounds.Left);
                    Assert.True(valueBounds.Right <= width);
                    Assert.True(value.ActualWidth >= value.DesiredSize.Width);
                }

                var bars = Descendants<ProgressBar>(widget).ToArray();
                Assert.Equal(3, bars.Length);
                Assert.All(bars, bar => Assert.Equal(100, bar.Value));
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
