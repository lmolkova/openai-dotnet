using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace OpenAI.Custom.Common.Instrumentation;

internal class StreamingScope : IDisposable
{
    private string _responseModel;
    private readonly InstrumentationScope _scope;
    private readonly bool _recordContent;
    private string _responseId;

    private int _reported;
    private ChatFinishReason? _finishReason = null;
    private ChatMessageRole? _role;
    private ChatTokenUsage _tokenUsage;
    private ContentBuffer _content;
    private List<ToolsBuffer> _tools;

    public StreamingScope(InstrumentationScope scope, bool recordContent)
    {
        _scope = scope;
        _recordContent = recordContent;
    }

    public void RecordChunk(StreamingChatCompletionUpdate chunk)
    {
        if (chunk.Model != null)
        {
            _responseModel = chunk.Model;
        }

        if (chunk.Id != null)
        {
            _responseId = chunk.Id;
        }

        if (chunk.Role != null)
        {
            _role = chunk.Role.Value;
        }

        if (chunk.ContentUpdate != null)
        {
            _content ??= new ContentBuffer();
            foreach (var u in chunk.ContentUpdate)
            {
                _content.Add(u);
            }
        }

        if (chunk.ToolCallUpdates != null)
        {
            foreach (var u in chunk.ToolCallUpdates)
            {
                EnsureToolBufferCapacity(u.Index);
                _tools[u.Index].Add(u);
            }
        }

        if (chunk.FinishReason != null)
        {
            _finishReason = chunk.FinishReason;
        }

        if (chunk.Usage != null)
        {
            _tokenUsage = chunk.Usage;
        }
    }

    private void EnsureToolBufferCapacity(int index)
    {
        _tools ??= new List<ToolsBuffer>();
        while (_tools.Count <= index)
        {
            _tools.Add(new ToolsBuffer());
        }
    }

    public void RecordException(Exception ex)
    {
        EndScope(_finishReason, ex, false);
    }

    public void RecordCancellation()
    {
        EndScope(_finishReason, null, true);
    }

    private void EndScope(ChatFinishReason? finishReason, Exception ex, bool canceled)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _scope.RecordStreamingChatCompletion(_responseId, _responseModel, finishReason, _role, [_content.ToContent()],
                _tools?.Select(t => t.ToCall()), _tokenUsage, ex, canceled);
            _scope.Dispose();
        }
    }

    public void Dispose()
    {
        EndScope(_finishReason, null, false);
    }

    private class ToolsBuffer
    {
        public string CallId { get; private set; }
        public string FunctionName { get; private set; }

        public StringBuilder Arguments { get; private set; }

        public void Add(StreamingChatToolCallUpdate chunk)
        {
            if (chunk.Id != null)
            {
                CallId = chunk.Id;
            }

            if (chunk.FunctionName != null)
            {
                FunctionName = chunk.FunctionName;
            }

            if (chunk.FunctionArgumentsUpdate != null)
            {
                Arguments ??= new StringBuilder();
                Arguments.Append(chunk.FunctionArgumentsUpdate);
            }
        }

        public ChatToolCall ToCall()
        {
            return new ChatToolCall(CallId, new InternalChatCompletionMessageToolCallFunction(FunctionName, Arguments.ToString()));
        }
    }

    private class ContentBuffer
    {
        public Uri ImageUrl { get; private set; }
        public ImageChatMessageContentPartDetail? ImageDetail { get; private set; }

        public StringBuilder Text { get; private set; }
        public ChatMessageContentPartKind? Kind { get; private set; }

        public void Add(ChatMessageContentPart chunk)
        {
            if (chunk.Kind != default)
            {
                Kind = chunk.Kind;
            }

            if (chunk.ImageDetail != default)
            {
                ImageDetail = chunk.ImageDetail;
            }

            if (chunk.ImageUri != null)
            {
                ImageUrl = chunk.ImageUri;
            }

            if (chunk.Text != null)
            {
                Text ??= new StringBuilder();
                Text.Append(chunk.Text);
            }
        }

        public ChatMessageContentPart ToContent()
        {
            return Kind == ChatMessageContentPartKind.Image ? new ChatMessageContentPart(ImageUrl, ImageDetail) : new ChatMessageContentPart(Text?.ToString() ?? "");
        }
    }
}
