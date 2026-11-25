namespace Turnglass.Desktop.Tests;

[CollectionDefinition("Desktop configuration", DisableParallelization = true)]
public sealed class DesktopConfigurationDefinition;

[Collection("Desktop configuration")]
public sealed class DesktopOptionsTests
{
    [Fact]
    public void ExplicitDataDirectoryWinsAndLegacyEnvironmentDoesNotRedirectTheDefault()
    {
        using var environment = new EnvironmentScope();
        var explicitData = Path.Combine(Path.GetTempPath(), "Turnglass-explicit");
        var configuredData = Path.Combine(Path.GetTempPath(), "Turnglass-configured");
        environment.Set("TURNGLASS_DATA_DIR", configuredData);
        environment.Set("CODEX_SESSION_VIEWER_DATA_DIR", Path.Combine(Path.GetTempPath(), "legacy-ignored"));
        Assert.Equal(explicitData, DesktopOptions.Parse(["--data-dir", explicitData]).DataDirectory);
        Assert.Equal(configuredData, DesktopOptions.Parse([]).DataDirectory);
        environment.Set("TURNGLASS_DATA_DIR", null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turnglass"), DesktopOptions.Parse([]).DataDirectory);
    }

    [Fact]
    public void TestDebugAndDownloadConfigurationRequiresTheExplicitTestFlag()
    {
        using var environment = new EnvironmentScope();
        var data = Path.Combine(Path.GetTempPath(), "Turnglass-desktop-options");
        var downloads = Path.Combine(data, "downloads");
        var profile = Path.Combine(data, "test-profile");
        environment.Set("TURNGLASS_DESKTOP_TEST_PORT", "19222");
        environment.Set("TURNGLASS_DESKTOP_TEST_DOWNLOAD_DIR", downloads);
        environment.Set("WEBVIEW2_USER_DATA_FOLDER", profile);
        var normal = DesktopOptions.Parse(["--data-dir", data]);
        Assert.False(normal.TestMode);
        Assert.Null(normal.DebugPort);
        Assert.Null(normal.DownloadDirectory);
        Assert.Equal(Path.Combine(data, "WebView2"), normal.UserDataDirectory);
        var test = DesktopOptions.Parse(["--data-dir", data, "--desktop-test"]);
        Assert.True(test.TestMode);
        Assert.Equal(19222, test.DebugPort);
        Assert.Equal(downloads, test.DownloadDirectory);
        Assert.Equal(profile, test.UserDataDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("not-a-port")]
    public void TestModeRejectsMissingOrInvalidDebugPorts(string? port)
    {
        using var environment = new EnvironmentScope();
        environment.Set("TURNGLASS_DESKTOP_TEST_PORT", port);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--desktop-test"]));
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--unknown")]
    public void IncompleteOrUnknownArgumentsAreRejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse([argument]));
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = [];

        internal EnvironmentScope()
        {
            foreach (var name in new[] { "TURNGLASS_DATA_DIR", "CODEX_SESSION_VIEWER_DATA_DIR", "TURNGLASS_DESKTOP_TEST_PORT", "TURNGLASS_DESKTOP_TEST_DOWNLOAD_DIR", "WEBVIEW2_USER_DATA_FOLDER" })
            {
                Set(name, null);
            }
        }

        internal void Set(string name, string? value)
        {
            _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
