using NUnit.Framework;
using OpenAI.Chat;
using OpenAI.Tests.Telemetry;
using OpenAI.Tests.Utility;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAI.Tests.Chat;

[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
[Category("Chat")]
[Category("Smoke")]
public class ChatMockTests : SyncAsyncTestBase
{
    private static readonly ApiKeyCredential s_fakeCredential = new ApiKeyCredential("key");

    public ChatMockTests(bool isAsync) : base(isAsync)
    {
    }

    private static readonly List<ChatMessage> s_messages = new()
    {
        new UserChatMessage("Message content.")
    };

    [Test]
    public async Task CompleteChatDeserializesId()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "id": "chat_id"
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.Id, Is.EqualTo("chat_id"));
    }

    [Test]
    public async Task CompleteChatDeserializesCreatedAt()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "created": 1704096000
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.CreatedAt.ToUnixTimeSeconds(), Is.EqualTo(1704096000));
    }

    [Test]
    public async Task CompleteChatDeserializesModel()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "model": "model_name"
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.Model, Is.EqualTo("model_name"));
    }

    [Test]
    public async Task CompleteChatDeserializesSystemFingerprint()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "system_fingerprint": "fingerprint_value"
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.SystemFingerprint, Is.EqualTo("fingerprint_value"));
    }

    [Test]
    public async Task CompleteChatDeserializesUsage()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 20,
                "total_tokens": 30
            }
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.Usage.InputTokenCount, Is.EqualTo(10));
        Assert.That(chatCompletion.Usage.OutputTokenCount, Is.EqualTo(20));
        Assert.That(chatCompletion.Usage.TotalTokenCount, Is.EqualTo(30));
    }

    [Test]
    [TestCase("stop", ChatFinishReason.Stop)]
    [TestCase("length", ChatFinishReason.Length)]
    [TestCase("content_filter", ChatFinishReason.ContentFilter)]
    [TestCase("tool_calls", ChatFinishReason.ToolCalls)]
    [TestCase("function_call", ChatFinishReason.FunctionCall)]
    public async Task CompleteChatDeserializesFinishReason(string stringReason, ChatFinishReason expectedReason)
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, $$"""
        {
            "choices": [
                {
                    "finish_reason": "{{stringReason}}"
                }
            ]
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.FinishReason, Is.EqualTo(expectedReason));
    }

    [Test]
    [TestCase("system", ChatMessageRole.System)]
    [TestCase("user", ChatMessageRole.User)]
    [TestCase("assistant", ChatMessageRole.Assistant)]
    [TestCase("tool", ChatMessageRole.Tool)]
    [TestCase("function", ChatMessageRole.Function)]
    public async Task CompleteChatDeserializesRole(string stringRole, ChatMessageRole expectedRole)
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, $$"""
        {
            "choices": [
                {
                    "message": {
                        "role": "{{stringRole}}"
                    }
                }
            ]
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);

        Assert.That(chatCompletion.Role, Is.EqualTo(expectedRole));
    }

    [Test]
    public async Task CompleteChatDeserializesTextContent()
    {
        OpenAIClientOptions clientOptions = GetClientOptionsWithMockResponse(200, """
        {
            "choices": [
                {
                    "message": {
                        "content": "This is the content."
                    }
                }
            ]
        }
        """);
        ChatClient client = new ChatClient("model", s_fakeCredential, clientOptions);

        ChatCompletion chatCompletion = IsAsync
            ? await client.CompleteChatAsync(s_messages)
            : client.CompleteChat(s_messages);
        ChatMessageContentPart contentPart = chatCompletion.Content.Single();

        Assert.That(contentPart.Kind, Is.EqualTo(ChatMessageContentPartKind.Text));
        Assert.That(contentPart.Text, Is.EqualTo("This is the content."));
    }

    [Test]
    public void CompleteChatRespectsTheCancellationToken()
    {
        ChatClient client = new ChatClient("model", s_fakeCredential);
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        if (IsAsync)
        {
            Assert.That(async () => await client.CompleteChatAsync(s_messages, cancellationToken: cancellationSource.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }
        else
        {
            Assert.That(() => client.CompleteChat(s_messages, cancellationToken: cancellationSource.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }
    }

    [Test]
    public void CompleteChatStreamingAsyncRespectsTheCancellationToken()
    {
        AssertAsyncOnly();

        ChatClient client = new ChatClient("model", s_fakeCredential);
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        IAsyncEnumerator<StreamingChatCompletionUpdate> enumerator = client
            .CompleteChatStreamingAsync(s_messages, cancellationToken: cancellationSource.Token)
            .GetAsyncEnumerator();

        Assert.That(async () => await enumerator.MoveNextAsync(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void CompleteChatStreamingRespectsTheCancellationToken()
    {
        AssertSyncOnly();

        ChatClient client = new ChatClient("model", s_fakeCredential);
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        IEnumerator<StreamingChatCompletionUpdate> enumerator = client
            .CompleteChatStreaming(s_messages, cancellationToken: cancellationSource.Token)
            .GetEnumerator();

        Assert.That(() => enumerator.MoveNext(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    [NonParallelizable]
    public void ChatStreamingTransportErrorWithTracingAndMetrics()
    {
        AssertAsyncOnly();

        BinaryData content = BinaryData.FromString(
            """
            data: {"id":"chatcmpl-A7mKGugwaczn3YyrJLlZY6CM0Wlkr","object":"chat.completion.chunk","created":1726417424,"model":"gpt-4o-mini-2024-07-18","choices":[{"index":0,"delta":{"role":"assistant","content":""}}],"usage":null}
            """);

        MockPipelineResponse response = new(200)
        {
            ContentStream = new TestStream(content, () => new SocketException())
        };

        OpenAIClientOptions options = new OpenAIClientOptions() 
        {
            Transport = new MockPipelineTransport(response)
        };
        
        ChatClient client = new ChatClient("gpt-4o-mini", s_fakeCredential, options);

        using TestActivityListener activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using TestMeterListener meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        AsyncCollectionResult<StreamingChatCompletionUpdate> streamingResult = 
            client.CompleteChatStreamingAsync(new UserChatMessage("Hello, world!"));

        TestResponseInfo testResponseInfo = new() 
        { 
            ErrorType = typeof(SocketException).FullName
        };

        Assert.ThrowsAsync<SocketException>(async () =>
        {
            await foreach (StreamingChatCompletionUpdate chatUpdate in streamingResult)
            {
                testResponseInfo.WithStreamingUpdate(chatUpdate);
            }
        });

        activityListener.ValidateChatActivity(testResponseInfo);
        meterListener.ValidateDuration(testResponseInfo);
        meterListener.ValidateUsage(testResponseInfo);
    }

    private async ValueTask<StreamingChatCompletionUpdate> InvokeCompleteChatStreamingAsync(ChatClient client)
    {
        if (IsAsync)
        {
            IAsyncEnumerator<StreamingChatCompletionUpdate> enumerator = client
                .CompleteChatStreamingAsync(s_messages)
                .GetAsyncEnumerator();

            await enumerator.MoveNextAsync();
            return enumerator.Current;
        }
        else
        {
            IEnumerator<StreamingChatCompletionUpdate> enumerator = client
                .CompleteChatStreaming(s_messages)
                .GetEnumerator();

            enumerator.MoveNext();
            return enumerator.Current;
        }
    }

    private OpenAIClientOptions GetClientOptionsWithMockResponse(int status, string content)
    {
        MockPipelineResponse response = new MockPipelineResponse(status);
        response.SetContent(content);

        return new OpenAIClientOptions()
        {
            Transport = new MockPipelineTransport(response)
        };
    }

    private class TestStream : Stream
    {
        private readonly byte[] _content;
        private readonly Action _callback;
        private long _position;
        public TestStream(BinaryData content, Action callback)
        {
            _content = content.ToArray();
            _position = 0;
            _callback = callback;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.Length + 1;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == _content.Length)
            {
                _callback();
                _position++;
                return 1;
            }

            int read = 0;
            for (; read < count && read < _content.Length && _position < _content.Length; read++, _position++)
            {
                buffer[offset + read] = _content[read];
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
