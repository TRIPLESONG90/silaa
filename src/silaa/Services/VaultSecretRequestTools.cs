using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed record VaultSecretRequestResult(bool Stored, string Name);

internal sealed class VaultSecretRequestBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VaultSecretRequestResult>> _pending =
        new(StringComparer.Ordinal);

    public async Task<VaultSecretRequestResult> RequestAsync(
        string sessionId,
        string name,
        string reason,
        Action<ChatRunEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var requestId = $"vault_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<VaultSecretRequestResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException("Vault 키 요청을 만들지 못했습니다.");
        var request = new VaultSecretRequest
        {
            RequestId = requestId,
            Name = name,
            Reason = reason
        };
        onEvent(new ChatRunEvent("vault_secret_request", JsonSerializer.Serialize(request), sessionId, requestId));
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { return await completion.Task; }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public bool Submit(string requestId, bool stored, string name)
    {
        if (!_pending.TryGetValue(requestId, out var completion)) return false;
        var normalizedName = stored ? VaultStore.NormalizeName(name) : name.Trim();
        return completion.TrySetResult(new VaultSecretRequestResult(stored, normalizedName));
    }
}

internal sealed class VaultSecretRequestTools(
    string sessionId,
    LocalStore store,
    VaultSecretRequestBroker broker,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<string>>)RequestVaultSecretAsync,
            "request_vault_secret",
            "Ask the user to securely enter one missing API key or token into Vault. " +
            "Use this instead of asking for secret text in chat. The secret is never returned to the model.")
    ];

    public async Task<string> RequestVaultSecretAsync(
        [Description("Vault entry and environment variable name, for example GITHUB_TOKEN.")] string name,
        [Description("Plain-language explanation of why this task needs the key and where it will be used.")] string reason,
        CancellationToken cancellationToken = default)
    {
        name = VaultStore.NormalizeName(name);
        reason = reason.Trim();
        if (reason.Length is < 1 or > 500)
            throw new ArgumentException("Reason must contain 1 to 500 characters.");
        if (store.Vault.Contains(name))
            return $"Vault entry '{name}' is already available. Its secret value was not read or returned.";
        if (onEvent is null)
            return "Interactive Vault input is unavailable in this background run. Ask the user to open a foreground conversation.";

        VaultSecretRequestResult result;
        using (runTimeout?.Pause())
        {
            result = await broker.RequestAsync(sessionId, name, reason, onEvent, cancellationToken);
        }
        return result.Stored
            ? $"The user securely stored Vault entry '{result.Name}'. The secret value was not returned. Use the entry name with run_command_with_vault."
            : $"The user declined to store Vault entry '{name}'. Do not ask them to paste the secret into chat.";
    }
}
