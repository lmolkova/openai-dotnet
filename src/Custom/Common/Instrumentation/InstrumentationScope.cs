using OpenAI.Chat;
using OpenAI.Embeddings;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAI.Custom.Common.Instrumentation;

internal class InstrumentationScope : IDisposable
{
    private static readonly ActivitySource s_tracerChat = new ActivitySource("OpenAI.ChatClient");
    private static readonly Meter s_meterChat = new Meter("OpenAI.ChatClient");
    private static readonly Histogram<double> s_duration = s_meterChat.CreateHistogram<double>(Constants.GenAiClientOperationDurationMetricName, "s", "Measures GenAI operation duration.");
    private static readonly Histogram<long> s_tokens = s_meterChat.CreateHistogram<long>(Constants.GenAiClientTokenUsageMetricName, "{token}", "Measures the number of input and output token used.");

    // up down counter not available in DS 6
    private static readonly Counter<long> s_streamStart = s_meterChat.CreateCounter<long>(Constants.GenAiClientStreamsStartedMetricName, "{stream}", "Measures the number of started streaming calls.");
    private static readonly Counter<long> s_streamComplete = s_meterChat.CreateCounter<long>(Constants.GenAiClientStreamsCompletedMetricName, "{stream}", "Measures the number of completed streaming calls.");

    private readonly string _operationName;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private readonly bool _recordEvents;
    private readonly bool _recordContent;
    private readonly string _requestModel;
    private static readonly AsyncLocal<bool> IsActive = new AsyncLocal<bool> { Value = false };
    private bool _isEnabled;
    private Stopwatch _duration;
    private Activity _activity;
    private TagList _commonTags;

    public InstrumentationScope(
        string model, string operationName,
        string serverAddress, int serverPort,
        bool recordEvents, bool recordContent)
    {
        _requestModel = model;
        _operationName = operationName;
        _serverAddress = serverAddress;
        _serverPort = serverPort;
        _recordEvents = recordEvents;
        _recordContent = recordContent;
    }

    public InstrumentationScope StartChat(ChatCompletionOptions options)
    {
        _isEnabled = !IsActive.Value && (s_tracerChat.HasListeners() || s_tokens.Enabled || s_duration.Enabled);
        IsActive.Value = true;

        if (_isEnabled)
        {
            _duration = Stopwatch.StartNew();
            _commonTags = new TagList
            {
                { Constants.GenAiSystemKey, Constants.GenAiSystemValue },
                { Constants.GenAiRequestModelKey, _requestModel },
                { Constants.ServerAddressKey, _serverAddress },
                { Constants.ServerPortKey, _serverPort },
                { Constants.GenAiOperationNameKey, _operationName },
            };

            _activity = s_tracerChat.StartActivity(string.Concat(_operationName, " ", _requestModel), ActivityKind.Client);
            if (_activity?.IsAllDataRequested == true)
            {
                RecordChatAttributes(options);

                if (_recordEvents && options?.Messages != null)
                {
                    foreach (var message in options.Messages)
                    {
                        RecordChatMessage(message);
                    }
                }
            }
        }

        return this;
    }

    public InstrumentationScope StartEmbedding(EmbeddingGenerationOptions options)
    {
        _isEnabled = !IsActive.Value && (s_tracerChat.HasListeners() || s_tokens.Enabled || s_duration.Enabled);
        IsActive.Value = true;

        if (_isEnabled)
        {
            _duration = Stopwatch.StartNew();
            _commonTags = new TagList
            {
                { Constants.GenAiSystemKey, Constants.GenAiSystemValue },
                { Constants.GenAiRequestModelKey, _requestModel },
                { Constants.ServerAddressKey, _serverAddress },
                { Constants.ServerPortKey, _serverPort },
                { Constants.GenAiOperationNameKey, _operationName },
            };

            _activity = s_tracerChat.StartActivity(string.Concat(_operationName, " ", _requestModel), ActivityKind.Client);
            if (_activity?.IsAllDataRequested == true)
            {
                RecordCommonAttributes();

                if (_recordEvents && options.Input != null)
                {
                    RecordEmbeddingInput(options.Input);
                }
            }
        }

        return this;
    }

