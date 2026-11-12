using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskWeave.Core.AgentActivity;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.Display;
using DeskWeave.Desktop;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class AgentActivityWidgetTests(WpfFixture fixture)
{
    [Fact]
    public void Update_BoundsRowsKeepsConcurrentSessionsAndDoesNotRebuildUnchangedRows()
    {
        fixture.Run(() =>
        {
            var widget = new AgentActivityWidget();
            var now = DateTimeOffset.UtcNow;
            var tasks = Enumerable.Range(0, 7).Select(index => Task(index.ToString(), now,
                index < 2 ? AgentTaskState.NeedsApproval : AgentTaskState.Running)).ToArray();
            var snapshot = new AgentActivitySnapshot(tasks, [new("claude-code", "Claude Code", "Connected", null)]);
            widget.Update(snapshot);
            var rows = (ItemsControl)widget.FindName("Tasks");
            Assert.Equal(5, rows.Items.Count);
            Assert.Equal("2 waiting on you", ((TextBlock)widget.FindName("Attention")).Text);
            Assert.Contains("2 more tasks", ((TextBlock)widget.FindName("Overflow")).Text);
            var first = Assert.IsType<AgentActivityWidget.TaskRow>(rows.Items[0]);
            var second = Assert.IsType<AgentActivityWidget.TaskRow>(rows.Items[1]);
            Assert.Equal(first.Workspace, second.Workspace);
            Assert.NotEqual(first.Detail, second.Detail);
            Assert.Equal("Approval needed", first.State);
            var original = rows.ItemsSource;
            widget.Update(snapshot);
            Assert.Same(original, rows.ItemsSource);
            widget.Update(AgentActivitySnapshot.Empty);
            Assert.False(widget.HasSources);
            Assert.Empty(rows.Items);
        });
    }

    [Fact]
    public void Update_UnknownTasksDoNotClaimRunningOrHumanAttention()
    {
        fixture.Run(() =>
        {
            var widget = new AgentActivityWidget();
            var task = Task("a", DateTimeOffset.UtcNow, AgentTaskState.NeedsInput) with { Freshness = AgentFreshness.Unknown };
            widget.Update(new([task], [new("claude-code", "Claude Code", "Disconnected", "Snapshot is invalid.")]));
            Assert.Equal("0 waiting on you", ((TextBlock)widget.FindName("Attention")).Text);
            var row = Assert.IsType<AgentActivityWidget.TaskRow>(((ItemsControl)widget.FindName("Tasks")).Items[0]);
            Assert.Equal("Unknown", row.State);
            Assert.Contains("disconnected", row.Timing);
            var source = Assert.IsType<AgentActivityWidget.SourceRow>(((ItemsControl)widget.FindName("Sources")).Items[0]);
            Assert.Contains("Disconnected", source.Status);
            Assert.Equal(Visibility.Visible, source.WarningVisibility);
        });
    }

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1280, 720, 1)]
    [InlineData(1920, 1080, 1.5)]
    public void Layout_AgentPanelDoesNotOverlapExistingPanels(int width, int height, double scale)
    {
        fixture.Run(() =>
        {
            var bounds = new PixelRect(0, 0, width, height);
            var display = new DisplayInfo("TEST", true, bounds, bounds, scale);
            var first = new WidgetSet(display, () => { });
            var firstWindows = Application.Current.Windows.OfType<DesktopWidgetWindow>().ToArray();
            var second = new WidgetSet(display, () => { });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = new AgentActivitySnapshot(
                    Enumerable.Range(0, 7).Select(index => Task(index.ToString(), now, AgentTaskState.NeedsInput)).ToArray(),
                    [new("claude-code", "Claude Code", "Connected", null)]);
                ProviderQuota[] quotas = [
                    new("copilot", "GitHub Copilot", "Pro",
                        [new("AI credits", 20, now.AddHours(5), Identity: "premium")], null, now),
                ];
                foreach (var set in new[] { first, second })
                {
                    set.Update(snapshot);
                    set.Update(quotas, false, now);
                    set.Layout();
                }
                Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                first.Layout();
                second.Layout();
                var agentWindow = firstWindows.Single(window => window.Widget is AgentActivityWidget);
                if (width == 1920 && scale == 1) Assert.True(agentWindow.IsVisible);
                var visible = firstWindows.Where(window => window.IsVisible).ToArray();
                for (var index = 0; index < visible.Length; index++)
                {
                    var current = visible[index];
                    var currentBounds = new Rect(current.Left, current.Top, current.ActualWidth, current.ActualHeight);
                    foreach (var other in visible.Skip(index + 1))
                    {
                        var intersection = Rect.Intersect(currentBounds,
                            new Rect(other.Left, other.Top, other.ActualWidth, other.ActualHeight));
                        Assert.True(intersection.IsEmpty || intersection.Width <= 1 || intersection.Height <= 1,
                            $"{current.Title} overlaps {other.Title}: {intersection}");
                    }
                }
                var secondAgent = Application.Current.Windows.OfType<DesktopWidgetWindow>()
                    .Except(firstWindows).Single(window => window.Widget is AgentActivityWidget);
                Assert.Equal(agentWindow.IsVisible, secondAgent.IsVisible);
                Assert.Equal(agentWindow.MeasureScaled(), secondAgent.MeasureScaled());
            }
            finally
            {
                first.Close();
                second.Close();
            }
        });
    }

    private static AgentTaskView Task(string id, DateTimeOffset now, AgentTaskState state) =>
        new("claude-code", id.PadLeft(64, '0'), $"task-{id}", "Workspace", "Coding turn",
            state, AgentFreshness.Fresh, now.AddMinutes(-3), now, TimeSpan.FromMinutes(3));
}
