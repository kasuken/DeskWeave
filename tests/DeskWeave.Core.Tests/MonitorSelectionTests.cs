using DeskWeave.Core.Display;

namespace DeskWeave.Core.Tests;

public class MonitorSelectionTests
{
    private static readonly DisplayInfo Primary = Display("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080);
    private static readonly DisplayInfo Right = Display("\\\\.\\DISPLAY2", false, 1920, 0, 2560, 1440);
    private static readonly DisplayInfo Left = Display("\\\\.\\DISPLAY3", false, -1280, 200, 1280, 1024);

    [Fact]
    public void Primary_mode_targets_only_the_primary_screen()
    {
        Assert.Equal([Primary], MonitorSelection.Targets(MonitorMode.Primary, [Right, Primary]));
    }

    [Fact]
    public void Secondary_mode_targets_only_the_secondary_screen()
    {
        Assert.Equal([Right], MonitorSelection.Targets(MonitorMode.Secondary, [Primary, Right]));
    }

    [Fact]
    public void Both_mode_targets_primary_then_secondary()
    {
        Assert.Equal([Primary, Right], MonitorSelection.Targets(MonitorMode.Both, [Right, Primary]));
    }

    [Fact]
    public void Secondary_mode_falls_back_to_primary_with_one_screen()
    {
        Assert.Equal([Primary], MonitorSelection.Targets(MonitorMode.Secondary, [Primary]));
    }

    [Fact]
    public void Both_mode_with_one_screen_targets_only_the_primary()
    {
        Assert.Equal([Primary], MonitorSelection.Targets(MonitorMode.Both, [Primary]));
    }

    [Fact]
    public void Both_mode_with_three_screens_uses_exactly_two()
    {
        Assert.Equal([Primary, Left], MonitorSelection.Targets(MonitorMode.Both, [Primary, Right, Left]));
    }

    [Fact]
    public void Secondary_is_the_leftmost_non_primary_screen()
    {
        Assert.Equal(Left, MonitorSelection.Secondary([Right, Primary, Left]));
    }

    [Fact]
    public void Secondary_ties_break_by_top_then_device_name()
    {
        var upperB = Display("B", false, 1920, -1080, 1920, 1080);
        var lowerA = Display("A", false, 1920, 0, 1920, 1080);
        var lowerC = Display("C", false, 1920, 0, 1920, 1080);

        Assert.Equal(upperB, MonitorSelection.Secondary([lowerC, Primary, lowerA, upperB]));
        Assert.Equal(lowerA, MonitorSelection.Secondary([lowerC, Primary, lowerA]));
    }

    [Fact]
    public void Display_change_moves_secondary_widgets_back_when_the_monitor_returns()
    {
        Assert.Equal([Primary], MonitorSelection.Targets(MonitorMode.Secondary, [Primary]));
        Assert.Equal([Right], MonitorSelection.Targets(MonitorMode.Secondary, [Primary, Right]));
    }

    [Fact]
    public void No_displays_means_no_targets()
    {
        foreach (var mode in Enum.GetValues<MonitorMode>())
        {
            Assert.Empty(MonitorSelection.Targets(mode, []));
        }
    }

    [Fact]
    public void Missing_primary_flag_uses_the_first_display()
    {
        var only = Primary with { IsPrimary = false };

        Assert.Equal([only], MonitorSelection.Targets(MonitorMode.Primary, [only]));
    }

    [Theory]
    [InlineData(MonitorMode.Primary, true)]
    [InlineData(MonitorMode.Secondary, false)]
    [InlineData(MonitorMode.Both, true)]
    public void Availability_with_one_screen(MonitorMode mode, bool expected)
    {
        Assert.Equal(expected, MonitorSelection.IsAvailable(mode, [Primary]));
    }

    [Fact]
    public void Every_mode_is_available_with_two_screens()
    {
        Assert.All(Enum.GetValues<MonitorMode>(), mode => Assert.True(MonitorSelection.IsAvailable(mode, [Primary, Right])));
    }

    [Fact]
    public void Work_area_is_measured_in_dips_and_placed_in_pixels_at_150_percent()
    {
        var display = new DisplayInfo("D", false, new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 0, 3840, 2088), 1.5);

        Assert.Equal(2560, display.WorkWidth);
        Assert.Equal(1392, display.WorkHeight);
        Assert.Equal((1920 + 150, 300), display.ToPixels(100, 200));
    }

    [Fact]
    public void Placement_respects_a_work_area_offset_by_the_taskbar_and_negative_coordinates()
    {
        var display = new DisplayInfo("D", false, new PixelRect(-1280, 0, 1280, 1024), new PixelRect(-1280, 40, 1280, 984), 1.25);

        Assert.Equal((-1280 + 10, 40 + 25), display.ToPixels(8, 20));
    }

    [Theory]
    [InlineData(1920, 1032, 1.0, 1.0)]
    [InlineData(3840, 2112, 1.0, 1.8)]
    [InlineData(1024, 720, 1.0, 0.7)]
    [InlineData(2880, 1548, 1.5, 1.0)]
    public void Content_scale_follows_logical_work_area(int width, int height, double dpi, double expected)
    {
        var display = new DisplayInfo("D", true, new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height), dpi);

        Assert.Equal(expected, display.ContentScale, 3);
    }

    private static DisplayInfo Display(string name, bool primary, int left, int top, int width, int height) =>
        new(name, primary, new PixelRect(left, top, width, height), new PixelRect(left, top, width, height - 40), 1.0);
}
