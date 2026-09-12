using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace JadeAudioControl.Compat;

/// <summary>
/// A small JSON reader/writer over the in-box <see cref="JavaScriptSerializer"/>.
///
/// .NET Framework has no System.Text.Json, and pulling one in would mean
/// shipping DLLs next to the exe. This keeps the app a single file: navigation
/// is by indexer, missing keys give an empty value rather than throwing, so
/// call sites can chain without null checks.
/// </summary>
public sealed class Json
{
    private static readonly JavaScriptSerializer Serializer = new()
    {
        MaxJsonLength = int.MaxValue,
        RecursionLimit = 256,
    };

    /// <summary>An absent value. Every accessor on it yields null or empty.</summary>
    public static readonly Json Empty = new(null);

    private readonly object? _value;

    private Json(object? value) => _value = value;

    public static Json Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;
        try
        {
            return new Json(Serializer.DeserializeObject(text));
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>True when the text parsed as JSON rather than being plain text.</summary>
    public static bool TryParse(string text, out Json json)
    {
        try
        {
            json = new Json(Serializer.DeserializeObject(text));
            return true;
        }
        catch (Exception)
        {
            json = Empty;
            return false;
        }
    }

    public bool Exists => _value is not null;
    public bool IsString => _value is string;
    public bool IsObject => _value is IDictionary<string, object>;
    public bool IsArray => _value is IEnumerable && _value is not string && !IsObject;

    public Json this[string key]
    {
        get
        {
            if (_value is IDictionary<string, object> map && map.TryGetValue(key, out var found))
                return new Json(found);
            return Empty;
        }
    }

    public Json this[int index]
    {
        get
        {
            var items = Items.ToList();
            return index >= 0 && index < items.Count ? items[index] : Empty;
        }
    }

    /// <summary>The elements of an array, or nothing at all.</summary>
    public IEnumerable<Json> Items
    {
        get
        {
            if (_value is object[] array)
            {
                foreach (var item in array)
                    yield return new Json(item);
            }
            else if (_value is not string && _value is IList list)
            {
                foreach (var item in list)
                    yield return new Json(item);
            }
        }
    }

    /// <summary>The key/value pairs of an object, in the order they arrived.</summary>
    public IEnumerable<KeyValuePair<string, Json>> Properties
    {
        get
        {
            if (_value is IDictionary<string, object> map)
                foreach (var pair in map)
                    yield return new KeyValuePair<string, Json>(pair.Key, new Json(pair.Value));
        }
    }

    public string? AsString => _value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => _value.ToString(),
    };

    public int AsInt(int fallback = 0) =>
        TryDouble(out double value) ? (int)Math.Round(value) : fallback;

    public double AsDouble(double fallback = 0) =>
        TryDouble(out double value) ? value : fallback;

    private bool TryDouble(out double result)
    {
        switch (_value)
        {
            case null:
                result = 0;
                return false;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case double d: result = d; return true;
            case decimal m: result = (double)m; return true;
            case float f: result = f; return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// The scalar value, so an accidental ToString() in place of AsString
    /// yields the content rather than the type name.
    /// </summary>
    public override string ToString() => AsString ?? string.Empty;

    // -- writing -------------------------------------------------------------

    /// <summary>Serialise a Dictionary/List/primitive graph, pretty-printed.</summary>
    public static string Write(object graph, bool indented = false)
    {
        string text = Serializer.Serialize(graph);
        return indented ? Indent(text) : text;
    }

    /// <summary>
    /// JavaScriptSerializer only emits compact JSON, so re-flow it for files a
    /// person might open.
    /// </summary>
    private static string Indent(string json)
    {
        var builder = new StringBuilder(json.Length * 2);
        int depth = 0;
        bool inString = false, escaped = false;

        foreach (char c in json)
        {
            if (inString)
            {
                builder.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    builder.Append(c);
                    break;
                case '{':
                case '[':
                    builder.Append(c);
                    builder.Append('\n').Append(new string(' ', ++depth * 2));
                    break;
                case '}':
                case ']':
                    builder.Append('\n').Append(new string(' ', --depth * 2)).Append(c);
                    break;
                case ',':
                    builder.Append(c).Append('\n').Append(new string(' ', depth * 2));
                    break;
                case ':':
                    builder.Append(": ");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }
}
