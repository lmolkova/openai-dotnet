using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using static OpenAI.Telemetry.OpenTelemetryConstants;

namespace OpenAI.Telemetry;

internal class OpenTelemetryScope : IDisposable
{
    private static readonly ActivitySource s_chatSource = new ActivitySource("Experimental.OpenAI.ChatClient");
    private static readonly Meter s_chatMeter = new Meter("Experimental.OpenAI.ChatClient");

    // TODO: add explicit histogram buckets once System.Diagnostics.DiagnosticSource 9.0 is used
    private static readonly Histogram<double> s_duration = s_chatMeter.CreateHistogram<double>(GenAiClientOperationDurationMetricName, "s", "Measures GenAI operation duration.");
    private static readonly Histogram<long> s_tokens = s_chatMeter.CreateHistogram<long>(GenAiClientTokenUsageMetricName, "{token}", "Measures the number of input and output token used.");

    private readonly string _operationName;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private readonly string _requestModel;

    private Stopwatch _duration;
    private Activity _activity;
    private TagList _commonTags;

    private string _responseModel;
    private string _responseId;
    private string _finishReason;
    private string _errorType;
    private Exception _exception;
    private ChatTokenUsage _tokenUsage;

    private int _closed = 0;

    private OpenTelemetryScope(
        string model, string operationName,
        string serverAddress, int serverPort)
    {
        _requestModel = model;
        _operationName = operationName;
        _serverAddress = serverAddress;
        _serverPort = serverPort;
    }

    private static bool IsChatEnabled => s_chatSource.HasListeners() || s_tokens.Enabled || s_duration.Enabled;

    public static OpenTelemetryScope StartChat(string model, string operationName,
        string serverAddress, int serverPort, ChatCompletionOptions options)
    {
        if (IsChatEnabled)
        {
            var scope = new OpenTelemetryScope(model, operationName, serverAddress, serverPort);
            scope.StartChat(options);
            return scope;
        }

        return null;
    }

    private void StartChat(ChatCompletionOptions options)
    {
        _duration = Stopwatch.StartNew();
        _commonTags = new TagList
        {
            { GenAiSystemKey, GenAiSystemValue },
            { GenAiRequestModelKey, _requestModel },
            { ServerAddressKey, _serverAddress },
            { ServerPortKey, _serverPort },
            { GenAiOperationNameKey, _operationName },
        };

        _activity = s_chatSource.StartActivity(string.Concat(_operationName, " ", _requestModel), ActivityKind.Client);
        if (_activity?.IsAllDataRequested == true)
        {
            RecordCommonAttributes();
            SetActivityTagIfNotNull(GenAiRequestMaxTokensKey, options?.MaxOutputTokenCount);
            SetActivityTagIfNotNull(GenAiRequestTemperatureKey, options?.Temperature);
            SetActivityTagIfNotNull(GenAiRequestTopPKey, options?.TopP);
        }

        return;
    }

    public void RecordChatCompletion(ChatCompletion completion)
    {
        _responseModel = completion.Model;
        _responseId = completion.Id;
        _finishReason = GetFinishReason(completion.FinishReason);
        _tokenUsage = completion.Usage;
    }

    public void RecordStreamingUpdate(StreamingChatCompletionUpdate update)
    {
        // TODO - we'll add content events later, for now let's just report spans
        if (update.FinishReason != null)
        {
            _finishReason = GetFinishReason(update.FinishReason);
        }

        if (update.CompletionId != null)
        {
            _responseId = update.CompletionId;
        }

        if (update.Model != null)
        {
            _responseModel = update.Model;
        }

        if (update.Usage != null)
        {
            _tokenUsage = update.Usage;
        }
    }

    public void RecordException(Exception ex)
    {
        _errorType = GetErrorType(ex);
        _exception = ex;
    }

    public void RecordCancellation()
    {
        _errorType = typeof(TaskCanceledException).FullName;
    }

    public void Dispose()
    {
        // idempotent closing
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            End();
        }
    }

    private void RecordCommonAttributes()
    {
        _activity.SetTag(GenAiSystemKey, GenAiSystemValue);
        _activity.SetTag(GenAiRequestModelKey, _requestModel);
        _activity.SetTag(ServerAddressKey, _serverAddress);
        _activity.SetTag(ServerPortKey, _serverPort);
        _activity.SetTag(GenAiOperationNameKey, _operationName);
    }

    private void End()
    {
        if (_finishReason == null && _errorType == null)
        {
            // if there was no finish reason and no error, the response was not received fully
            // which means there was some unexpected unknown error, let's report it.
            _errorType = "error";
        }

        RecordResponseOnActivity();
        
        // tags is a struct, let's copy and modify them
        var metricTags = _commonTags;

        if (_responseModel != null)
        {
            metricTags.Add(GenAiResponseModelKey, _responseModel);
        }

        if (_errorType != null)
        {
            metricTags.Add(ErrorTypeKey, _errorType);
        }

        if (_tokenUsage != null)
        {
            var inputUsageTags = metricTags;
            inputUsageTags.Add(GenAiTokenTypeKey, "input");
            s_tokens.Record(_tokenUsage.InputTokenCount, inputUsageTags);

            var outputUsageTags = metricTags;
            outputUsageTags.Add(GenAiTokenTypeKey, "output");
            s_tokens.Record(_tokenUsage.OutputTokenCount, outputUsageTags);
        }

        s_duration.Record(_duration.Elapsed.TotalSeconds, metricTags);
        _activity?.Stop();
    }

    private void RecordResponseOnActivity()
    {
        if (_activity?.IsAllDataRequested != true)
        {
            return;
        }
        SetActivityTagIfNotNull(GenAiResponseIdKey, _responseId);
        SetActivityTagIfNotNull(GenAiResponseModelKey, _responseModel);
        SetActivityTagIfNotNull(GenAiUsageInputTokensKey, _tokenUsage?.InputTokenCount);
        SetActivityTagIfNotNull(GenAiUsageOutputTokensKey, _tokenUsage?.OutputTokenCount);
        _activity.SetTag(GenAiResponseFinishReasonKey, new[] { _finishReason });
        if (_errorType != null)
        {
            _activity.SetTag(ErrorTypeKey, _errorType);
            _activity.SetStatus(ActivityStatusCode.Error, _exception?.Message ?? _errorType);
        }
    }

    private string GetFinishReason(ChatFinishReason? finishReason) => finishReason switch
        {
            ChatFinishReason.ContentFilter => "content_filter",
            ChatFinishReason.FunctionCall => "function_call",
            ChatFinishReason.Length => "length",
            ChatFinishReason.Stop => "stop",
            ChatFinishReason.ToolCalls => "tool_calls",
            _ => finishReason?.ToString(),
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

    private string GetErrorType(Exception exception)
    {
        if (exception is ClientResultException requestFailedException)
        {
            // TODO (lmolkova) when we start targeting .NET 8 we should put
            // requestFailedException.InnerException.HttpRequestError into error.type
            return requestFailedException.Status.ToString();
        }

        return exception?.GetType()?.FullName;
    }

    private void SetActivityTagIfNotNull(string name, object value)
    {
        if (value != null)
        {
            _activity.SetTag(name, value);
        }
    }

    private void SetActivityTagIfNotNull(string name, int? value)
    {
        if (value.HasValue)
        {
            _activity.SetTag(name, value.Value);
        }
    }

    private void SetActivityTagIfNotNull(string name, float? value)
    {
        if (value.HasValue)
        {
            _activity.SetTag(name, value.Value);
        }
    }
}
