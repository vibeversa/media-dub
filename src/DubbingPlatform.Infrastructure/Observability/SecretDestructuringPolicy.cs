using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using DubbingPlatform.Application.Security;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace DubbingPlatform.Infrastructure.Observability;

/// <summary>
/// Serilog destructuring policy that redacts secret-bearing members when complex
/// objects are destructured. Any member whose name contains secret/password/token/
/// key/credential (case-insensitive, consistent with <see cref="SecretRedactor"/>)
/// is rendered as <c>[REDACTED]</c> instead of its value. Scalars and objects
/// without sensitive members fall through to default destructuring.
/// </summary>
public sealed class SecretDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        // Dictionaries: redact sensitive keys.
        if (value is IDictionary dictionary)
        {
            var elements = new List<LogEventProperty>();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                if (SecretRedactor.IsSensitiveKey(key))
                {
                    elements.Add(new LogEventProperty(key, new ScalarValue(SecretRedactor.RedactedValue)));
                }
                else
                {
                    elements.Add(new LogEventProperty(key, propertyValueFactory.CreatePropertyValue(entry.Value, destructureObjects: true)));
                }
            }

            result = new StructureValue(elements);
            return true;
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid)
        {
            result = null;
            return false;
        }

        var properties = type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        var hasSensitive = false;
        foreach (var property in properties)
        {
            if (property.CanRead && SecretRedactor.IsSensitiveKey(property.Name))
            {
                hasSensitive = true;
                break;
            }
        }

        if (!hasSensitive)
        {
            result = null;
            return false;
        }

        var destructured = new List<LogEventProperty>(properties.Length);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? memberValue;
            try
            {
                memberValue = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }

            if (SecretRedactor.IsSensitiveKey(property.Name))
            {
                destructured.Add(new LogEventProperty(property.Name, new ScalarValue(SecretRedactor.RedactedValue)));
            }
            else
            {
                destructured.Add(new LogEventProperty(property.Name, propertyValueFactory.CreatePropertyValue(memberValue, destructureObjects: true)));
            }
        }

        result = new StructureValue(destructured, type.Name);
        return true;
    }
}

/// <summary>
/// Redacts secret-bearing top-level log properties. Any property whose name
/// contains secret/password/token/key/credential is replaced with
/// <c>[REDACTED]</c>. String values containing secret assignments are also
/// redacted via <see cref="SecretRedactor"/>.
/// </summary>
public sealed class SecretRedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var property in logEvent.Properties.ToList())
        {
            if (SecretRedactor.IsSensitiveKey(property.Key))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(SecretRedactor.RedactedValue)));
                continue;
            }

            if (property.Value is ScalarValue scalar && scalar.Value is string text)
            {
                var redacted = SecretRedactor.Redact(text);
                if (!string.Equals(redacted, text, StringComparison.Ordinal))
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(redacted)));
                }
            }
        }
    }
}

/// <summary>
/// JSON console formatter that redacts secret material from the rendered output.
/// Delegates to <see cref="JsonFormatter"/> then applies
/// <see cref="SecretRedactor.Redact(string?)"/> so secret values never reach
/// logs, traces, or console output.
/// </summary>
public sealed class RedactingJsonFormatter : ITextFormatter
{
    private readonly JsonFormatter _inner = new();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        var rendered = buffer.ToString();
        output.Write(SecretRedactor.Redact(rendered));
    }
}
