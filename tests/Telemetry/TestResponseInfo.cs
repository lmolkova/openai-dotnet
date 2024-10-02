using OpenAI.Chat;

namespace OpenAI.Tests.Telemetry;

public class TestResponseInfo
{
    public string Id { get; set; }
    public string Model { get; set; }
    public string FinishReason { get; set; }
    public string ErrorType { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    public void WithStreamingUpdate(StreamingChatCompletionUpdate update)
    {
        FinishReason ??= GetFinishReasonString(update.FinishReason);
        Model ??= update.Model;
        Id ??= update.CompletionId;
        PromptTokens ??= update.Usage?.InputTokenCount;
        CompletionTokens ??= update.Usage?.OutputTokenCount;
    }

    public static TestResponseInfo FromChatCompletion(ChatCompletion response)
    {
        return new TestResponseInfo()
        {
            Id = response?.Id,
            Model = response?.Model,
            FinishReason = GetFinishReasonString(response?.FinishReason),
            PromptTokens = response?.Usage.InputTokenCount,
            CompletionTokens = response?.Usage?.OutputTokenCount
        };
    }

    public static string GetFinishReasonString(ChatFinishReason? finishReason) => finishReason switch
    {
        ChatFinishReason.ContentFilter => "content_filter",
        ChatFinishReason.FunctionCall => "function_call",
        ChatFinishReason.Length => "length",
        ChatFinishReason.Stop => "stop",
        ChatFinishReason.ToolCalls => "tool_calls",
        _ => finishReason?.ToString(),
    };
}