// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OpenAI.Tests.Telemetry;

internal class TestActivityListener : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> stoppedActivities = new ConcurrentQueue<Activity>();

    public TestActivityListener(string sourceName)
    {
        _listener = new ActivityListener()
        {
            ActivityStopped = stoppedActivities.Enqueue,
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public List<Activity> Activities => stoppedActivities.ToList();

    public void Dispose()
    {
        _listener.Dispose();
    }

    public void ValidateChatActivity(TestResponseInfo response, string requestModel = "gpt-4o-mini", string host = "api.openai.com", int port = 443)
    {
        Assert.AreEqual(1, Activities.Count);
        var activity = Activities.Single();

        Assert.NotNull(activity);
        Assert.AreEqual($"chat {requestModel}", activity.DisplayName);
        Assert.AreEqual("chat", activity.GetTagItem("gen_ai.operation.name"));
        Assert.AreEqual("openai", activity.GetTagItem("gen_ai.system"));
        Assert.AreEqual(requestModel, activity.GetTagItem("gen_ai.request.model"));

        Assert.AreEqual(host, activity.GetTagItem("server.address"));
        Assert.AreEqual(port, activity.GetTagItem("server.port"));

        Assert.AreEqual(response.Model, activity.GetTagItem("gen_ai.response.model"));
        Assert.AreEqual(response.Id, activity.GetTagItem("gen_ai.response.id"));
        Assert.AreEqual(new[] { response.FinishReason }, activity.GetTagItem("gen_ai.response.finish_reasons"));
        Assert.AreEqual(response.PromptTokens, activity.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.AreEqual(response.CompletionTokens, activity.GetTagItem("gen_ai.usage.output_tokens"));

        if (response.FinishReason != null) {
            Assert.AreEqual(ActivityStatusCode.Unset, activity.Status);
            Assert.Null(activity.StatusDescription);
            Assert.Null(activity.GetTagItem("error.type"));
        }

        if (response.ErrorType != null)
        {
            Assert.AreEqual(ActivityStatusCode.Error, activity.Status);
            Assert.AreEqual(response.ErrorType, activity.GetTagItem("error.type"));
        }
    }
}
