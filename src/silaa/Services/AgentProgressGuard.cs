using System.Text.Json;

namespace silaa.Services;

/// <summary>
/// Stops only an unmistakable tool loop. Long reasoning and varied tool work are intentionally unrestricted.
/// </summary>
internal sealed class AgentProgressGuard
{
    internal const int RepeatedIdenticalToolCallLimit = 5;
    private readonly HashSet<string> _seenCallIds = new(StringComparer.Ordinal);
    private string _previousSignature = "";
    private int _repeatCount;

    public bool ObserveToolCall(string? callId, string name, IDictionary<string, object?>? arguments)
    {
        if (!string.IsNullOrWhiteSpace(callId) && !_seenCallIds.Add(callId)) return false;
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
}
