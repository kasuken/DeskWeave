using System.Windows;
using System.Windows.Controls;
using DeskWeave.Core;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.Display;
using DeskWeave.Desktop;
using DeskWeave.Services;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class WidgetSetTests(WpfFixture fixture)
{
    [Fact]
    public void Layout_SharedDataAcrossTwoSets_PreservesQuotaAndFitsProcessPanelAboveClock()
    {
        fixture.Run(() =>
        {
            var bounds = new PixelRect(0, 0, 1920, 1080);
            var display = new DisplayInfo("TEST", true, bounds, bounds, 1);
            var firstSet = new WidgetSet(display, () => { });
            var firstWindows = Application.Current.Windows.OfType<DesktopWidgetWindow>().ToArray();
            var sets = new[] { firstSet, new WidgetSet(display, () => { }) };
            try
            {
                var idle = new AcceleratorSample("Idle GPU", "GPU", new AdapterKey(0, 1), 0,
                    new Dictionary<string, double> { ["3D"] = 0 }, 0, 1024, 0, 2048, null);
                var unavailable = idle with
                {
                    Name = "Unavailable NPU", Utilization = null, Engines = new Dictionary<string, double>(),
                    DedicatedUsed = null, SharedUsed = null,
                };
                var sample = new SystemSample(0, 8, new MemorySample(1024, 2048, 50), null, null, null,
                    new NetworkSample(false, "Network", "Disconnected", "", "", null, null, null),
                    [idle, unavailable])
                {
                    Processes =
                    [
                        new(1, "cpu-first", 12, 10_000, 0),
                        new(2, "memory-first", 9, 40_000, 9),
                        new(3, "gpu-second", 7, 30_000, 5),
                        new(4, "cpu-third", 6, 20_000, 1),
                    ],
                };
                ProviderQuota[] quotas = [new(QuotaProviders.Copilot, "GitHub Copilot", "Pro", [], null, DateTimeOffset.UtcNow)];
                foreach (var set in sets)
                {
                    set.Update(sample);
                    set.Update(quotas, false, DateTimeOffset.UtcNow);
                    set.Layout();
                }

                var windows = Application.Current.Windows.OfType<DesktopWidgetWindow>().ToArray();
                Assert.Equal(10, windows.Length);
                foreach (var window in windows.Where(w => w.Widget is AiQuotaWidget))
                    Assert.True(window.IsVisible);
                foreach (var monitor in windows.Where(w => w.Widget is SystemMonitorWidget))
                {
                    var cards = (ItemsControl)monitor.Widget.FindName("Accelerators");
                    var card = Assert.IsType<SystemMonitorWidget.AcceleratorCard>(Assert.Single(cards.Items));
                    Assert.Equal("Idle GPU", card.Name);
                    Assert.Contains(card.Rows, r => r.Label == "Load" && r.Value == "0%");
                    Assert.DoesNotContain(card.Rows, r => r.Label == "Temperature");

                }

                foreach (var processWindow in windows.Where(w => w.Widget is TopProcessesWidget))
                {
                    Assert.True(processWindow.IsVisible);
                    var processSections = (ItemsControl)processWindow.Widget.FindName("ProcessSections");
                    Assert.Equal(["cpu-first", "memory-first", "gpu-second"],
                        Assert.IsType<TopProcessesWidget.ProcessSection>(processSections.Items[0]).Rows
                            .Select(row => row.Name));
                    Assert.Equal(["memory-first", "gpu-second", "cpu-third"],
                        Assert.IsType<TopProcessesWidget.ProcessSection>(processSections.Items[1]).Rows
                            .Select(row => row.Name));
                    Assert.Equal(["memory-first", "gpu-second", "cpu-third"],
                        Assert.IsType<TopProcessesWidget.ProcessSection>(processSections.Items[2]).Rows
                            .Select(row => row.Name));
                }

                // Force the constrained layout with many live adapters on a shorter work area.
                sets[0].Display = display with { WorkArea = new PixelRect(0, 0, 1280, 720) };
                sets[0].Update(sample with { Accelerators = Enumerable.Repeat(idle, 8).ToArray() });
                sets[0].Update(Array.Empty<ProviderQuota>(), false, DateTimeOffset.UtcNow);
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                sets[0].Layout();
                var firstMonitor = firstWindows.Single(w => w.Widget is SystemMonitorWidget);
                var firstProcesses = firstWindows.Single(w => w.Widget is TopProcessesWidget);
                var firstClock = firstWindows.Single(w => w.Widget is WorldClockWidget);
                Assert.True(firstMonitor.Scale < sets[0].Display.ContentScale,
                    $"Monitor scale {firstMonitor.Scale}; content scale {sets[0].Display.ContentScale}");
                var margin = 24 * sets[0].Display.ContentScale;
                var gap = 12 * sets[0].Display.ContentScale;
                var totalHeight = firstMonitor.MeasureScaled().Height + firstProcesses.MeasureScaled().Height
                    + firstClock.MeasureScaled().Height + 2 * margin + 2 * gap;
                Assert.True(totalHeight <= sets[0].Display.WorkHeight + 1,
                    $"Widgets need {totalHeight} DIPs; work height {sets[0].Display.WorkHeight}");
                Assert.True(firstProcesses.IsVisible);
                Assert.Equal(firstMonitor.Scale, firstProcesses.Scale);
                Assert.Equal(firstMonitor.MeasureScaled().Width, firstProcesses.MeasureScaled().Width, 6);
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(firstMonitor.ActualWidth, firstProcesses.ActualWidth, 6);
                Assert.False(firstWindows.Single(w => w.Widget is AiQuotaWidget).IsVisible);

                sets[0].Display = display;
                sets[0].Update(sample);
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                sets[0].Layout();
                Assert.Equal(display.ContentScale, firstMonitor.Scale);
                Assert.Equal(firstMonitor.Scale, firstProcesses.Scale);
                Assert.Equal(firstMonitor.MeasureScaled().Width, firstProcesses.MeasureScaled().Width, 6);
            }
            finally
            {
                foreach (var set in sets) set.Close();
            }
        });
    }
}
