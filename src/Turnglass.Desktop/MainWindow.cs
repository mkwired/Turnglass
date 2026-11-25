extern alias DesktopBridgeComponent;

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Turnglass.Infrastructure;
using Windows.Storage.Pickers;
using Windows.System;
using NativeBridge = DesktopBridgeComponent::Turnglass.DesktopBridge.NativeBridge;

namespace Turnglass.Desktop;

internal sealed class MainWindow : Window, IAsyncDisposable
{
    private const string Origin = "https://turnglass.example";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeBridge _bridge = new();
    private readonly Grid _root = new();
    private readonly TextBlock _status = new() { Text = "Starting Turnglass…", TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
    private readonly WebView2 _webView = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _message = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private IHost? _host;
    private DesktopOptions? _options;
    private Task? _initialization;
    private Task? _shutdown;
    private bool _closing;
    private bool _stopped;

    internal MainWindow()
    {
        Title = "Turnglass";
        _message.Children.Add(_status);
        _root.Children.Add(_webView);
        _root.Children.Add(_message);
        Content = _root;
        _root.Loaded += (_, _) => _initialization ??= InitializeAsync();
        AppWindow.Closing += OnClosing;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var scale = _root.XamlRoot.RasterizationScale;
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min((int)(1280 * scale), workArea.Width);
            var height = Math.Min((int)(850 * scale), workArea.Height);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                workArea.X + ((workArea.Width - width) / 2), workArea.Y + ((workArea.Height - height) / 2), width, height));
            _options = DesktopOptions.Parse(Environment.GetCommandLineArgs()[1..]);
            Directory.CreateDirectory(_options.DataDirectory);
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "turnglass.ico"));
            if (_options.TestMode)
            {
                await File.WriteAllTextAsync(Path.Combine(_options.DataDirectory, "desktop-runtime.txt"),
                    $"NativeAOT={!RuntimeFeature.IsDynamicCodeSupported}\nReflectionJson={System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault}\n",
                    _lifetime.Token).ConfigureAwait(true);
            }

            _host = await Task.Run(async () =>
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = AppContext.BaseDirectory,
                    DisableDefaults = true,
                });
                builder.Services.AddTurnglass(_options.DataDirectory);
                builder.Services.AddSingleton<DesktopApiDispatcher>();
                var host = builder.Build();
                try
                {
                    await host.StartAsync(_lifetime.Token).ConfigureAwait(false);
                    return host;
                }
                catch
                {
                    host.Dispose();
                    throw;
                }
            }, _lifetime.Token).ConfigureAwait(true);

            _lifetime.Token.ThrowIfCancellationRequested();
            _bridge.Configure(_host.Services.GetRequiredService<DesktopApiDispatcher>().InvokeAsync, _lifetime.Token);
            var assets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!File.Exists(Path.Combine(assets, "index.html")))
            {
                throw new FileNotFoundException("The frontend is missing. Publish the complete Turnglass desktop folder and try again.");
            }

            var environmentOptions = new CoreWebView2EnvironmentOptions();
            if (_options.DebugPort is int port)
            {
                environmentOptions.AdditionalBrowserArguments = $"--remote-debugging-port={port.ToString(CultureInfo.InvariantCulture)} --remote-debugging-address=127.0.0.1";
            }

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _options.UserDataDirectory, environmentOptions)
                .AsTask().WaitAsync(_lifetime.Token).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).AsTask().WaitAsync(_lifetime.Token).ConfigureAwait(true);
            _lifetime.Token.ThrowIfCancellationRequested();
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = _options.TestMode;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("turnglass.example", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += (_, args) =>
            {
                TraceTest("Permission " + args.PermissionKind);
                args.State = CoreWebView2PermissionState.Deny;
            };
            core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            core.DownloadStarting += OnDownloadStarting;
            core.ProcessFailed += (_, _) => ShowError("The web view stopped unexpectedly. Close and reopen Turnglass to continue.");
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!IsTrustedOrigin(args.Request.Uri) && !args.Request.Uri.StartsWith("blob:" + Origin + "/", StringComparison.Ordinal))
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
                }
            };

            var adapter = new TurnglassDesktopDispatchAdapter.DispatchAdapter();
            core.AddHostObjectToScript("turnglass", adapter.WrapObject(_bridge, adapter));
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "if (window === window.top && location.origin === 'https://turnglass.example') { Object.defineProperty(window, '__TURNGLASS_DESKTOP__', { value: Object.freeze({ version: 1 }), writable: false, configurable: false }); }")
                .AsTask().WaitAsync(_lifetime.Token).ConfigureAwait(true);
            _lifetime.Token.ThrowIfCancellationRequested();
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    ShowError("Turnglass could not load its frontend. Close and reopen the complete desktop folder.");
                }
            };
            _message.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            core.Navigate(Origin + "/index.html");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window also cancels startup.
        }
        catch (Exception exception)
        {
            LogError(exception);
            ShowError("Turnglass could not start. Check that Microsoft Edge WebView2 Runtime is installed and that the data folder is writable.\n\n" + exception.Message);
            _message.Children.Add(new HyperlinkButton
            {
                Content = "Install Microsoft Edge WebView2 Runtime",
                NavigateUri = new Uri("https://developer.microsoft.com/microsoft-edge/webview2/"),
            });
        }
    }

    private static bool IsTrustedOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "turnglass.example" && uri.Port == 443 && uri.UserInfo.Length == 0;

    private static bool IsTrustedDocument(string value) => IsTrustedOrigin(value) && new Uri(value).AbsolutePath is "/" or "/index.html";

    private async void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        TraceTest("Navigation " + args.Uri);
        if (IsTrustedDocument(args.Uri))
        {
            _bridge.CancelAll();
            return;
        }

        args.Cancel = true;
        if (args.IsUserInitiated && IsTrustedDocument(sender.Source))
        {
            await OpenExternalAsync(args.Uri).ConfigureAwait(true);
        }
    }

    private static async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.IsUserInitiated && IsTrustedDocument(sender.Source))
        {
            await OpenExternalAsync(args.Uri).ConfigureAwait(true);
        }
    }

    private static async Task OpenExternalAsync(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            try
            {
                await Launcher.LaunchUriAsync(uri);
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // A missing default browser should not close the viewer.
            }
        }
    }

    private async void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        TraceTest("Download " + args.DownloadOperation.Uri + " from " + sender.Source);
        using var deferral = args.GetDeferral();
        args.Handled = true;
        try
        {
            if (!IsTrustedDocument(sender.Source) || !args.DownloadOperation.Uri.StartsWith("blob:" + Origin + "/", StringComparison.Ordinal))
            {
                args.Cancel = true;
                return;
            }

            var extension = string.Equals(Path.GetExtension(args.ResultFilePath), ".json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".md";
            var name = Path.GetFileNameWithoutExtension(args.ResultFilePath);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "turnglass-insights";
            }

            if (_options is { TestMode: true, DownloadDirectory: not null })
            {
                Directory.CreateDirectory(_options.DownloadDirectory);
                args.ResultFilePath = Path.Combine(_options.DownloadDirectory, name + extension);
                return;
            }

            var picker = new FileSavePicker { SuggestedFileName = name, SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeChoices.Add(extension == ".json" ? "JSON" : "Markdown", new[] { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                args.Cancel = true;
            }
            else
            {
                args.ResultFilePath = file.Path;
            }
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            LogError(exception);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_stopped)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            await DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LogError(exception);
        }
        finally
        {
            _stopped = true;
            Close();
        }
    }

    public ValueTask DisposeAsync() => new(_shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        var drain = _bridge.StopAsync();
        try
        {
            // WebView2 startup waits observe the lifetime token, even if the native
            // runtime has not completed its underlying initialization operation.
            if (_initialization is not null)
            {
                await _initialization.ConfigureAwait(true);
            }

            _webView.CoreWebView2?.RemoveHostObjectFromScript("turnglass");
            _webView.Close();
            await drain.ConfigureAwait(true);
            if (_host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _host.StopAsync(timeout.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            // Never dispose SQLite services beneath an outstanding bridge call.
            await drain.ConfigureAwait(true);
            _host?.Dispose();
            _lifetime.Dispose();
        }
    }

    private void ShowError(string message)
    {
        TraceTest("Error " + message);
        _status.Text = message;
        _message.Visibility = Visibility.Visible;
        _webView.Visibility = Visibility.Collapsed;
    }

    private void LogError(Exception exception)
    {
        try
        {
            if (_options is not null)
            {
                File.AppendAllText(Path.Combine(_options.DataDirectory, "desktop-errors.log"), $"{DateTimeOffset.UtcNow:O} {exception}\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The error screen remains available if the data directory is not writable.
        }
    }

    private void TraceTest(string message)
    {
        if (_options is { TestMode: true })
        {
            try
            {
                File.AppendAllText(Path.Combine(_options.DataDirectory, "desktop-events.log"), message + "\n");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Test diagnostics must not affect application behavior.
            }
        }
    }
}
