using System.Globalization;

namespace Turnglass.Desktop;

internal sealed record DesktopOptions(string DataDirectory, bool TestMode, int? DebugPort, string? DownloadDirectory, string UserDataDirectory)
{
    internal static DesktopOptions Parse(string[] arguments)
    {
        string? dataDirectory = null;
        var testMode = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--data-dir" when index + 1 < arguments.Length:
                    dataDirectory = arguments[++index];
                    break;
                case "--desktop-test":
                    testMode = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {arguments[index]}");
            }
        }

        var data = Path.GetFullPath(dataDirectory ?? Environment.GetEnvironmentVariable("TURNGLASS_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turnglass"));
        int? port = null;
        string? downloads = null;
        var profile = Path.Combine(data, "WebView2");
        if (testMode)
        {
            var value = Environment.GetEnvironmentVariable("TURNGLASS_DESKTOP_TEST_PORT");
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1024 or > 65535)
            {
                throw new ArgumentException("Desktop tests require TURNGLASS_DESKTOP_TEST_PORT between 1024 and 65535.");
            }

            port = parsed;
            downloads = Environment.GetEnvironmentVariable("TURNGLASS_DESKTOP_TEST_DOWNLOAD_DIR");
            downloads = string.IsNullOrWhiteSpace(downloads) ? null : Path.GetFullPath(downloads);
            profile = Path.GetFullPath(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER") ?? profile);
        }

        return new(data, testMode, port, downloads, profile);
    }
}
