using Serilog.Core;
using Serilog.Events;

namespace Nexus.Web.Infrastructure.Observability;

/// <summary>
/// Enforces proposal §9.1: message bodies, passwords, cookies, and tokens
/// must never appear in structured log output.
/// </summary>
public sealed class SensitiveDataRedactingEnricher : ILogEventEnricher
{
    public const string RedactedMarker = "[REDACTED]";

    private static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "passwd",
        "pwd",
        "token",
        "cookie",
        "authorization",
        "secret",
        "messagebody",
        "body",
        "credential",
        "apikey",
        "connectionstring"
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var keys = logEvent.Properties.Keys.ToList();
        foreach (var key in keys)
        {
            var value = logEvent.Properties[key];
            if (IsSensitiveKey(key))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(key, new ScalarValue(RedactedMarker)));
                continue;
            }

            var sanitized = SanitizeValue(value);
            if (!ReferenceEquals(sanitized, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(key, sanitized));
            }
        }
    }

    public static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
                            .Replace("-", string.Empty, StringComparison.Ordinal);

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static LogEventPropertyValue SanitizeValue(LogEventPropertyValue value)
    {
        return value switch
        {
            StructureValue structure => SanitizeStructure(structure),
            DictionaryValue dict => SanitizeDictionary(dict),
            SequenceValue seq => SanitizeSequence(seq),
            _ => value
        };
    }

    private static StructureValue SanitizeStructure(StructureValue structure)
    {
        var changed = false;
        var props = new List<LogEventProperty>(structure.Properties.Count);
        foreach (var prop in structure.Properties)
        {
            if (IsSensitiveKey(prop.Name))
            {
                changed = true;
                props.Add(new LogEventProperty(prop.Name, new ScalarValue(RedactedMarker)));
            }
            else
            {
                var next = SanitizeValue(prop.Value);
                if (!ReferenceEquals(next, prop.Value))
                {
                    changed = true;
                }
                props.Add(new LogEventProperty(prop.Name, next));
            }
        }

        return changed ? new StructureValue(props, structure.TypeTag) : structure;
    }

    private static DictionaryValue SanitizeDictionary(DictionaryValue dict)
    {
        var changed = false;
        var pairs = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dict.Elements.Count);
        foreach (var kvp in dict.Elements)
        {
            var keyText = kvp.Key.Value?.ToString() ?? string.Empty;
            if (IsSensitiveKey(keyText))
            {
                changed = true;
                pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(kvp.Key, new ScalarValue(RedactedMarker)));
            }
            else
            {
                var next = SanitizeValue(kvp.Value);
                if (!ReferenceEquals(next, kvp.Value))
                {
                    changed = true;
                }
                pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(kvp.Key, next));
            }
        }

        return changed ? new DictionaryValue(pairs) : dict;
    }

    private static SequenceValue SanitizeSequence(SequenceValue seq)
    {
        var changed = false;
        var items = new List<LogEventPropertyValue>(seq.Elements.Count);
        foreach (var elem in seq.Elements)
        {
            var next = SanitizeValue(elem);
            if (!ReferenceEquals(next, elem))
            {
                changed = true;
            }
            items.Add(next);
        }

        return changed ? new SequenceValue(items) : seq;
    }
}
