extern alias DesktopBridgeComponent;

using System.Globalization;
using NativeBridge = DesktopBridgeComponent::Turnglass.DesktopBridge.NativeBridge;

namespace Turnglass.Desktop.Tests;

public sealed class NativeBridgeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicWinRtOperationReturnsBackendResult()
    {
        var bridge = new NativeBridge();
        bridge.Configure((_, _) => Task.FromResult("result"), CancellationToken.None);
        Assert.Equal("result", await bridge.InvokeAsync(Request("public")).AsTask().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken).ConfigureAwait(true));
        await bridge.StopAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task CancelAllAllowsInlineCompletionOfEveryTrackedRequest()
    {
        const int Count = 32;
        var started = Signal();
        var calls = 0;
        var bridge = new NativeBridge();
        bridge.Configure((_, token) =>
        {
            // Synchronous continuations deliberately finish requests inside the cancellation callback.
            var completion = new TaskCompletionSource<string>();
            token.Register(() => completion.TrySetResult("cancelled inline"));
            if (Interlocked.Increment(ref calls) == Count)
            {
                started.TrySetResult();
            }

            return completion.Task;
        }, CancellationToken.None);
        var requests = Enumerable.Range(0, Count).Select(index => bridge.InvokeCoreAsync(Request(index.ToString(CultureInfo.InvariantCulture)))).ToArray();
        await Bounded(started.Task).ConfigureAwait(true);
        bridge.CancelAll();
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken).ConfigureAwait(true);
        Assert.All(results, result => Assert.Equal("cancelled inline", result));
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("{\"id\":\"valid\"}")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"\"}")]
    public async Task StopWaitsForBackendCompletionIncludingInvalidEnvelopes(string envelope)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new NativeBridge();
        bridge.Configure((_, token) =>
        {
            entered.TrySetResult(token);
            return release.Task; // Models synchronous SQLite work that cannot complete merely by cancelling its token.
        }, CancellationToken.None);
        var request = bridge.InvokeCoreAsync(envelope);
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken).ConfigureAwait(true);
        var stopped = bridge.StopAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.False(stopped.IsCompleted);
        Assert.False(request.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeCoreAsync(Request("late"))).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeCoreAsync("invalid late request")).ConfigureAwait(true);
        release.SetResult("finished");
        await Bounded(stopped).ConfigureAwait(true);
        Assert.True(request.IsCompletedSuccessfully);
        Assert.Equal("finished", await request.ConfigureAwait(true));
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
        Assert.Throws<InvalidOperationException>(() => bridge.Configure((_, _) => Task.FromResult("late"), CancellationToken.None));
    }

    [Fact]
    public async Task RequestCanFinishWhileCancellationCallbackIsStillRunning()
    {
        var entered = Signal();
        var callbackEntered = Signal();
        var response = new TaskCompletionSource<string>();
        using var releaseCallback = new ManualResetEventSlim();
        var bridge = new NativeBridge();
        bridge.Configure((_, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10), CancellationToken));
            });
            entered.TrySetResult();
            return response.Task;
        }, CancellationToken.None);
        var request = bridge.InvokeCoreAsync(Request("overlap"));
        await Bounded(entered.Task).ConfigureAwait(true);
        var cancellation = Task.Run(() => bridge.Cancel("overlap"), CancellationToken);
        try
        {
            await Bounded(callbackEntered.Task).ConfigureAwait(true);
            response.SetResult("finished during cancellation");
            Assert.Equal("finished during cancellation", await request.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken).ConfigureAwait(true));
            Assert.False(cancellation.IsCompleted);
            // A concurrent second cancellation must not touch a completed/disposed source.
            bridge.Cancel("overlap");
        }
        finally
        {
            releaseCallback.Set();
        }

        await Bounded(cancellation).ConfigureAwait(true);
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
    }

    [Fact]
    public async Task ConcurrentCancellationAndCompletionDoNotDisposeAnActiveSource()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var entered = Signal();
            var response = new TaskCompletionSource<string>();
            var bridge = new NativeBridge();
            bridge.Configure((_, token) =>
            {
                token.Register(() => response.TrySetResult("cancelled"));
                entered.TrySetResult();
                return response.Task;
            }, CancellationToken.None);
            var request = bridge.InvokeCoreAsync(Request("race"));
            await Bounded(entered.Task).ConfigureAwait(true);
            var cancel = Task.Run(() => bridge.Cancel("race"), CancellationToken);
            var finish = Task.Run(() => response.TrySetResult("finished"), CancellationToken);
            await Bounded(Task.WhenAll(cancel, finish, request)).ConfigureAwait(true);
            bridge.Cancel("race");
            await Bounded(bridge.StopAsync()).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LifetimeCancellationCancelsWorkAndRejectsNewCalls()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = Signal();
        var bridge = new NativeBridge();
        bridge.Configure(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "unreachable";
        }, lifetime.Token);
        var request = bridge.InvokeCoreAsync(Request("lifetime"));
        await Bounded(entered.Task).ConfigureAwait(true);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request).ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.InvokeCoreAsync("invalid after lifetime")).ConfigureAwait(true);
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
    }

    [Fact]
    public async Task DuplicateIdsAreRejectedOnlyWhileThePreviousRequestIsActive()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new NativeBridge();
        bridge.Configure((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }, CancellationToken.None);
        var first = bridge.InvokeCoreAsync(Request("same"));
        await Bounded(entered.Task).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.InvokeCoreAsync(Request("same"))).ConfigureAwait(true);
        release.SetResult("done");
        await Bounded(first).ConfigureAwait(true);
        Assert.Equal("done", await bridge.InvokeCoreAsync(Request("same")).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken).ConfigureAwait(true));
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
    }

    [Fact]
    public async Task BackendFailuresAreObservedAndDoNotPreventDraining()
    {
        var bridge = new NativeBridge();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeCoreAsync(Request("startup"))).ConfigureAwait(true);
        bridge.Configure((_, _) => Task.FromException<string>(new IOException("synthetic failure")), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<IOException>(() => bridge.InvokeCoreAsync(Request("failure"))).ConfigureAwait(true);
        Assert.Equal("synthetic failure", exception.Message);
        await Bounded(bridge.StopAsync()).ConfigureAwait(true);
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotHideOutstandingBackendWorkFromStop()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new NativeBridge();
        bridge.Configure((_, token) =>
        {
            token.Register(() => throw new InvalidOperationException("callback failure"));
            entered.TrySetResult();
            return release.Task;
        }, CancellationToken.None);
        var request = bridge.InvokeCoreAsync(Request("bad-callback"));
        await Bounded(entered.Task).ConfigureAwait(true);
        bridge.CancelAll();
        await Assert.ThrowsAsync<AggregateException>(() => request).ConfigureAwait(true);
        var stopped = bridge.StopAsync();
        Assert.False(stopped.IsCompleted);
        release.SetResult("backend finally completed");
        await Bounded(stopped).ConfigureAwait(true);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

    private static string Request(string id) => $$"""{"id":"{{id}}"}""";
}