    public InstrumentationScope StartChat(BinaryContent content)
    {
        if (_isEnabled)
        {
            // this is bad!
            using var stream = new MemoryStream();
            content.WriteTo(stream);
            stream.Position = 0;
            StartChat(ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromStream(stream)));
        }
        return this;
    }

    public StreamingScope StartStreamingChat(ChatCompletionOptions options)
    {
        if (_isEnabled)
        {
            StartChat(options);
            s_streamStart.Add(1, _commonTags);
        }
        return new StreamingScope(this, _recordContent);
    }

    public async Task RecordChatCompletionAsync(PipelineResponse rawResponse, CancellationToken cancellationToken)
    {
        if (_isEnabled)
        {
            await rawResponse.BufferContentAsync(cancellationToken);
            RecordChatCompletion(ChatCompletion.FromResponse(rawResponse));
        }
    }

    public void RecordChatCompletion(PipelineResponse rawResponse)
    {
        if (_isEnabled)
        {
            rawResponse.BufferContent();
            RecordChatCompletion(ChatCompletion.FromResponse(rawResponse));
        }
    }

    public void RecordChatCompletion(ChatCompletion completion)
    {
        if (_isEnabled)
        {
            RecordMetrics(completion.Model, null, completion.Usage?.InputTokens, completion.Usage?.OutputTokens);

            if (_activity?.IsAllDataRequested == true)
            {
                RecordResponseAttributes(completion.Id, completion.Model, completion.FinishReason, completion.Usage);
                if (completion.Choices != null && completion.Choices.Count > 0 && _recordEvents)
                {
                    foreach (var choice in completion.Choices)
                    {
                        RecordChoice(choice.Index, choice.FinishReason, choice.Message.Role,
                            choice.Message.Content, choice.Message.ToolCalls);
                    }
                }
            }
        }
    }

    public void RecordEmbeddings(EmbeddingCollection embeddings)
    {
        if (_isEnabled)
        {
            RecordMetrics(embeddings.Model, null, embeddings.Usage?.InputTokens, null);

            if (_activity?.IsAllDataRequested == true)
            {
                RecordUsageAttributes(embeddings.Usage?.InputTokens, null);
            }
        }
    }

    public void RecordStreamingChatCompletion(string responseId, string model, ChatFinishReason? finishReason, ChatMessageRole? role, IEnumerable<ChatMessageContentPart> content,
        IEnumerable<ChatToolCall> toolCalls, ChatTokenUsage usage, Exception ex, bool canceled)
    {
        if (_isEnabled)
        {
            string errorType = GetErrorType(ex, canceled);

            if (_activity?.IsAllDataRequested == true)
            {
                RecordResponseAttributes(responseId, model, finishReason, usage);
                SetActivityError(ex, errorType);
            }

            RecordChoice(0, finishReason, role, content, toolCalls);

            s_streamComplete.Add(1, _commonTags);
            RecordMetrics(model, errorType, usage?.InputTokens, usage?.OutputTokens);
        }
    }

    public void RecordException(Exception ex, bool canceled)
    {
        if (_isEnabled)
        {
            string errorType = GetErrorType(ex, canceled);
            RecordMetrics(null, errorType, null, null);
            SetActivityError(ex, errorType);
        }
    }

    public void Dispose()
    {
        _activity?.Stop();
        IsActive.Value = false;
    }

    private void RecordCommonAttributes()
    {
        _activity.SetTag(Constants.GenAiSystemKey, Constants.GenAiSystemValue);
        _activity.SetTag(Constants.GenAiRequestModelKey, _requestModel);
        _activity.SetTag(Constants.ServerAddressKey, _serverAddress);
        _activity.SetTag(Constants.ServerPortKey, _serverPort);
        _activity.SetTag(Constants.GenAiOperationNameKey, _operationName);
    }

    private void RecordChatAttributes(ChatCompletionOptions options)
    {
        RecordCommonAttributes();

        if (options?.MaxTokens != null)
        {
            _activity.SetTag(Constants.GenAiRequestMaxTokensKey, options.MaxTokens.Value);
        }

        if (options?.Temperature != null)
        {
            _activity.SetTag(Constants.GenAiRequestTemperatureKey, options.Temperature.Value);
        }

        if (options?.TopP != null)
        {
            _activity.SetTag(Constants.GenAiRequestTopPKey, options.TopP.Value);
        }
    }

    private void RecordMetrics(string responseModel, string errorType, int? inputTokensUsage, int? outputTokensUsage)
    {
        TagList tags = ResponseTagsWithError(responseModel, errorType);
        s_duration.Record(_duration.Elapsed.TotalSeconds, tags);

        if (inputTokensUsage != null)
        {
            // tags is a struct, let's copy them
            TagList inputUsageTags = tags;
            inputUsageTags.Add(Constants.GenAiUsageTokenTypeKey, "input");
            s_tokens.Record(inputTokensUsage.Value, inputUsageTags);
        }

        if (outputTokensUsage != null)
        { 
            TagList outputUsageTags = tags;
            outputUsageTags.Add(Constants.GenAiUsageTokenTypeKey, "output");

            s_tokens.Record(outputTokensUsage.Value, outputUsageTags);
        }
    }

    private TagList ResponseTagsWithError(string responseModel, string errorType)
    {
        // tags is a struct, let's copy them
        var tags = _commonTags;

        if (responseModel != null)
        {
            tags.Add(Constants.GenAiResponseModelKey, responseModel);
        }

        if (errorType != null)
        {
            tags.Add(Constants.ErrorTypeKey, errorType);
        }

        return tags;
    }

    private void RecordUsageAttributes(int? inputTokensUsage, int? outputTokensUsage)
    {
        if (inputTokensUsage != null)
        {
            _activity.SetTag(Constants.GenAiUsageInputTokensKey, inputTokensUsage.Value);
        }

        if (outputTokensUsage != null)
        {
            _activity.SetTag(Constants.GenAiUsageOutputTokensKey, outputTokensUsage.Value);
        }
    }

    private void RecordResponseAttributes(string responseId, string model, ChatFinishReason? finishReason, ChatTokenUsage usage)
    {
        if (responseId != null)
        {
            _activity.SetTag(Constants.GenAiResponseIdKey, responseId);
        }

        if (model != null)
        {
            _activity.SetTag(Constants.GenAiResponseModelKey, model);
        }

        if (finishReason != null)
        {
            _activity.SetTag(Constants.GenAiUsageFinishReasonKey, GetFinishReason(finishReason.Value));
        }

        RecordUsageAttributes(usage.InputTokens, usage.OutputTokens);
    }

    private string GetFinishReason(ChatFinishReason? reason) =>
        reason switch
        {
            ChatFinishReason.ContentFilter => "content_filter",
            ChatFinishReason.FunctionCall => "function_call",
            ChatFinishReason.Length => "length",
            ChatFinishReason.Stop => "stop",
            ChatFinishReason.ToolCalls => "tool_calls",
            _ => reason?.ToString(),
        };

    private string GetChatMessageRole(ChatMessageRole? role) =>
        role switch
        {
            ChatMessageRole.Assistant => "assistant",
            ChatMessageRole.Function => "function",
            ChatMessageRole.System => "system",
            ChatMessageRole.Tool => "tool",
            ChatMessageRole.User => "user",
            _ => role?.ToString(),
        };

    private string GetErrorType(Exception exception, bool canceled)
    {
        if (canceled)
        {
            return typeof(TaskCanceledException).FullName;
        }

        if (exception is ClientResultException requestFailedException)
        {
            // TODO (limolkova) when we start targeting .NET 8 we should put
            // requestFailedException.InnerException.HttpRequestError into error.type
            return requestFailedException.Status.ToString();
        }

        return exception?.GetType()?.FullName;
    }

    private void SetActivityError(Exception exception, string errorType)
    {
        if (exception != null || errorType != null)
        {
            _activity?.SetTag(Constants.ErrorTypeKey, errorType);
            _activity?.SetStatus(ActivityStatusCode.Error, exception?.ToString() ?? errorType);
        }
    }

    private void RecordChoice(int? index, ChatFinishReason? finishReason, ChatMessageRole? role,
        IEnumerable<ChatMessageContentPart> content, IEnumerable<ChatToolCall> toolCalls)
    {
        if (_recordEvents)
        {
            object payload = new
            {
                index,
                finish_reason = GetFinishReason(finishReason),
                message = new
                {
                    role = GetChatMessageRole(role),
                    content = EventUtils.Sanitize(content, _recordContent),
                    tool_calls = EventUtils.Sanitize(toolCalls, _recordContent)
                }
            };

            _activity.WriteEvent(Constants.GenAiChoiceEventName, payload);
        }
    }

    private void RecordEmbeddingInput(BinaryData input)
    {
        // TODO:semconv should decide
        // - if to record content in the events - probably not
        // - if opt-in should be different for chat and embedding
    }

    private void RecordChatMessage(ChatMessage message)
    {
        if (message is SystemChatMessage systemMessage)
        {
            object payload = new { content = EventUtils.Sanitize(systemMessage.Content, _recordContent) };
            _activity.WriteEvent(Constants.GenAiSystemMessageEventName, payload);
        }
        else if (message is UserChatMessage userMessage)
        {
            object payload = new { content = EventUtils.Sanitize(userMessage, _recordContent) };
            _activity.WriteEvent(Constants.GenAiUserMessageEventName, payload);
        }
        else if (message is AssistantChatMessage assistantMessage)
        {
            object payload = new
            {
                content = EventUtils.Sanitize(assistantMessage.Content, _recordContent),
                tool_calls = EventUtils.Sanitize(assistantMessage.ToolCalls, _recordContent)
            };
            _activity.WriteEvent(Constants.GenAiAssistantMessageEventName, payload);
        }
        else if (message is ToolChatMessage toolMessage)
        {
            object payload = new
            {
                content = EventUtils.Sanitize(toolMessage.Content, _recordContent),
                tool_call_id = toolMessage.ToolCallId
            };
            _activity.WriteEvent(Constants.GenAiToolMessageEventName, payload);
        }
        else if (message is FunctionChatMessage functionMessage)
        {
            object payload = new
            {
                content = EventUtils.Sanitize(functionMessage.Content, _recordContent),
            };
            _activity.WriteEvent(Constants.GenAiFunctionMessageEventName, payload);
        }
    }
}
