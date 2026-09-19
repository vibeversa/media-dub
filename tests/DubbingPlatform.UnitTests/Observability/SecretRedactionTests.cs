using System.Globalization;
using DubbingPlatform.Infrastructure.Observability;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace DubbingPlatform.UnitTests.Observability;

/// <summary>
/// Verifies secret redaction in logs: sensitive property names and values never
/// appear in rendered output.
/// </summary>
public sealed class SecretRedactionTests
{
    [Fact]
    public void DestructuringPolicy_Redacts_Sensitive_Members()
    {
        var policy = new SecretDestructuringPolicy();
        var factory = new TestValueFactory();

        var result = policy.TryDestructure(
            new SampleOptions { Endpoint = "http://localhost", SecretKey = "hunter2" },
            factory,
            out var value);

        Assert.True(result);
        Assert.NotNull(value);
        var structure = Assert.IsType<StructureValue>(value);
        var secret = structure.Properties.First(p => string.Equals(p.Name, "SecretKey", StringComparison.Ordinal));
        Assert.Equal("[REDACTED]", ((ScalarValue)secret.Value).Value);
        var endpoint = structure.Properties.First(p => string.Equals(p.Name, "Endpoint", StringComparison.Ordinal));
        Assert.Equal("http://localhost", ((ScalarValue)endpoint.Value).Value);
    }

    [Fact]
    public void DestructuringPolicy_Ignores_Objects_Without_Secrets()
    {
        var policy = new SecretDestructuringPolicy();
        var factory = new TestValueFactory();

        var result = policy.TryDestructure(new PlainOptions { Name = "ok" }, factory, out _);

        Assert.False(result);
    }

    [Fact]
    public void Enricher_Redacts_Sensitive_Top_Level_Properties()
    {
        var loggerConfig = new LoggerConfiguration().MinimumLevel.Verbose();
        using var logger = loggerConfig.CreateLogger();
        var enricher = new SecretRedactingEnricher();

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("SecretKey", new ScalarValue("hunter2")), new LogEventProperty("CorrelationId", new ScalarValue("abc"))]);

        enricher.Enrich(logEvent, new TestPropertyFactory());

        var secret = logEvent.Properties["SecretKey"];
        Assert.Equal("[REDACTED]", ((ScalarValue)secret).Value);
        Assert.Equal("abc", ((ScalarValue)logEvent.Properties["CorrelationId"]).Value);
    }

    [Fact]
    public void Json_Formatter_Redacts_Secrets_In_Output()
    {
        var formatter = new RedactingJsonFormatter();
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("SecretKey=hunter2"),
            [new LogEventProperty("CorrelationId", new ScalarValue("corr-1"))]);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, writer);
        var output = writer.ToString();

        Assert.DoesNotContain("hunter2", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
        Assert.Contains("corr-1", output, StringComparison.Ordinal);
    }

    private sealed class SampleOptions
    {
        public string Endpoint { get; set; } = string.Empty;

        public string SecretKey { get; set; } = string.Empty;
    }

    private sealed class PlainOptions
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestValueFactory : Serilog.Core.ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false)
        {
            return new ScalarValue(value);
        }
    }

    private sealed class TestPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
        {
            return new LogEventProperty(name, new ScalarValue(value));
        }

        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false)
        {
            return new ScalarValue(value);
        }
    }
}
