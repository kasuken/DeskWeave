using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core;
using DeskWeave.Services;

namespace DeskWeave.Widgets;

public partial class SystemMonitorWidget : UserControl
{
    /// <summary>90 seconds of history at the 2 second sample interval.</summary>
    public const int HistoryLength = 45;

    private readonly MetricHistory _cpuHistory = new(HistoryLength);

    public SystemMonitorWidget()
    {
        InitializeComponent();
        CpuHistory.SizeChanged += (_, _) => DrawHistory();
    }

    public void Update(SystemSample sample)
    {
        LogicalProcessors.Text = $"{sample.LogicalProcessors} logical CPUs";

        SetMeter(CpuMeter, CpuValue, sample.Cpu);
        _cpuHistory.Add(sample.Cpu);
        DrawHistory();

        SetMeter(RamMeter, RamValue, sample.Memory?.Percent);
        RamDetail.Text = sample.Memory is { } memory
            ? $"{Formatting.Bytes(memory.Used)} / {Formatting.Bytes(memory.Total)}"
            : Formatting.Unavailable;
        ProcessSections.ItemsSource = ProcessSectionsFor(sample.Processes);

        if (sample.Disk is { } disk)
        {
            DiskLabel.Text = disk.Path;
            SetMeter(DiskMeter, DiskValue, disk.Percent);
            DiskDetail.Text = $"{Formatting.Bytes(disk.Available)} free of {Formatting.Bytes(disk.Total)}";
            DiskIo.Text = $"R {Formatting.Rate(disk.ReadRate)}   W {Formatting.Rate(disk.WriteRate)}";
        }
        else
        {
            SetMeter(DiskMeter, DiskValue, null);
            DiskDetail.Text = DiskIo.Text = Formatting.Unavailable;
        }

        CpuTemp.Text = Formatting.Temperature(sample.CpuTemperature);
        CpuTemp.Foreground = TemperatureBrush(sample.CpuTemperature);
        SsdTemp.Text = Formatting.Temperature(sample.SsdTemperature);
        Accelerators.ItemsSource = sample.Accelerators.Where(a => a.HasTelemetry)
            .Select(a => new AcceleratorCard(a.Name, a.Kind, AcceleratorPresentation.Rows(a))).ToArray();

        var network = sample.Network;
        var connectedBrush = Brush(network.Connected ? "GreenBrush" : "MutedBrush");
        NetDot.Foreground = connectedBrush;
        NetKind.Foreground = connectedBrush;
        NetKind.Text = network.Kind;
        NetName.Text = network.Name;
        NetSignal.Text = network.Signal is { } signal ? $"{signal}%" : string.Empty;
        NetAddress.Text = network.Connected ? $"{network.Address}  ·  {network.Interface}" : string.Empty;
        NetDown.Text = " " + Formatting.Rate(network.Download);
        NetUp.Text = " " + Formatting.Rate(network.Upload);
    }

    public sealed record AcceleratorCard(string Name, string Kind, IReadOnlyList<AcceleratorRow> Rows);
    public sealed record ProcessSection(string Label, IReadOnlyList<ProcessRow> Rows);
    public sealed record ProcessRow(string Name, string Value);

    private static IReadOnlyList<ProcessSection> ProcessSectionsFor(IReadOnlyList<ProcessUsage> processes)
    {
        var gpu = processes.Where(p => p.GpuPercent is > 0)
            .OrderByDescending(p => p.GpuPercent);
        var gpuUnavailable = processes.All(p => p.GpuPercent is null);
        return
        [
            new("CPU", Top(processes.Where(p => p.CpuPercent.HasValue)
                .OrderByDescending(p => p.CpuPercent), p => Formatting.Percent(p.CpuPercent), "Sampling")),
            new("MEMORY", Top(processes.Where(p => p.WorkingSet.HasValue)
                .OrderByDescending(p => p.WorkingSet), p => Formatting.Bytes(p.WorkingSet), Formatting.Unavailable)),
            new("GPU", Top(gpu, p => Formatting.Percent(p.GpuPercent),
                gpuUnavailable ? Formatting.Unavailable : "No active GPU load")),
        ];
    }

    private static IReadOnlyList<ProcessRow> Top(
        IEnumerable<ProcessUsage> ordered,
        Func<ProcessUsage, string> format,
        string emptyLabel)
    {
        var rows = ordered.Take(3).Select(p => new ProcessRow(p.Name, format(p))).ToArray();
        return rows.Length > 0 ? rows : [new ProcessRow(emptyLabel, string.Empty)];
    }

    private void SetMeter(ProgressBar meter, TextBlock value, double? percent)
    {
        meter.Value = percent ?? 0;
        meter.Foreground = percent switch
        {
            >= 90 => Brush("RedBrush"),
            >= 75 => Brush("YellowBrush"),
            _ => Brush("AccentBrush"),
        };
        value.Text = Formatting.Percent(percent);
    }

    private Brush TemperatureBrush(double? celsius) => celsius switch
    {
        >= 90 => Brush("RedBrush"),
        >= 75 => Brush("YellowBrush"),
        _ => Brush("ForegroundBrush"),
    };

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void DrawHistory()
    {
        var width = CpuHistory.ActualWidth;
        var height = CpuHistory.ActualHeight;
        var samples = _cpuHistory.Samples.ToArray();
        var line = new PointCollection(samples.Length);
        var area = new PointCollection(samples.Length + 2);
        if (width <= 0 || height <= 0 || samples.Length == 0)
        {
            CpuLine.Points = line;
            CpuArea.Points = area;
            return;
        }

        // Right-aligned so the newest sample is always at the right edge.
        var step = width / (HistoryLength - 1);
        var start = width - (samples.Length - 1) * step;
        for (var i = 0; i < samples.Length; i++)
        {
            line.Add(new Point(start + i * step, height - 2 - samples[i] / 100 * (height - 4)));
        }

        area.Add(new Point(start, height));
        foreach (var point in line)
        {
            area.Add(point);
        }

        area.Add(new Point(width, height));
        CpuLine.Points = line;
        CpuArea.Points = area;
    }
}
