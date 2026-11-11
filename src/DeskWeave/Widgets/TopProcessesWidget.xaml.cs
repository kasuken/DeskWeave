using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core;
using DeskWeave.Services;

namespace DeskWeave.Widgets;

public partial class TopProcessesWidget : UserControl
{
    public TopProcessesWidget() => InitializeComponent();

    public void Update(IReadOnlyList<ProcessUsage> processes) =>
        ProcessSections.ItemsSource = ProcessSectionsFor(processes);

    public sealed record ProcessSection(string Label, string Detail, Brush Tint, IReadOnlyList<ProcessRow> Rows);
    public sealed record ProcessRow(string Name, string Value, string Rank = "", double RelativeUsage = 0)
    {
        public Visibility MeterVisibility => Rank.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<ProcessSection> ProcessSectionsFor(IReadOnlyList<ProcessUsage> processes)
    {
        var gpu = processes.Where(p => p.GpuPercent is > 0)
            .OrderByDescending(p => p.GpuPercent);
        var gpuUnavailable = processes.All(p => p.GpuPercent is null);
        return
        [
            new("CPU", "PROCESSOR", (Brush)FindResource("AccentBrush"),
                Top(processes.Where(p => p.CpuPercent.HasValue).OrderByDescending(p => p.CpuPercent),
                    p => p.CpuPercent.GetValueOrDefault(), p => Formatting.Percent(p.CpuPercent), "Sampling")),
            new("MEMORY", "WORKING SET", (Brush)FindResource("BlueBrush"),
                Top(processes.Where(p => p.WorkingSet.HasValue).OrderByDescending(p => p.WorkingSet),
                    p => p.WorkingSet.GetValueOrDefault(), p => Formatting.Bytes(p.WorkingSet), Formatting.Unavailable)),
            new("GPU", "GRAPHICS", (Brush)FindResource("MagentaBrush"),
                Top(gpu, p => p.GpuPercent.GetValueOrDefault(), p => Formatting.Percent(p.GpuPercent),
                gpuUnavailable ? Formatting.Unavailable : "No active GPU load")),
        ];
    }

    private static IReadOnlyList<ProcessRow> Top(
        IEnumerable<ProcessUsage> ordered,
        Func<ProcessUsage, double> measure,
        Func<ProcessUsage, string> format,
        string emptyLabel)
    {
        var top = ordered.Take(3).ToArray();
        if (top.Length == 0)
        {
            return [new ProcessRow(emptyLabel, string.Empty)];
        }

        var maximum = measure(top[0]);
        return top.Select((p, index) => new ProcessRow(
            p.Name, format(p), (index + 1).ToString("00", CultureInfo.InvariantCulture),
            maximum > 0 ? 100 * (measure(p) / maximum) : 0)).ToArray();
    }
}
