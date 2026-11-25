using System.Runtime.CompilerServices;
using System.Text.Json;
using Windows.Foundation;

[assembly: InternalsVisibleTo("Turnglass.Desktop.Tests")]

namespace Turnglass.DesktopBridge;

/// <summary>The narrow WinRT surface projected into the trusted desktop document.</summary>
public sealed partial class NativeBridge
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RequestOperation> _requests = new(StringComparer.Ordinal);
    private readonly HashSet<RequestOperation> _operations = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, CancellationToken, Task<string>>? _handler;
    private CancellationToken _lifetime;
    private CancellationTokenRegistration _lifetimeRegistration;
    private bool _stopping;

    internal void Configure(Func<string, CancellationToken, Task<string>> handler, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_handler is not null || _stopping)
            {
                throw new InvalidOperationException("The desktop bridge cannot be configured again or after shutdown.");
            }

            _handler = handler;
            _lifetime = lifetime;
            _lifetimeRegistration = lifetime.Register(static state => ((NativeBridge)state!).CancelAll(), this);
        }
    }

    public IAsyncOperation<string> InvokeAsync(string requestJson)
    {
        return InvokeCoreAsync(requestJson).AsAsyncOperation();
    }

    public void Cancel(string requestId)
    {
        RequestOperation? operation;
        lock (_gate)
        {
            _requests.TryGetValue(requestId, out operation);
        }

        operation?.Cancel();
    }

    internal void CancelAll()
    {
        RequestOperation[] operations;
        lock (_gate)
        {
            operations = [.. _operations];
        }

        Cancel(operations);
    }

    internal async Task StopAsync()
    {
        RequestOperation[] operations;
        lock (_gate)
        {
            _stopping = true;
            operations = [.. _operations];
            if (operations.Length == 0)
            {
                _drained.TrySetResult();
            }
        }

        Cancel(operations);
        await _drained.Task.ConfigureAwait(false);
        await _lifetimeRegistration.DisposeAsync().ConfigureAwait(false);
    }

    internal Task<string> InvokeCoreAsync(string requestJson)
    {
        var id = GetRequestId(requestJson);
        RequestOperation operation;
        Func<string, CancellationToken, Task<string>> handler;
        lock (_gate)
        {
            if (_stopping)
            {
                return Task.FromException<string>(new InvalidOperationException("Turnglass is stopping."));
            }

            if (_handler is null)
            {
                return Task.FromException<string>(new InvalidOperationException("Turnglass is still starting."));
            }

            if (_lifetime.IsCancellationRequested)
            {
                return Task.FromCanceled<string>(_lifetime);
            }

            if (id is not null && _requests.ContainsKey(id))
            {
                return Task.FromException<string>(new ArgumentException("The request identifier is already in use.", nameof(requestJson)));
            }

            handler = _handler;
            operation = new RequestOperation(id);
            _operations.Add(operation);
            if (id is not null)
            {
                _requests.Add(id, operation);
            }
        }

        // Even invalid envelopes are tracked until their dispatcher validation has finished.
        _ = ExecuteAsync(operation, handler, requestJson);
        return operation.Completion.Task;
    }

    private async Task ExecuteAsync(RequestOperation operation, Func<string, CancellationToken, Task<string>> handler, string requestJson)
    {
        string? result = null;
        Exception? failure = null;
        try
        {
            result = await Task.Run(() => handler(requestJson, operation.Token), operation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        operation.Dispose();
        lock (_gate)
        {
            if (operation.Id is not null)
            {
                _requests.Remove(operation.Id);
            }
        }

        if (failure is OperationCanceledException canceled)
        {
            operation.Completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (failure is not null)
        {
            operation.Completion.TrySetException(failure);
        }
        else
        {
            operation.Completion.TrySetResult(result!);
        }

        lock (_gate)
        {
            _operations.Remove(operation);
            if (_stopping && _operations.Count == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private static void Cancel(RequestOperation[] operations)
    {
        foreach (var operation in operations)
        {
            operation.Cancel();
        }
    }

    private static string? GetRequestId(string requestJson)
    {
        if (string.IsNullOrEmpty(requestJson) || requestJson.Length > 1_048_576)
        {
            return null;
        }

        try
        {
            using var request = JsonDocument.Parse(requestJson);
            if (request.RootElement.ValueKind == JsonValueKind.Object &&
                request.RootElement.TryGetProperty("id", out var property) && property.ValueKind == JsonValueKind.String &&
                property.GetString() is { Length: > 0 and <= 128 } id && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is handled by the dispatcher without registering a cancellation ID.
        }

        return null;
    }

    private sealed class RequestOperation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private bool _cancellationRequested;
        private bool _cancelling;
        private bool _completed;

        internal string? Id { get; }
        internal CancellationToken Token { get; }
        internal TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RequestOperation(string? id)
        {
            Id = id;
            Token = _cancellation.Token;
        }

        internal void Cancel()
        {
            lock (_gate)
            {
                if (_completed || _cancellationRequested)
                {
                    return;
                }

                _cancellationRequested = true;
                _cancelling = true;
            }

            try
            {
                // Callbacks may finish a backend operation inline; hold neither the bridge nor operation lock.
                _cancellation.Cancel();
            }
            catch (AggregateException exception)
            {
                Completion.TrySetException(exception);
            }
            finally
            {
                bool dispose;
                lock (_gate)
                {
                    _cancelling = false;
                    dispose = _completed;
                }

                if (dispose)
                {
                    _cancellation.Dispose();
                }
            }
        }

        public void Dispose()
        {
            bool dispose;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                dispose = !_cancelling;
            }

            if (dispose)
            {
                _cancellation.Dispose();
            }
        }
    }
}
