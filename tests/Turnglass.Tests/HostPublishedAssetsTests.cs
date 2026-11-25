using System.Collections.Concurrent;
using System.Diagnostics;

namespace Turnglass.Tests;

public sealed class HostPublishedAssetsTests
{
    [Fact]
    public async Task StaticAssetsResolveBesideExecutableFromUnrelatedWorkingDirectory()
    {
        var executable = FindWebExecutable();
        var unrelatedDirectory = Path.Combine(Path.GetTempPath(), "Turnglass-cwd-" + Guid.NewGuid().ToString("N"));
        var codexHome = Path.Combine(unrelatedDirectory, "codex");
        var webRoot = Path.Combine(Path.GetDirectoryName(executable)!, "wwwroot");
        var webRootExisted = Directory.Exists(webRoot);
        var fileName = "asset-" + Guid.NewGuid().ToString("N") + ".txt";
        var assetPath = Path.Combine(webRoot, fileName);
        try
        {
            Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
            Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
            Directory.CreateDirectory(webRoot);
            await File.WriteAllTextAsync(assetPath, "published-asset", TestContext.Current.CancellationToken).ConfigureAwait(true);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = unrelatedDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[] { executable, "--data-dir", Path.Combine(unrelatedDirectory, "data"), "--urls", "http://127.0.0.1:0", "--environment", "Production", "--Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information", "--Logging:Console:FormatterName", "simple" })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["CODEX_HOME"] = codexHome;
            start.Environment["TURNGLASS_COPILOT_AUTO_DISCOVER"] = "false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = new Process { StartInfo = start };
            var output = new ConcurrentQueue<string>();
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not { } line)
                {
                    listening.TrySetException(new InvalidOperationException("The web process closed stdout before reporting its address."));
                    return;
                }

                output.Enqueue(line);
                const string prefix = "Now listening on: ";
                var position = line.IndexOf(prefix, StringComparison.Ordinal);
                if (position >= 0 && Uri.TryCreate(line[(position + prefix.Length)..].Trim(), UriKind.Absolute, out var address))
                {
                    listening.TrySetResult(address);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is { } line)
                {
                    output.Enqueue(line);
                }
            };

            Assert.True(process.Start());
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var client = new HttpClient { BaseAddress = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true) };
                var content = await client.GetStringAsync("/" + fileName, timeout.Token).ConfigureAwait(true);
                Assert.Equal("published-asset", content);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(true);
                TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, output));
            }
        }
        finally
        {
            File.Delete(assetPath);
            if (!webRootExisted && Directory.Exists(webRoot) && !Directory.EnumerateFileSystemEntries(webRoot).Any())
            {
                Directory.Delete(webRoot);
            }

            if (Directory.Exists(unrelatedDirectory))
            {
                Directory.Delete(unrelatedDirectory, true);
            }
        }
    }

    private static string FindWebExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent?.Name ?? "Debug";
        for (var directory = output; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Turnglass.Web", "bin", configuration, "net10.0", "Turnglass.Web.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Build the web project before running its subprocess tests.");
    }
}
