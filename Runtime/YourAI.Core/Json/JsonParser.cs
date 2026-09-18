using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YourAI.Core.Json
{
    /// <summary>
    /// A small recursive-descent JSON parser.
    ///
    /// Scope is deliberately narrow: enough to read what a model returns. It does
    /// not preserve member order, does not track positions for error recovery, and
    /// does not stream. What it does do is handle escapes correctly, including
    /// surrogate pairs, which is the part naive implementations get wrong and the
    /// part that matters when a model emits an emoji.
    ///
    /// Depth is capped because a malformed response containing thousands of nested
    /// brackets should produce an error rather than a stack overflow inside a
    /// network callback.
    /// </summary>
    public static class JsonParser
    {
        /// <summary>Deep enough for any real payload, shallow enough to stay off the stack limit.</summary>
        public const int MaxDepth = 64;

        /// <summary>
        /// Parses without throwing. On failure <paramref name="error"/> explains what
        /// went wrong and where; <paramref name="value"/> is <see cref="JsonValue.Null"/>.
        /// </summary>
        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            value = JsonValue.Null;
            error = null;

            if (text == null)
            {
                error = "input is null";
                return false;
            }

            Cursor cursor = new Cursor(text);
            cursor.SkipWhitespace();

            if (cursor.At >= text.Length)
            {
                error = "input is empty";
                return false;
            }

            JsonValue parsed;
            if (!cursor.ReadValue(0, out parsed, out error))
            {
                return false;
            }

            cursor.SkipWhitespace();
            if (cursor.At != text.Length)
            {
                error = "unexpected trailing content at offset " + cursor.At;
                return false;
            }

            value = parsed;
            return true;
        }

        /// <summary>Parses, throwing <see cref="FormatException"/> on malformed input.</summary>
        public static JsonValue Parse(string text)
        {
            JsonValue value;
            string error;
            if (!TryParse(text, out value, out error))
            {
                throw new FormatException("invalid JSON: " + error);
            }
            return value;
        }

        /// <summary>
        /// Escapes a string for embedding in JSON text. Solidus is left alone, since
        /// escaping it is legal but noisy and models do not require it.
        /// </summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // Surrogate halves pass through untouched; C# strings are
                            // already UTF-16, so a pair stays a pair.
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private sealed class Cursor
        {
            private readonly string _text;
            private int _at;

            public Cursor(string text)
            {
                _text = text;
            }

            public int At
            {
                get { return _at; }
            }

            public void SkipWhitespace()
            {
                while (_at < _text.Length)
                {
                    char c = _text[_at];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        _at++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public bool ReadValue(int depth, out JsonValue value, out string error)
            {
                value = JsonValue.Null;
                error = null;

                if (depth > MaxDepth)
                {
                    error = "nesting exceeded " + MaxDepth + " levels";
                    return false;
                }

                SkipWhitespace();
                if (_at >= _text.Length)
                {
                    error = "unexpected end of input";
                    return false;
                }

                char c = _text[_at];
                switch (c)
                {
                    case '{': return ReadObject(depth, out value, out error);
                    case '[': return ReadArray(depth, out value, out error);
                    case '"':
                        string s;
                        if (!ReadString(out s, out error)) { return false; }
                        value = new JsonValue { Kind = JsonKind.String, StringValue = s };
                        return true;
                    case 't':
                        return ReadLiteral("true", new JsonValue { Kind = JsonKind.Bool, BoolValue = true },
                            out value, out error);
                    case 'f':
                        return ReadLiteral("false", new JsonValue { Kind = JsonKind.Bool, BoolValue = false },
                            out value, out error);
                    case 'n':
                        return ReadLiteral("null", JsonValue.Null, out value, out error);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber(out value, out error);
                        }
                        error = "unexpected character '" + c + "' at offset " + _at;
                        return false;
                }
            }

            private bool ReadObject(int depth, out JsonValue value, out string error)
            {
                value = JsonValue.Null;
                error = null;

                _at++; // '{'
                Dictionary<string, JsonValue> members = new Dictionary<string, JsonValue>();

                SkipWhitespace();
                if (_at < _text.Length && _text[_at] == '}')
                {
                    _at++;
                    value = new JsonValue { Kind = JsonKind.Object, Members = members };
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_at >= _text.Length || _text[_at] != '"')
                    {
                        error = "expected a member name at offset " + _at;
                        return false;
                    }

                    string key;
                    if (!ReadString(out key, out error))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (_at >= _text.Length || _text[_at] != ':')
                    {
                        error = "expected ':' after member '" + key + "' at offset " + _at;
                        return false;
                    }
                    _at++;

                    JsonValue memberValue;
                    if (!ReadValue(depth + 1, out memberValue, out error))
                    {
                        return false;
                    }

                    // Last value wins on a duplicate key. RFC 8259 leaves this to the
                    // implementation; overwriting is the least surprising choice.
                    members[key] = memberValue;

                    SkipWhitespace();
                    if (_at >= _text.Length)
                    {
                        error = "unterminated object";
                        return false;
                    }
                    if (_text[_at] == ',')
                    {
                        _at++;
                        continue;
                    }
                    if (_text[_at] == '}')
                    {
                        _at++;
                        value = new JsonValue { Kind = JsonKind.Object, Members = members };
                        return true;
                    }
                    error = "expected ',' or '}' at offset " + _at;
                    return false;
                }
            }

            private bool ReadArray(int depth, out JsonValue value, out string error)
            {
                value = JsonValue.Null;
                error = null;

                _at++; // '['
                List<JsonValue> items = new List<JsonValue>();

                SkipWhitespace();
                if (_at < _text.Length && _text[_at] == ']')
                {
                    _at++;
                    value = new JsonValue { Kind = JsonKind.Array, Items = items };
                    return true;
                }

                while (true)
                {
                    JsonValue item;
                    if (!ReadValue(depth + 1, out item, out error))
                    {
                        return false;
                    }
                    items.Add(item);

                    SkipWhitespace();
                    if (_at >= _text.Length)
                    {
                        error = "unterminated array";
                        return false;
                    }
                    if (_text[_at] == ',')
                    {
                        _at++;
                        continue;
                    }
                    if (_text[_at] == ']')
                    {
                        _at++;
                        value = new JsonValue { Kind = JsonKind.Array, Items = items };
                        return true;
                    }
                    error = "expected ',' or ']' at offset " + _at;
                    return false;
                }
            }

            private bool ReadString(out string result, out string error)
            {
                result = null;
                error = null;

                _at++; // opening quote
                StringBuilder sb = new StringBuilder(32);

                while (true)
                {
                    if (_at >= _text.Length)
                    {
                        error = "unterminated string";
                        return false;
                    }

                    char c = _text[_at++];

                    if (c == '"')
                    {
                        result = sb.ToString();
                        return true;
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (_at >= _text.Length)
                    {
                        error = "unterminated escape sequence";
                        return false;
                    }

                    char esc = _text[_at++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_at + 4 > _text.Length)
                            {
                                error = "truncated \\u escape";
                                return false;
                            }
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int digit = HexDigit(_text[_at + i]);
                                if (digit < 0)
                                {
                                    error = "invalid hex digit in \\u escape at offset " + (_at + i);
                                    return false;
                                }
                                code = (code << 4) | digit;
                            }
                            _at += 4;
                            // Appended as a bare UTF-16 code unit on purpose: a
                            // surrogate pair arrives as two consecutive \u escapes and
                            // must be reassembled into the pair, not merged into one
                            // code point.
                            sb.Append((char)code);
                            break;
                        default:
                            error = "unknown escape '\\" + esc + "' at offset " + (_at - 1);
                            return false;
                    }
                }
            }

            private bool ReadNumber(out JsonValue value, out string error)
            {
                value = JsonValue.Null;
                error = null;

                int start = _at;
                if (_at < _text.Length && (_text[_at] == '-' || _text[_at] == '+'))
                {
                    _at++;
                }
                while (_at < _text.Length)
                {
                    char c = _text[_at];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        _at++;
                    }
                    else
                    {
                        break;
                    }
                }

                string slice = _text.Substring(start, _at - start);
                double parsed;
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    error = "invalid number '" + slice + "' at offset " + start;
                    return false;
                }

                value = new JsonValue { Kind = JsonKind.Number, NumberValue = parsed };
                return true;
            }

            private bool ReadLiteral(string literal, JsonValue produced, out JsonValue value, out string error)
            {
                value = JsonValue.Null;
                error = null;

                if (_at + literal.Length > _text.Length
                    || string.CompareOrdinal(_text, _at, literal, 0, literal.Length) != 0)
                {
                    error = "expected '" + literal + "' at offset " + _at;
                    return false;
                }

                _at += literal.Length;
                value = produced;
                return true;
            }

            private static int HexDigit(char c)
            {
                if (c >= '0' && c <= '9') { return c - '0'; }
                if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
                if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
                return -1;
            }
        }
    }
}
