using System.Windows;
using DeskWeave.Core.Display;
using DeskWeave.Core.GitHubStatus;
using DeskWeave.Desktop;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class ServiceStatusLayoutTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layout_SharedStatusFitsBothScreenSets(bool manyIncidents)
    {
        fixture.Run(() =>
        {
            var bounds = new PixelRect(0, 0, 1920, 1080);
            var display = new DisplayInfo("TEST", true, bounds, bounds, 1);
            var first = new WidgetSet(display, () => { });
            var firstWindows = Application.Current.Windows.OfType<DesktopWidgetWindow>().ToArray();
            var second = new WidgetSet(display, () => { });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var components = new[]
                {
                    new ServiceComponent("Copilot", "operational", "Operational", 0, 1),
                    new ServiceComponent("Copilot AI Model Providers", "operational", "Operational", 0, 2),
                };
                var incidents = manyIncidents
                    ? Enumerable.Range(0, 12).Select(i => new Incident($"Incident {i}: {new string('x', 180)}", "Investigating", "major")).ToArray()
                    : [];
                var summary = new StatusSummary(manyIncidents ? "Major disruption" : "All systems operational",
                    manyIncidents ? "major" : "none", components, incidents, [], now);
                var readings = new[]
                {
                    new ProviderStatusReading("github", "GitHub", new StatusReading(summary, true, false, now)),
                    new ProviderStatusReading("anthropic", "Claude", new StatusReading(summary, true, false, now)),
                    new ProviderStatusReading("openai", "OpenAI", new StatusReading(summary, true, false, now)),
                };

                first.Update(readings);
                second.Update(readings);
                first.Layout();
                second.Layout();

                var statusWindows = Application.Current.Windows.OfType<DesktopWidgetWindow>()
                    .Where(w => w.Widget is GitHubStatusWidget).ToArray();
                Assert.Equal(2, statusWindows.Length);
                Assert.All(statusWindows, window => Assert.True(window.IsVisible));
                Assert.Equal(statusWindows[0].MeasureScaled(), statusWindows[1].MeasureScaled());
                var status = firstWindows.Single(w => w.Widget is GitHubStatusWidget);
                var clock = firstWindows.Single(w => w.Widget is WorldClockWidget);
                Assert.True(status.MeasureScaled().Height + clock.MeasureScaled().Height + 60 <= bounds.Height);
                Assert.True(status.MeasureScaled().Width + 48 <= bounds.Width);
            }
            finally
            {
                first.Close();
                second.Close();
            }
        });
    }
}
