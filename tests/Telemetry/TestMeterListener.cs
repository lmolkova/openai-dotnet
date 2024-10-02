using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace OpenAI.Tests.Telemetry;

internal class TestMeterListener : IDisposable
{
    public record TestMeasurement(object value, Dictionary<string, object> tags);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<TestMeasurement>> _measurements = new();
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new();
    private readonly MeterListener _listener;
    public TestMeterListener(string meterName)
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
        _listener.Start();
    }

    public List<TestMeasurement> GetMeasurements(string instrumentName)
    {
        _measurements.TryGetValue(instrumentName, out var queue);
        return queue?.ToList();
    }

    public Instrument GetInstrument(string instrumentName)
    {
        _instruments.TryGetValue(instrumentName, out var instrument);
        return instrument;
    }

    private void OnMeasurementRecorded<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        _instruments.TryAdd(instrument.Name, instrument);

        var testMeasurement = new TestMeasurement(measurement, new Dictionary<string, object>(tags.ToArray()));
        _measurements.AddOrUpdate(instrument.Name, 
            k => new ConcurrentQueue<TestMeasurement>([ testMeasurement ]), 
            (k, l) =>
            {
                l.Enqueue(testMeasurement);
                return l;
            });
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private static void ValidateChatMetricTags(TestMeasurement measurement, TestResponseInfo response, string requestModel = "gpt-4o-mini", string host = "api.openai.com", int port = 443)
    {
        Assert.AreEqual("openai", measurement.tags["gen_ai.system"]);
        Assert.AreEqual("chat", measurement.tags["gen_ai.operation.name"]);
        Assert.AreEqual(host, measurement.tags["server.address"]);
        Assert.AreEqual(requestModel, measurement.tags["gen_ai.request.model"]);
        Assert.AreEqual(port, measurement.tags["server.port"]);

        if (response?.Model != null)
        {
            Assert.AreEqual(response.Model, measurement.tags["gen_ai.response.model"]);
        }
        else
        {
            Assert.False(measurement.tags.ContainsKey("gen_ai.response.model"));
        }

        if (response?.ErrorType != null)
        {
            Assert.AreEqual(response.ErrorType, measurement.tags["error.type"]);
        }
        else
        {
            Assert.False(measurement.tags.ContainsKey("error.type"));
        }
    }

    public TestMeasurement ValidateDuration(TestResponseInfo response, string requestModel, string host, int port)
    {
        var duration = GetInstrument("gen_ai.client.operation.duration");
        Assert.IsNotNull(duration);
        Assert.IsInstanceOf<Histogram<double>>(duration);

        var measurements = GetMeasurements("gen_ai.client.operation.duration");
        Assert.IsNotNull(measurements);
        Assert.AreEqual(1, measurements.Count);

        var measurement = measurements[0];
        Assert.IsInstanceOf<double>(measurement.value);

        ValidateChatMetricTags(measurement, response, requestModel, host, port);
        return measurement;
    }

    public void ValidateUsage(TestResponseInfo response, string requestModel, string host, int port)
    {
        var usage = GetInstrument("gen_ai.client.token.usage");

        if (response.PromptTokens == null)
        {
            Assert.IsNull(usage);
            return;
        }

        Assert.IsNotNull(usage);
        Assert.IsInstanceOf<Histogram<long>>(usage);

        var measurements = GetMeasurements("gen_ai.client.token.usage");
        Assert.IsNotNull(measurements);
        Assert.AreEqual(2, measurements.Count);

        foreach (var measurement in measurements)
        {
            Assert.IsInstanceOf<long>(measurement.value);
            ValidateChatMetricTags(measurement, response, requestModel, host, port);
        }

        Assert.True(measurements[0].tags.TryGetValue("gen_ai.token.type", out var type));
        Assert.IsInstanceOf<string>(type);

        TestMeasurement input = (type is "input") ? measurements[0] : measurements[1];
        TestMeasurement output = (type is "input") ? measurements[1] : measurements[0];

        Assert.AreEqual(response?.PromptTokens, input.value);
        Assert.AreEqual(response?.CompletionTokens, output.value);
    }
}
