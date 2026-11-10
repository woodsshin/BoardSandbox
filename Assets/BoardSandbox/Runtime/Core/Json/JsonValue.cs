using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BoardSandbox.Core.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    public sealed class JsonParseException : Exception
    {
        public int Offset { get; }

        public JsonParseException(string message, int offset)
            : base(message + " at offset " + offset.ToString(CultureInfo.InvariantCulture))
        {
            Offset = offset;
        }
    }

    public sealed class JsonValue
    {
        public const int MaxDepth = 128;

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null, 0, null, null, null, true);
        public static readonly JsonValue True = new JsonValue(JsonKind.Bool, 1, null, null, null, true);
        public static readonly JsonValue False = new JsonValue(JsonKind.Bool, 0, null, null, null, true);

        private static readonly JsonValue[] NoItems = new JsonValue[0];
        private static readonly KeyValuePair<string, JsonValue>[] NoMembers = new KeyValuePair<string, JsonValue>[0];

        private readonly double _number;
        private readonly string _string;
        private readonly List<JsonValue> _items;
        private readonly SortedDictionary<string, JsonValue> _members;
        private readonly bool _frozen;

        private JsonValue(JsonKind kind, double number, string text, List<JsonValue> items, SortedDictionary<string, JsonValue> members, bool frozen)
        {
            Kind = kind;
            _number = number;
            _string = text;
            _items = items;
            _members = members;
            _frozen = frozen;
        }

        public JsonKind Kind { get; }

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsBool => Kind == JsonKind.Bool;
        public bool IsNumber => Kind == JsonKind.Number;
        public bool IsString => Kind == JsonKind.String;
        public bool IsArray => Kind == JsonKind.Array;
        public bool IsObject => Kind == JsonKind.Object;

        // The VM encodes an empty table as [], so an empty array is accepted wherever an object is expected.
        public bool IsObjectLike => Kind == JsonKind.Object || (Kind == JsonKind.Array && _items.Count == 0);

        public int Count => Kind == JsonKind.Array ? _items.Count : Kind == JsonKind.Object ? _members.Count : 0;

        public JsonValue this[string key]
        {
            get
            {
                if (Kind == JsonKind.Object && key != null && _members.TryGetValue(key, out JsonValue value))
                    return value;
                return Null;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind == JsonKind.Array && index >= 0 && index < _items.Count)
                    return _items[index];
                return Null;
            }
        }

        public bool Has(string key)
        {
            return Kind == JsonKind.Object && key != null && _members.ContainsKey(key);
        }

        public IReadOnlyList<JsonValue> Items => Kind == JsonKind.Array ? (IReadOnlyList<JsonValue>)_items : NoItems;

        public IEnumerable<KeyValuePair<string, JsonValue>> Members =>
            Kind == JsonKind.Object ? (IEnumerable<KeyValuePair<string, JsonValue>>)_members : NoMembers;

        public string AsString(string fallback = "")
        {
            return Kind == JsonKind.String ? _string : fallback;
        }

        public double AsDouble(double fallback = 0)
        {
            return Kind == JsonKind.Number ? _number : fallback;
        }

        public int AsInt(int fallback = 0)
        {
            if (Kind != JsonKind.Number || double.IsNaN(_number) || _number > int.MaxValue || _number < int.MinValue)
                return fallback;
            return (int)_number;
        }

        public long AsLong(long fallback = 0)
        {
            if (Kind != JsonKind.Number || double.IsNaN(_number) || _number > 9.007199254740992e15 || _number < -9.007199254740992e15)
                return fallback;
            return (long)_number;
        }

        public bool AsBool(bool fallback = false)
        {
            return Kind == JsonKind.Bool ? _number != 0 : fallback;
        }

        public static JsonValue Of(bool value)
        {
            return value ? True : False;
        }

        public static JsonValue Of(double value)
        {
            return new JsonValue(JsonKind.Number, value, null, null, null, false);
        }

        public static JsonValue Of(int value)
        {
            return new JsonValue(JsonKind.Number, value, null, null, null, false);
        }

        public static JsonValue Of(long value)
        {
            return new JsonValue(JsonKind.Number, value, null, null, null, false);
        }

        public static JsonValue Of(string value)
        {
            return value == null ? Null : new JsonValue(JsonKind.String, 0, value, null, null, false);
        }

        public static JsonValue NewArray()
        {
            return new JsonValue(JsonKind.Array, 0, null, new List<JsonValue>(), null, false);
        }

        public static JsonValue NewObject()
        {
            return new JsonValue(JsonKind.Object, 0, null, null, new SortedDictionary<string, JsonValue>(StringComparer.Ordinal), false);
        }

        public JsonValue Set(string key, JsonValue value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            RequireMutable(JsonKind.Object);
            _members[key] = value ?? Null;
            return this;
        }

        public JsonValue Set(string key, string value)
        {
            return Set(key, Of(value));
        }

        public JsonValue Set(string key, double value)
        {
            return Set(key, Of(value));
        }

        public JsonValue Set(string key, int value)
        {
            return Set(key, Of(value));
        }

        public JsonValue Set(string key, bool value)
        {
            return Set(key, Of(value));
        }

        public bool Remove(string key)
        {
            RequireMutable(JsonKind.Object);
            return key != null && _members.Remove(key);
        }

        public JsonValue Add(JsonValue value)
        {
            RequireMutable(JsonKind.Array);
            _items.Add(value ?? Null);
            return this;
        }

        public JsonValue Add(string value)
        {
            return Add(Of(value));
        }

        public JsonValue Add(double value)
        {
            return Add(Of(value));
        }

        public JsonValue Add(int value)
        {
            return Add(Of(value));
        }

        public JsonValue Add(bool value)
        {
            return Add(Of(value));
        }

        private void RequireMutable(JsonKind expected)
        {
            if (_frozen || Kind != expected)
                throw new InvalidOperationException("The JSON value is not a mutable " + expected.ToString().ToLowerInvariant() + ".");
        }

        public JsonValue DeepClone()
        {
            switch (Kind)
            {
                case JsonKind.Array:
                {
                    JsonValue copy = NewArray();
                    foreach (JsonValue item in _items)
                        copy._items.Add(item.DeepClone());
                    return copy;
                }
                case JsonKind.Object:
                {
                    JsonValue copy = NewObject();
                    foreach (KeyValuePair<string, JsonValue> pair in _members)
                        copy._members[pair.Key] = pair.Value.DeepClone();
                    return copy;
                }
                case JsonKind.Number:
                    return Of(_number);
                case JsonKind.String:
                    return Of(_string);
                default:
                    return this;
            }
        }

        public bool StructurallyEquals(JsonValue other)
        {
            if (other == null || Kind != other.Kind)
                return false;
            switch (Kind)
            {
                case JsonKind.Null:
                    return true;
                case JsonKind.Bool:
                case JsonKind.Number:
                    return _number == other._number;
                case JsonKind.String:
                    return string.Equals(_string, other._string, StringComparison.Ordinal);
                case JsonKind.Array:
                    if (_items.Count != other._items.Count)
                        return false;
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (!_items[i].StructurallyEquals(other._items[i]))
                            return false;
                    }
                    return true;
                default:
                    if (_members.Count != other._members.Count)
                        return false;
                    foreach (KeyValuePair<string, JsonValue> pair in _members)
                    {
                        if (!other._members.TryGetValue(pair.Key, out JsonValue match) || !pair.Value.StructurallyEquals(match))
                            return false;
                    }
                    return true;
            }
        }

        public static JsonValue Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            return new JsonReader(text).ReadDocument();
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try
            {
                value = Parse(text);
                error = null;
                return true;
            }
            catch (JsonParseException e)
            {
                value = null;
                error = e.Message;
                return false;
            }
        }

        public string ToJson(bool indented = false)
        {
            StringBuilder builder = new StringBuilder();
            Write(builder, indented, 0);
            return builder.ToString();
        }

        public override string ToString()
        {
            return ToJson();
        }

        private void Write(StringBuilder sb, bool indented, int level)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_number != 0 ? "true" : "false");
                    break;
                case JsonKind.Number:
                    AppendNumber(sb, _number);
                    break;
                case JsonKind.String:
                    AppendString(sb, _string);
                    break;
                case JsonKind.Array:
                    if (_items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        if (indented)
                            NewLine(sb, level + 1);
                        _items[i].Write(sb, indented, level + 1);
                    }
                    if (indented)
                        NewLine(sb, level);
                    sb.Append(']');
                    break;
                default:
                    if (_members.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, JsonValue> pair in _members)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        if (indented)
                            NewLine(sb, level + 1);
                        AppendString(sb, pair.Key);
                        sb.Append(indented ? ": " : ":");
                        pair.Value.Write(sb, indented, level + 1);
                    }
                    if (indented)
                        NewLine(sb, level);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, int level)
        {
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void AppendNumber(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException("JSON cannot represent NaN or Infinity.");
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static void AppendString(StringBuilder sb, string text)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class JsonReader
        {
            private readonly string _text;
            private int _pos;

            public JsonReader(string text)
            {
                _text = text;
            }

            public JsonValue ReadDocument()
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                    throw Error("empty input");
                JsonValue value = ReadValue(0);
                SkipWhitespace();
                if (_pos != _text.Length)
                    throw Error("unexpected trailing characters");
                return value;
            }

            private JsonParseException Error(string message)
            {
                return new JsonParseException(message, _pos);
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                        break;
                    _pos++;
                }
            }

            private JsonValue ReadValue(int depth)
            {
                if (_pos >= _text.Length)
                    throw Error("unexpected end of input");
                switch (_text[_pos])
                {
                    case '{':
                        return ReadObject(depth + 1);
                    case '[':
                        return ReadArray(depth + 1);
                    case '"':
                        return Of(ReadString());
                    case 't':
                        ReadLiteral("true");
                        return True;
                    case 'f':
                        ReadLiteral("false");
                        return False;
                    case 'n':
                        ReadLiteral("null");
                        return Null;
                    default:
                        return ReadNumber();
                }
            }

            private void ReadLiteral(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                    throw Error("invalid literal");
                _pos += word.Length;
            }

            private JsonValue ReadNumber()
            {
                int start = _pos;
                if (_pos < _text.Length && _text[_pos] == '-')
                    _pos++;
                if (_pos >= _text.Length)
                    throw Error("invalid number");
                if (_text[_pos] == '0')
                {
                    _pos++;
                }
                else if (_text[_pos] >= '1' && _text[_pos] <= '9')
                {
                    while (_pos < _text.Length && IsDigit(_text[_pos]))
                        _pos++;
                }
                else
                {
                    _pos = start;
                    throw Error("unexpected character");
                }
                if (_pos < _text.Length && _text[_pos] == '.')
                {
                    _pos++;
                    if (_pos >= _text.Length || !IsDigit(_text[_pos]))
                        throw Error("invalid number");
                    while (_pos < _text.Length && IsDigit(_text[_pos]))
                        _pos++;
                }
                if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
                        _pos++;
                    if (_pos >= _text.Length || !IsDigit(_text[_pos]))
                        throw Error("invalid number");
                    while (_pos < _text.Length && IsDigit(_text[_pos]))
                        _pos++;
                }
                if (_pos - start >= 64)
                    throw Error("number literal is too long");
                double number = double.Parse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(number) || double.IsNaN(number))
                    throw Error("number is out of range");
                return Of(number);
            }

            private static bool IsDigit(char c)
            {
                return c >= '0' && c <= '9';
            }

            private string ReadString()
            {
                _pos++;
                StringBuilder builder = null;
                int run = _pos;
                while (true)
                {
                    if (_pos >= _text.Length)
                        throw Error("unterminated string");
                    char c = _text[_pos];
                    if (c == '"')
                    {
                        string tail = _text.Substring(run, _pos - run);
                        _pos++;
                        if (builder == null)
                            return tail;
                        builder.Append(tail);
                        return builder.ToString();
                    }
                    if (c < 0x20)
                        throw Error("control character in string");
                    if (c != '\\')
                    {
                        _pos++;
                        continue;
                    }
                    if (builder == null)
                        builder = new StringBuilder();
                    builder.Append(_text, run, _pos - run);
                    _pos++;
                    if (_pos >= _text.Length)
                        throw Error("unterminated escape");
                    char escape = _text[_pos++];
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            builder.Append(ReadHex4());
                            break;
                        default:
                            _pos--;
                            throw Error("invalid escape");
                    }
                    run = _pos;
                }
            }

            private char ReadHex4()
            {
                if (_pos + 4 > _text.Length)
                    throw Error("truncated unicode escape");
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = _text[_pos + i];
                    int digit;
                    if (c >= '0' && c <= '9')
                        digit = c - '0';
                    else if (c >= 'a' && c <= 'f')
                        digit = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F')
                        digit = c - 'A' + 10;
                    else
                        throw Error("invalid unicode escape");
                    value = (value << 4) | digit;
                }
                _pos += 4;
                return (char)value;
            }

            private JsonValue ReadArray(int depth)
            {
                if (depth > MaxDepth)
                    throw Error("nesting is too deep");
                _pos++;
                JsonValue array = NewArray();
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ']')
                {
                    _pos++;
                    return array;
                }
                while (true)
                {
                    SkipWhitespace();
                    array._items.Add(ReadValue(depth));
                    SkipWhitespace();
                    if (_pos >= _text.Length)
                        throw Error("unterminated array");
                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _pos++;
                        return array;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            private JsonValue ReadObject(int depth)
            {
                if (depth > MaxDepth)
                    throw Error("nesting is too deep");
                _pos++;
                JsonValue obj = NewObject();
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '}')
                {
                    _pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (_pos >= _text.Length || _text[_pos] != '"')
                        throw Error("expected a string key");
                    string key = ReadString();
                    SkipWhitespace();
                    if (_pos >= _text.Length || _text[_pos] != ':')
                        throw Error("expected ':'");
                    _pos++;
                    SkipWhitespace();
                    obj._members[key] = ReadValue(depth);
                    SkipWhitespace();
                    if (_pos >= _text.Length)
                        throw Error("unterminated object");
                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _pos++;
                        return obj;
                    }
                    throw Error("expected ',' or '}'");
                }
            }
        }
    }
}
