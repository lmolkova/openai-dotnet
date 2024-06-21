using OpenAI.Chat;
using OpenAI.Embeddings;
using System;
using System.ClientModel;

namespace OpenAI.Custom.Common.Instrumentation;

internal class InstrumentationFactory
{
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private readonly string _model;

    private readonly bool _recordEvents = AppContextSwitchHelper
        .GetConfigValue("OpenAI.Experimental.RecordEvents", "OPENAI_EXPERIMENTAL_RECORD_EVENTS");

    private readonly bool _recordContent = AppContextSwitchHelper
        .GetConfigValue("OpenAI.Experimental.RecordContent", "OPENAI_EXPERIMENTAL_RECORD_CONTENT");

    private const string ChatOperationName = "chat";
    private const string EmbeddingOperationName = "embedding";

    public InstrumentationFactory(string model, Uri endpoint)
    {
        _serverAddress = endpoint.Host;
        _serverPort = endpoint.Port;
        _model = model;
    }

    // TODO add sampling-relevant attributes
    // TODO optimize

    public InstrumentationScope StartChatCompletionScope(ChatCompletionOptions completionsOptions)
    {
        return new InstrumentationScope(_model, ChatOperationName,
                _serverAddress, _serverPort, _recordEvents, _recordContent)
            .StartChat(completionsOptions);
    }

    public InstrumentationScope StartChatCompletionScope(BinaryContent content)
    {
        return new InstrumentationScope(_model, ChatOperationName,
             _serverAddress, _serverPort, _recordEvents, _recordContent)
            .StartChat(content);
    }

    public StreamingScope StartChatCompletionStreamingScope(ChatCompletionOptions completionsOptions)
    {
        return new InstrumentationScope(_model, ChatOperationName,
                _serverAddress, _serverPort, _recordEvents, _recordContent)
            .StartStreamingChat(completionsOptions);
    }

    public InstrumentationScope StartEmbeddingScope(EmbeddingGenerationOptions embeddingOptions)
    {
        return new InstrumentationScope(_model, EmbeddingOperationName,
                _serverAddress, _serverPort, _recordEvents, _recordContent)
            .StartEmbedding(embeddingOptions);
    }
}

internal static class AppContextSwitchHelper
{
    /// <summary>
    /// Determines if either an AppContext switch or its corresponding Environment Variable is set
    /// </summary>
    /// <param name="appContexSwitchName">Name of the AppContext switch.</param>
    /// <param name="environmentVariableName">Name of the Environment variable.</param>
    /// <returns>If the AppContext switch has been set, returns the value of the switch.
    /// If the AppContext switch has not been set, returns the value of the environment variable.
    /// False if neither is set.
    /// </returns>
    public static bool GetConfigValue(string appContexSwitchName, string environmentVariableName)
    {
        // First check for the AppContext switch, giving it priority over the environment variable.
        if (AppContext.TryGetSwitch(appContexSwitchName, out bool value))
        {
            return value;
        }
        // AppContext switch wasn't used. Check the environment variable.
        string envVar = Environment.GetEnvironmentVariable(environmentVariableName);
        if (envVar != null && (envVar.Equals("true", StringComparison.OrdinalIgnoreCase) || envVar.Equals("1")))
        {
            return true;
        }

        // Default to false.
        return false;
    }
}
