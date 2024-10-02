using NUnit.Framework;
using OpenAI.Chat;
using OpenAI.Telemetry;
using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAI.Tests.Telemetry;

[TestFixture]
[NonParallelizable]
[Category("Telemetry")]
[Category("Smoke")]
public class ChatTelemetryTests
{
    private const string RequestModel = "gpt-4o-mini";
    private const string Host = "api.openai.com";
    private const int Port = 443;
    private static readonly string Endpoint = $"https://{Host}:{Port}/path";
    private const string CompletionId = "chatcmpl-9fG9OILMJnKZARXDwxoCnLcvDsDDX";
    private const string CompletionContent = "hello world";
    private const string ResponseModel = "responseModel";
    private const string FinishReason = "stop";
    private const int PromptTokens = 2;
    private const int CompletionTokens = 42;

    [Test]
    public void AllTelemetryOff()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        Assert.IsNull(telemetry.StartChatScope(new ChatCompletionOptions()));
        Assert.IsNull(Activity.Current);
    }

    [Test]
    public void MetricsOnTracingOff()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));

        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        var elapsedMax = Stopwatch.StartNew();
        using var scope = telemetry.StartChatScope(new ChatCompletionOptions());
        var elapsedMin = Stopwatch.StartNew();

        Assert.Null(Activity.Current);
        Assert.NotNull(scope);

        // so we have some duration to measure
        Thread.Sleep(20);

        elapsedMin.Stop();

        var response = CreateChatCompletion();
        scope.RecordChatCompletion(response);
        scope.Dispose();

        var measurement = meterListener.ValidateDuration(TestResponseInfo.FromChatCompletion(response), RequestModel, Host, Port);
        Assert.GreaterOrEqual((double)measurement.value, elapsedMin.Elapsed.TotalSeconds);
        Assert.LessOrEqual((double)measurement.value, elapsedMax.Elapsed.TotalSeconds);

        meterListener.ValidateUsage(TestResponseInfo.FromChatCompletion(response), RequestModel, Host, Port);
    }

    [Test]
    public void MetricsOnTracingOffException()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordException(new Exception());
        }

        meterListener.ValidateDuration(new TestResponseInfo() { ErrorType = typeof(Exception).FullName }, RequestModel, Host, Port);
        Assert.IsNull(meterListener.GetMeasurements("gen_ai.client.token.usage"));
    }

    [Test]
    public void TracingOnMetricsOff()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var listener = new TestActivityListener("Experimental.OpenAI.ChatClient");

        var chatCompletion = CreateChatCompletion();

        Activity activity = null;
        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            activity = Activity.Current;
            Assert.IsNull(activity.GetTagItem("gen_ai.request.temperature"));
            Assert.IsNull(activity.GetTagItem("gen_ai.request.top_p"));
            Assert.IsNull(activity.GetTagItem("gen_ai.request.max_tokens"));

            Assert.NotNull(scope);

            scope.RecordChatCompletion(chatCompletion);
        }

        Assert.Null(Activity.Current);
        Assert.AreEqual(1, listener.Activities.Count);

        listener.ValidateChatActivity(TestResponseInfo.FromChatCompletion(chatCompletion), RequestModel, Host, Port);
    }

    [Test]
    public void ChatTracingAllAttributes()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var listener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        var options = new ChatCompletionOptions()
        {
            Temperature = 0.42f,
            MaxOutputTokenCount = 200,
            TopP = 0.9f
        };
        SetMessages(options, new UserChatMessage("hello"));

        var chatCompletion = CreateChatCompletion();

        using (var scope = telemetry.StartChatScope(options))
        {
            Assert.AreEqual(options.Temperature.Value, (float)Activity.Current.GetTagItem("gen_ai.request.temperature"), 0.01);
            Assert.AreEqual(options.TopP.Value, (float)Activity.Current.GetTagItem("gen_ai.request.top_p"), 0.01);
            Assert.AreEqual(options.MaxOutputTokenCount.Value, Activity.Current.GetTagItem("gen_ai.request.max_tokens"));
            scope.RecordChatCompletion(chatCompletion);
        }
        Assert.Null(Activity.Current);

        listener.ValidateChatActivity(TestResponseInfo.FromChatCompletion(chatCompletion), RequestModel, Host, Port);
    }

    [Test]
    public void ChatTracingException()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        var error = new SocketException(42, "test error");
        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordException(error);
        }

        Assert.Null(Activity.Current);

        activityListener.ValidateChatActivity(new TestResponseInfo() { ErrorType = error.GetType().FullName }, RequestModel, Host, Port);
    }

    [Test]
    public void ChatStreaming()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordStreamingUpdate(CreateStreamingUpdate(model: ResponseModel));
            scope.RecordStreamingUpdate(CreateStreamingUpdate(finishReason: FinishReason));
        }

        TestResponseInfo response = new ()
        {
            Model = ResponseModel,
            Id = CompletionId,
            FinishReason = FinishReason
        };

        activityListener.ValidateChatActivity(response, RequestModel, Host, Port);
        meterListener.ValidateDuration(response, RequestModel, Host, Port);
        Assert.Null(meterListener.GetInstrument("gen_ai.client.token.usage"));
    }

    [Test]
    public void ChatStreamingWithUsage()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordStreamingUpdate(CreateStreamingUpdate(model: ResponseModel));
            scope.RecordStreamingUpdate(CreateStreamingUpdate(finishReason: FinishReason));
            scope.RecordStreamingUpdate(CreateStreamingUpdate(promptTokens: PromptTokens, completionTokens: CompletionTokens));
        }

        Assert.Null(Activity.Current);

        TestResponseInfo response = new()
        {
            Model = ResponseModel,
            Id = CompletionId,
            FinishReason = FinishReason,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens
        };

        activityListener.ValidateChatActivity(response, RequestModel, Host, Port);
        meterListener.ValidateDuration(response, RequestModel, Host, Port);
        meterListener.ValidateUsage(response, RequestModel, Host, Port);
    }

    [Test]
    public void ChatStreamingWithError()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordStreamingUpdate(CreateStreamingUpdate(model: ResponseModel));
            scope.RecordException(new Exception("boom"));
        }

        TestResponseInfo response = new()
        {
            Model = ResponseModel,
            Id = CompletionId,
            ErrorType = typeof(Exception).FullName
        };

        activityListener.ValidateChatActivity(response, RequestModel, Host, Port);
        meterListener.ValidateDuration(response, RequestModel, Host, Port);
        Assert.Null(meterListener.GetInstrument("gen_ai.client.token.usage"));
    }

    [Test]
    public void ChatStreamingWithCancellation()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
            scope.RecordCancellation();
        }

        TestResponseInfo response = new() { ErrorType = typeof(OperationCanceledException).FullName };

        activityListener.ValidateChatActivity(response, RequestModel, Host, Port);
        meterListener.ValidateDuration(response, RequestModel, Host, Port);
        Assert.Null(meterListener.GetInstrument("gen_ai.client.token.usage"));
    }

    [Test]
    public void ChatStreamingEmpty()
    {
        var telemetry = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));
        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        using (var scope = telemetry.StartChatScope(new ChatCompletionOptions()))
        {
        }

        TestResponseInfo response = new() { ErrorType = "error" };

        activityListener.ValidateChatActivity(response, RequestModel, Host, Port);
        meterListener.ValidateDuration(response, RequestModel, Host, Port);
        Assert.Null(meterListener.GetInstrument("gen_ai.client.token.usage"));
    }

    [Test]
    public async Task ChatTracingAndMetricsMultiple()
    {
        var source = new OpenTelemetrySource(RequestModel, new Uri(Endpoint));

        using var activityListener = new TestActivityListener("Experimental.OpenAI.ChatClient");
        using var meterListener = new TestMeterListener("Experimental.OpenAI.ChatClient");

        var options = new ChatCompletionOptions();

        var tasks = new Task[5];
        int numberOfSuccessfulResponses = 3;
        int totalPromptTokens = 0, totalCompletionTokens = 0;
        for (int i = 0; i < tasks.Length; i++)
        {
            int t = i;
            // don't let Activity.Current escape the scope
            tasks[i] = Task.Run(async () =>
            {
                using var scope = source.StartChatScope(options);
                await Task.Delay(10);
                if (t < numberOfSuccessfulResponses)
                {
                    var promptTokens = Random.Shared.Next(100);
                    var completionTokens = Random.Shared.Next(100);

                    var completion = CreateChatCompletion(promptTokens, completionTokens);
                    Interlocked.Add(ref totalCompletionTokens, completionTokens);
                    Interlocked.Add(ref totalPromptTokens, promptTokens);
                    scope.RecordChatCompletion(completion);
                }
                else
                {
                    scope.RecordException(new TaskCanceledException());
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.AreEqual(tasks.Length, activityListener.Activities.Count);

        var durations = meterListener.GetMeasurements("gen_ai.client.operation.duration");
        Assert.AreEqual(tasks.Length, durations.Count);
        Assert.AreEqual(numberOfSuccessfulResponses, durations.Count(d => !d.tags.ContainsKey("error.type")));

        var usages = meterListener.GetMeasurements("gen_ai.client.token.usage");
        // we don't report usage if there was no response
        Assert.AreEqual(numberOfSuccessfulResponses * 2, usages.Count);
        Assert.IsEmpty(usages.Where(u => u.tags.ContainsKey("error.type")));

        Assert.AreEqual(totalPromptTokens, usages
            .Where(u => u.tags.Contains(new KeyValuePair<string, object>("gen_ai.token.type", "input")))
            .Sum(u => (long)u.value));
        Assert.AreEqual(totalCompletionTokens, usages
            .Where(u => u.tags.Contains(new KeyValuePair<string, object>("gen_ai.token.type", "output")))
            .Sum(u => (long)u.value));
    }

    private void SetMessages(ChatCompletionOptions options, params ChatMessage[] messages)
    {
        var messagesProperty = typeof(ChatCompletionOptions).GetProperty("Messages", BindingFlags.Instance | BindingFlags.NonPublic);
        messagesProperty.SetValue(options, messages.ToList());
    }

    private static ChatCompletion CreateChatCompletion(int promptTokens = PromptTokens, int completionTokens = CompletionTokens)
    {
        var completion = BinaryData.FromString(
            $$"""
            {
              "id": "{{CompletionId}}",
              "created": 1719621282,
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "{{CompletionContent}}"
                  },
                  "logprobs": null,
                  "index": 0,
                  "finish_reason": "{{FinishReason}}"
                }
              ],
              "model": "{{ResponseModel}}",
              "system_fingerprint": "fp_7ec89fabc6",
              "usage": {
                "completion_tokens": {{completionTokens}},
                "prompt_tokens": {{promptTokens}},
                "total_tokens": 42
              }
            }
            """);

        return ModelReaderWriter.Read<ChatCompletion>(completion);
    }

    private static StreamingChatCompletionUpdate CreateStreamingUpdate(string finishReason = null,
        string model = null,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        var usage = promptTokens == null ? "null" : $$"""
            {
                "completion_tokens": {{completionTokens}},
                "prompt_tokens": {{promptTokens}},
                "total_tokens": 42
            }
            """;

        finishReason = finishReason == null ? "null" : $"\"{finishReason}\"";
        model = model == null ? "null" : $"\"{model}\"";

        var completion = BinaryData.FromString(
            $$"""
            {
              "id": "{{CompletionId}}",
              "created": 1719621282,
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": null
                  },
                  "logprobs": null,
                  "index": 0,
                  "finish_reason": {{finishReason}}
                }
              ],
              "model": {{model}},
              "system_fingerprint": "fp_7ec89fabc6",
              "usage": {{usage}}
            }
            """);

        return ModelReaderWriter.Read<StreamingChatCompletionUpdate>(completion);
    }
}