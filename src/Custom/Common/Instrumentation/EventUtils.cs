using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using OpenAI.Chat;

namespace OpenAI.Custom.Common.Instrumentation;

internal static class EventUtils
{
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new JsonSerializerOptions()
    {
        //IgnoreNullValues = true,
    };

    public static void WriteEvent(this Activity activity, string eventName, object payload)
    {
        activity?.AddEvent(new ActivityEvent(eventName, default, new ActivityTagsCollection
            {
                { Constants.GenAiEventPayloadKey, JsonSerializer.Serialize(payload, s_jsonSerializerOptions) },
                { Constants.GenAiSystemKey, Constants.GenAiSystemValue}
            }));
    }

    public static object Sanitize(UserChatMessage userMessage, bool recordContent)
    {
        if (userMessage.Content != null && userMessage.Content.Any())
        {
            return userMessage.Content.Select(m =>
            {
                if (m.Kind == ChatMessageContentPartKind.Text)
                {
                    return new
                    {
                        type = "text",
                        content = Sanitize(m.Text, recordContent)
                    };
                }
                else if (m.Kind == ChatMessageContentPartKind.Image)
                {
                    return (object)new
                    {
                        type = "image",
                        detail_level = m.ImageDetail?.ToString(), // TODO
                        content = Sanitize(m.ImageUri.OriginalString, recordContent)
                    };
                }
                return null;
            });
        }

        return null;
    }

    public static List<object> Sanitize(IEnumerable<ChatToolCall> calls, bool recordContent)
    {
        if (calls == null || !calls.Any())
        {
            return null;
        }
        List<object> toolCalls = new List<object>();
        foreach (ChatToolCall call in calls)
        {
            toolCalls.Add(new
            {
                id = call.Id,
                type = call.Kind,
                function = call is ChatToolCall funcCall ? new
                {
                    name = funcCall.FunctionName,
                    arguments = Sanitize(funcCall.FunctionArguments, recordContent)
                } : null
            });
        }
        return toolCalls;
    }

    public static IEnumerable<object> Sanitize(IEnumerable<ChatMessageContentPart> contentParts, bool recordContent)
    {
        return contentParts?.Select(m =>
        {
            if (m.Kind == ChatMessageContentPartKind.Text)
            {
                return new
                {
                    type = "text",
                    content = Sanitize(m.Text, recordContent)
                };
            }
            else if (m.Kind == ChatMessageContentPartKind.Image)
            {
                return (object)new
                {
                    type = "image",
                    detail_level = m.ImageDetail?.ToString(),
                    content = Sanitize(m.ImageUri.OriginalString, recordContent)
                };
            }
            return null;
        });
    }

    private static string Sanitize(string content, bool recordContent)
    {
        if (content == null)
        {
            return null;
        }

        return recordContent ? content : "REDACTED";
    }
}
