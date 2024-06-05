// <auto-generated/>

#nullable disable

using System;
using System.Collections.Generic;

namespace OpenAI.Assistants
{
    internal partial class InternalRunStepDeltaStepDetailsToolCallsObject : InternalRunStepDeltaStepDetails
    {
        internal InternalRunStepDeltaStepDetailsToolCallsObject()
        {
            Type = "tool_calls";
            ToolCalls = new ChangeTrackingList<InternalRunStepDeltaStepDetailsToolCallsObjectToolCallsObject>();
        }

        internal InternalRunStepDeltaStepDetailsToolCallsObject(string type, IDictionary<string, BinaryData> serializedAdditionalRawData, IReadOnlyList<InternalRunStepDeltaStepDetailsToolCallsObjectToolCallsObject> toolCalls) : base(type, serializedAdditionalRawData)
        {
            ToolCalls = toolCalls;
        }

        public IReadOnlyList<InternalRunStepDeltaStepDetailsToolCallsObjectToolCallsObject> ToolCalls { get; }
    }
}