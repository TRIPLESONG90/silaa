using System.Text.Json;

namespace silaa.Services;

/// <summary>
/// Stops only an unmistakable tool loop. Long reasoning and varied tool work are intentionally unrestricted.
/// </summary>
internal sealed class AgentProgressGuard
{
    internal const int RepeatedIdenticalToolCallLimit = 5;
    internal const int RepeatedNoProgressResultLimit = 4;
    internal const int HardToolCallLimit = 32;
    private readonly HashSet<string> _seenCallIds = new(StringComparer.Ordinal);
    private string _previousSignature = "";
    private int _repeatCount;
    private string _previousFailure = "";
    private int _failureCount;
    private int _toolCallCount;

    public bool ObserveToolCall(string? callId, string name, IDictionary<string, object?>? arguments)
    {
        if (!string.IsNullOrWhiteSpace(callId) && !_seenCallIds.Add(callId)) return false;
        _toolCallCount++;
        if (_toolCallCount >= HardToolCallLimit) return true;
        var signature = name + "\n" + (arguments is { Count: > 0 }
            ? JsonSerializer.Serialize(arguments)
            : "{}");
        if (signature.Equals(_previousSignature, StringComparison.Ordinal))
            _repeatCount++;
        else
        {
            _previousSignature = signature;
            _repeatCount = 1;
        }
        return _repeatCount >= RepeatedIdenticalToolCallLimit;
    }

    public bool ObserveToolResult(object? result, Exception? exception)
    {
        var failure = FailureFingerprint(result, exception);
        if (failure.Length == 0)
        {
            _previousFailure = "";
            _failureCount = 0;
            return false;
        }

        if (failure.Equals(_previousFailure, StringComparison.Ordinal)) _failureCount++;
        else
        {
            _previousFailure = failure;
            _failureCount = 1;
        }
        return _failureCount >= RepeatedNoProgressResultLimit;
    }

    private static string FailureFingerprint(object? result, Exception? exception)
    {
        if (exception is not null) return "exception:" + exception.GetType().Name;
        var text = result switch
        {
            null => "",
            string value => value,
            _ => JsonSerializer.Serialize(result)
        };
        if (text.Length == 0) return "";
        var lower = text.ToLowerInvariant();
        var category = lower switch
        {
            _ when lower.Contains("access_denied") || lower.Contains("denied") => "denied",
            _ when lower.Contains("not found") || lower.Contains("cannot find") || lower.Contains("was not found") => "not-found",
            _ when lower.Contains("exit code: 1") || lower.Contains("exit code: 2") || lower.Contains("exit code: 128") => "command-failed",
            _ when lower.Contains("error") || lower.Contains("exception") || lower.Contains("failed") || lower.Contains("unavailable") => "error",
            _ => ""
        };
        return category;
    }
}
