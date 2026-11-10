using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace silaa.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task? _listener;
    private Action? _activationHandler;
    private bool _activationPending;
    private bool _disposed;

    public SingleInstanceCoordinator(string? instanceKey = null)
    {
        var key = string.IsNullOrWhiteSpace(instanceKey) ? UserScopedKey() : SafeKey(instanceKey);
        _pipeName = $"silaa-single-{key}";
        _mutex = new Mutex(initiallyOwned: true, $"Local\\silaa-single-{key}", out var createdNew);
        IsPrimary = createdNew;
        if (IsPrimary) _listener = Task.Run(ListenAsync);
    }

    public bool IsPrimary { get; }

    public void SetActivationHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var invoke = false;
        lock (_gate)
        {
            _activationHandler = handler;
            if (_activationPending)
            {
                _activationPending = false;
                invoke = true;
            }
        }
        if (invoke) handler();
    }

    public async Task<bool> SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimary) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync("activate".AsMemory(), timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
                var command = await reader.ReadLineAsync(_stop.Token);
                if (command?.Equals("activate", StringComparison.Ordinal) == true) RaiseActivation();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (IOException) when (!_stop.IsCancellationRequested) { }
        }
    }

    private void RaiseActivation()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _activationHandler;
            if (handler is null) _activationPending = true;
        }
        handler?.Invoke();
    }

    private static string UserScopedKey()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    }

    private static string SafeKey(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (safe.Length == 0) throw new ArgumentException("Single-instance key is empty.", nameof(value));
        return safe.Length > 80 ? safe[..80] : safe;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException exception) when (exception.InnerExceptions.All(item => item is OperationCanceledException)) { }
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _stop.Dispose();
    }
}
