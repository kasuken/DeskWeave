using DeskWeave.Core.Display;

namespace DeskWeave.Core.Tests;

public sealed class DisplaySettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DeskWeaveTests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_folder, "settings.json");

    [Fact]
    public void Defaults_to_primary_when_nothing_is_saved()
    {
        Assert.Equal(MonitorMode.Primary, new DisplaySettingsStore(File).Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"MonitorMode":"Everywhere"}""")]
    [InlineData("""{"MonitorMode":42}""")]
    [InlineData("null")]
    public void Defaults_to_primary_when_the_file_is_invalid(string content)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, content);

        Assert.Equal(MonitorMode.Primary, new DisplaySettingsStore(File).Load());
    }

    [Theory]
    [InlineData(MonitorMode.Primary)]
    [InlineData(MonitorMode.Secondary)]
    [InlineData(MonitorMode.Both)]
    public void Saved_mode_survives_a_restart(MonitorMode mode)
    {
        new DisplaySettingsStore(File).Save(mode);

        Assert.Equal(mode, new DisplaySettingsStore(File).Load());
    }

    [Fact]
    public void Fallback_to_primary_does_not_change_the_saved_secondary_preference()
    {
        var store = new DisplaySettingsStore(File);
        store.Save(MonitorMode.Secondary);
        var primary = new DisplayInfo("P", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1);

        var targets = MonitorSelection.Targets(store.Load(), [primary]);

        Assert.Equal([primary], targets);
        Assert.Equal(MonitorMode.Secondary, new DisplaySettingsStore(File).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
