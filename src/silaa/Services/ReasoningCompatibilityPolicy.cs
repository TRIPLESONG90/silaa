using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json.Nodes;

namespace silaa.Services;

/// <summary>
/// Adds portable reasoning controls and adapts Ollama's streamed `reasoning` field to the
/// `reasoning_content` field consumed by the OpenAI Microsoft.Extensions.AI adapter.
/// </summary>
internal sealed class ReasoningCompatibilityPolicy(string effort, bool enableGemmaThinking) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        RewriteRequest(message, asynchronous: false).GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
        RewriteResponse(message);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await RewriteRequest(message, asynchronous: true).ConfigureAwait(false);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        RewriteResponse(message);
    }

    private async ValueTask RewriteRequest(PipelineMessage message, bool asynchronous)
    {
        if (string.IsNullOrWhiteSpace(effort) && !enableGemmaThinking) return;
        var content = message.Request.Content;
        if (content is null) return;
        using var buffer = new MemoryStream();
        if (asynchronous) await content.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);
        else content.WriteTo(buffer, message.CancellationToken);
        var root = JsonNode.Parse(buffer.ToArray()) as JsonObject;
        if (root is null) return;

        if (!string.IsNullOrWhiteSpace(effort)) root["reasoning_effort"] = effort;
        if (enableGemmaThinking && root["messages"] is JsonArray messages)
        {
            var system = messages.OfType<JsonObject>()
                .FirstOrDefault(item => item["role"]?.GetValue<string>() is "system" or "developer");
            if (system?["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var text) &&
                !text.StartsWith("<|think|>", StringComparison.Ordinal))
                system["content"] = $"<|think|>\n{text}";
        }
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(root.ToJsonString()));
    }

    private static void RewriteResponse(PipelineMessage message)
    {
        var response = message.Response;
        if (response?.ContentStream is { } stream)
            response.ContentStream = new ReasoningFieldStream(stream);
    }

    private sealed class ReasoningFieldStream(Stream inner) : Stream
    {
        private readonly StreamReader _reader = new(inner, Encoding.UTF8, true, 4096, leaveOpen: false);
        private byte[] _pending = [];
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!EnsurePending()) return 0;
            var copied = Math.Min(count, _pending.Length - _offset);
            _pending.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset, copied));
            _offset += copied;
            return copied;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await EnsurePendingAsync(cancellationToken).ConfigureAwait(false)) return 0;
            var copied = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private bool EnsurePending()
        {
            if (_offset < _pending.Length) return true;
            var line = _reader.ReadLine();
            if (line is null) return false;
            SetPending(line);
            return true;
        }

        private async ValueTask<bool> EnsurePendingAsync(CancellationToken cancellationToken)
        {
            if (_offset < _pending.Length) return true;
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return false;
            SetPending(line);
            return true;
        }

        private void SetPending(string line)
        {
            line = line.Replace("\"reasoning\":", "\"reasoning_content\":", StringComparison.Ordinal);
            _pending = Encoding.UTF8.GetBytes(line + "\n");
            _offset = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _reader.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
