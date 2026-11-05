using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskWeave.Core;
using Microsoft.Win32;

namespace DeskWeave.Widgets;

public partial class WorldClockWidget : UserControl
{
    public static readonly DependencyProperty WingColumnsProperty =
        DependencyProperty.Register(nameof(WingColumns), typeof(int), typeof(WorldClockWidget), new PropertyMetadata(8));

    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(WorldClockWidget), new PropertyMetadata(98.0));

    public static readonly DependencyProperty CenterWidthProperty =
        DependencyProperty.Register(nameof(CenterWidth), typeof(double), typeof(WorldClockWidget), new PropertyMetadata(196.0));

    // Horizontal chrome: panel padding and border plus the center's margins.
    private const double Chrome = 14 * 2 + 2 + 12 * 2;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly IReadOnlyList<City> _cities = WorldClock.DefaultCities;
    private TimeZoneInfo _localZone = TimeZoneInfo.Local;
    private string? _localIana;
    private int _wingSize = 8;
    private double _availableWidth = double.PositiveInfinity;

    public WorldClockWidget()
    {
        InitializeComponent();
        RefreshLocalZone();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            SystemEvents.TimeChanged += OnTimeChanged;
            Tick();
        };
        Unloaded += (_, _) =>
        {
            SystemEvents.TimeChanged -= OnTimeChanged;
            _timer.Stop();
        };
        Tick();
    }

    public int WingColumns
    {
        get => (int)GetValue(WingColumnsProperty);
        set => SetValue(WingColumnsProperty, value);
    }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CenterWidth
    {
        get => (double)GetValue(CenterWidthProperty);
        set => SetValue(CenterWidthProperty, value);
    }

    /// <summary>
    /// Adapts the arrangement to the available width (in unscaled widget units): one strip with the
    /// local clock centered when it fits, wrapped wings on medium screens, and a stacked layout on narrow ones.
    /// </summary>
    public void SetAvailableWidth(double width)
    {
        _availableWidth = width;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var cell = CellWidth;
        var sideBySideColumns = (int)Math.Floor((_availableWidth - Chrome - CenterWidth) / (2 * cell));
        var stacked = sideBySideColumns < 2;
        var fit = stacked ? (int)Math.Floor((_availableWidth - Chrome) / cell) : sideBySideColumns;
        fit = Math.Clamp(fit, 1, _wingSize);

        // Balance rows instead of leaving a single orphan clock on the last row.
        var rows = (int)Math.Ceiling(_wingSize / (double)fit);
        WingColumns = (int)Math.Ceiling(_wingSize / (double)rows);

        if (stacked)
        {
            Place(Center, row: 0, column: 0);
            Place(LeftWing, row: 1, column: 0);
            Place(RightWing, row: 2, column: 0);
            Center.Margin = new Thickness(0, 0, 0, 8);
            Center.BorderThickness = new Thickness(0, 0, 0, 1);
            LeftWing.Margin = new Thickness(0, 0, 0, 4);
            Center.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            Place(LeftWing, row: 0, column: 0);
            Place(Center, row: 0, column: 1);
            Place(RightWing, row: 0, column: 2);
            Center.Margin = new Thickness(12, 0, 12, 0);
            Center.BorderThickness = new Thickness(1, 0, 1, 0);
            LeftWing.Margin = new Thickness(0);
        }
    }

    private static void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        // Picks up time zone changes made in Windows Settings while running.
        TimeZoneInfo.ClearCachedData();
        RefreshLocalZone();
        Tick();
    });

    private void RefreshLocalZone()
    {
        _localZone = TimeZoneInfo.Local;
        string? region = null;
        try
        {
            region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
        }

        _localIana = WorldClock.LocalIanaId(_localZone, region);
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = WorldClock.Snapshot(now, _localZone, _localIana, _cities);

        LocalCity.Text = snapshot.Local.City.ToUpperInvariant();
        LocalTime.Text = snapshot.Local.SecondsText;
        LocalDetail.Text = $"{snapshot.Local.DateText} · {snapshot.Local.UtcOffset}";
        LeftWing.ItemsSource = snapshot.Left;
        RightWing.ItemsSource = snapshot.Right;

        var wingSize = Math.Max(snapshot.Left.Count, snapshot.Right.Count);
        if (wingSize != _wingSize && wingSize > 0)
        {
            _wingSize = wingSize;
            ApplyLayout();
        }

        // Align ticks to the next wall-clock second.
        _timer.Interval = TimeSpan.FromMilliseconds(1000 - DateTime.UtcNow.Millisecond + 5);
        _timer.Start();
    }
}
