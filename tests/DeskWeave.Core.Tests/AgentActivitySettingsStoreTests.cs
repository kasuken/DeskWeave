using DeskWeave.Core.AgentActivity;

namespace DeskWeave.Core.Tests;

public sealed class AgentActivitySettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DeskWeaveAgentSettingsTests", Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(_folder, "settings.json");

    [Fact]
    public void Missing_settings_default_to_disabled()
    {
        var settings = new AgentActivitySettingsStore(SettingsFile).Load();
        Assert.Empty(settings.EnabledIntegrations);
        Assert.Empty(settings.Workspaces);
        Assert.Null(settings.Warning);
    }

    [Fact]
    public void Settings_round_trip_with_unknown_adapter_and_local_mapping()
    {
        Directory.CreateDirectory(_folder);
        var store = new AgentActivitySettingsStore(SettingsFile);
        store.Save(["claude-code", "future-adapter"], new Dictionary<string, string> { ["Example"] = _folder });

        var settings = store.Load();

        Assert.Equal(["claude-code", "future-adapter"], settings.EnabledIntegrations);
        Assert.Equal(_folder, settings.Workspaces["Example"]);
        Assert.Null(settings.Warning);
        Assert.Contains("\"EnabledIntegrations\"", File.ReadAllText(SettingsFile));
        Assert.Contains("\"Workspaces\"", File.ReadAllText(SettingsFile));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"EnabledIntegrations":["claude-code"],"Workspaces":{},"Secret":"hidden"}""")]
    [InlineData("""{"EnabledIntegrations":["claude-code","claude-code"],"Workspaces":{}}""")]
    [InlineData("""{"EnabledIntegrations":["unsafe/adapter"],"Workspaces":{}}""")]
    [InlineData("""{"EnabledIntegrations":["claude-code"],"Workspaces":{"My Workspace":"https://example.com"}}""")]
    [InlineData("""{"EnabledIntegrations":["claude-code"],"Workspaces":{"../private":"C:\\private"}}""")]
    [InlineData("""{"EnabledIntegrations":null,"Workspaces":{}}""")]
    [InlineData("""{"EnabledIntegrations":[],"EnabledIntegrations":[],"Workspaces":{}}""")]
    public void Invalid_settings_disable_everything_with_sanitized_warning(string text)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, text);

        var settings = new AgentActivitySettingsStore(SettingsFile).Load();

        Assert.Empty(settings.EnabledIntegrations);
        Assert.Empty(settings.Workspaces);
        Assert.NotNull(settings.Warning);
        Assert.DoesNotContain(_folder, settings.Warning);
    }

    [Fact]
    public void Oversized_settings_disable_integrations()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, new string('x', 16385));
        Assert.NotNull(new AgentActivitySettingsStore(SettingsFile).Load().Warning);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\temp")]
    public void Save_rejects_nonlocal_destinations(string destination)
    {
        var store = new AgentActivitySettingsStore(SettingsFile);
        Assert.Throws<ArgumentException>(() => store.Save(["claude-code"], new Dictionary<string, string> { ["Work"] = destination }));
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Invalid_save_does_not_replace_valid_settings()
    {
        Directory.CreateDirectory(_folder);
        var store = new AgentActivitySettingsStore(SettingsFile);
        store.Save(["claude-code"], new Dictionary<string, string>());
        Assert.Throws<ArgumentException>(() => store.Save(["bad/id"], new Dictionary<string, string>()));
        Assert.Equal(["claude-code"], store.Load().EnabledIntegrations);
    }

    [Fact]
    public void Save_propagates_io_failures()
    {
        Directory.CreateDirectory(_folder);
        var store = new AgentActivitySettingsStore(_folder);
        var exception = Record.Exception(() => store.Save(["claude-code"], new Dictionary<string, string>()));
        Assert.True(exception is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void Settings_are_limited_to_sixteen_workspaces()
    {
        Directory.CreateDirectory(_folder);
        var mappings = Enumerable.Range(0, 17).ToDictionary(i => $"Workspace {i}", _ => _folder);
        var store = new AgentActivitySettingsStore(SettingsFile);
        Assert.Throws<ArgumentException>(() => store.Save(["claude-code"], mappings));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
