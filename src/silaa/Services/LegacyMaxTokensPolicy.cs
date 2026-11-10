using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace silaa.Services;

/// <summary>Adapts the OpenAI SDK's max_completion_tokens field for compatible APIs that require max_tokens.</summary>
internal sealed class LegacyMaxTokensPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message, false).GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await Rewrite(message, true).ConfigureAwait(false);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static async ValueTask Rewrite(PipelineMessage message, bool asynchronous)
    {
        var content = message.Request.Content;
        if (content is null) return;
        using var buffer = new MemoryStream();
        if (asynchronous) await content.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);
        else content.WriteTo(buffer, message.CancellationToken);
        var root = JsonNode.Parse(buffer.ToArray()) as JsonObject;
        if (root is null || !root.Remove("max_completion_tokens", out var value)) return;
        root["max_tokens"] = value;
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(root.ToJsonString()));
    }
}
