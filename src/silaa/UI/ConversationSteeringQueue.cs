namespace silaa.UI;

internal sealed class ConversationSteeringQueue
{
    private readonly Queue<QueuedConversationPrompt> _pending = new();

    public bool IsActive { get; private set; }
    public bool StopRequested { get; private set; }
    public bool SteeringRequested { get; private set; }
    public int PendingCount => _pending.Count;

    public void Start()
    {
        if (IsActive) throw new InvalidOperationException("A conversation run is already active.");
        IsActive = true;
        StopRequested = false;
        SteeringRequested = false;
    }

    public int Enqueue(string prompt) => Enqueue(prompt, "");

    public int Enqueue(string userPrompt, string agentContext)
    {
        if (!IsActive) throw new InvalidOperationException("No conversation run is active.");
        userPrompt = userPrompt.Trim();
        agentContext = agentContext.Trim();
        if (userPrompt.Length == 0) throw new ArgumentException("Queued prompt cannot be empty.");
        _pending.Enqueue(new QueuedConversationPrompt(userPrompt, agentContext));
        SteeringRequested = true;
        return _pending.Count;
    }

    public void BeginPrompt() => SteeringRequested = false;

    public bool ShouldContinueAfterCancellation =>
        SteeringRequested && !StopRequested && _pending.Count > 0;

    public bool TryTakeNext(out string prompt)
    {
        var found = TryTakeNext(out prompt, out _);
        return found;
    }

    public bool TryTakeNext(out string userPrompt, out string agentContext)
    {
        if (_pending.TryDequeue(out var next))
        {
            userPrompt = next.UserPrompt;
            agentContext = next.AgentContext;
            return true;
        }
        userPrompt = "";
        agentContext = "";
        return false;
    }

    public IReadOnlyList<string> RequestStop()
    {
        StopRequested = true;
        SteeringRequested = false;
        return DrainPending();
    }

    public IReadOnlyList<string> Finish()
    {
        var remaining = DrainPending();
        IsActive = false;
        StopRequested = false;
        SteeringRequested = false;
        return remaining;
    }

    private IReadOnlyList<string> DrainPending()
    {
        var items = _pending.Select(item => item.UserPrompt).ToList();
        _pending.Clear();
        return items;
    }

    private sealed record QueuedConversationPrompt(string UserPrompt, string AgentContext);
}
