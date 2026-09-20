using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YourAI.Core.Json
{
    public enum JsonKind
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5,
    }

    /// <summary>
    /// A parsed JSON value.
    ///
    /// Why this exists rather than a reference to an existing library: the core
    /// assembly is forbidden from referencing UnityEngine, so <c>JsonUtility</c> is
    /// out; and Unity's .NET Standard 2.1 profile does not ship
    /// <c>System.Text.Json</c>. Options were a third-party dependency (which would
    /// break the "core depends on nothing" promise and make offline verification
    /// harder) or roughly two hundred lines here. This is the two hundred lines.
    ///
    /// Indexing never throws. Reading a missing key, a wrong type, or an
    /// out-of-range index yields <see cref="JsonValue.Null"/>, because a malformed
    /// model response is a routine event, not an exceptional one -- it should
    /// produce an empty delta, not an exception thrown across a network callback.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };

        public JsonKind Kind;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;

        /// <summary>Elements when <see cref="Kind"/> is Array.</summary>
        public List<JsonValue> Items;

        /// <summary>Members when <see cref="Kind"/> is Object.</summary>
        public Dictionary<string, JsonValue> Members;

        /// <summary>
        /// Re-emits this value as JSON text.
        ///
        /// <c>ToString</c> is for people -- it prints "{1 members}" -- so it must never
        /// be used where JSON is expected. This is the one to call when a parsed value
        /// has to go back on the wire, which happens whenever an argument object read
        /// out of one dialect has to be written into another. Gemini, for instance,
        /// hands back args as a real object where the others hand back a string, so the
        /// only way to keep the framework's canonical representation is to re-serialise.
        /// </summary>
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(64);
            WriteJson(sb);
            return sb.ToString();
        }

        private void WriteJson(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Bool:
                    sb.Append(BoolValue ? "true" : "false");
                    return;

                case JsonKind.Number:
                    // Invariant, because a culture that formats a half as "0,5" would
                    // emit JSON that does not parse back.
                    sb.Append(NumberValue.ToString("0.################", CultureInfo.InvariantCulture));
                    return;

                case JsonKind.String:
                    sb.Append('"').Append(JsonParser.Escape(StringValue ?? string.Empty)).Append('"');
                    return;

                case JsonKind.Array:
                    sb.Append('[');
                    if (Items != null)
                    {
                        for (int i = 0; i < Items.Count; i++)
                        {
                            if (i > 0)
                            {
                                sb.Append(',');
                            }

                            if (Items[i] != null)
                            {
                                Items[i].WriteJson(sb);
                            }
                            else
                            {
                                sb.Append("null");
                            }
                        }
                    }
                    sb.Append(']');
                    return;

                case JsonKind.Object:
                    sb.Append('{');
                    if (Members != null)
                    {
                        int written = 0;
                        foreach (KeyValuePair<string, JsonValue> member in Members)
                        {
                            if (written > 0)
                            {
                                sb.Append(',');
                            }

                            sb.Append('"').Append(JsonParser.Escape(member.Key)).Append("\":");
                            if (member.Value != null)
                            {
                                member.Value.WriteJson(sb);
                            }
                            else
                            {
                                sb.Append("null");
                            }
                            written++;
                        }
                    }
                    sb.Append('}');
                    return;

                default:
                    sb.Append("null");
                    return;
            }
        }

        public bool IsNull
        {
            get { return Kind == JsonKind.Null; }
        }

        public bool IsObject
        {
            get { return Kind == JsonKind.Object; }
        }

        public bool IsArray
        {
            get { return Kind == JsonKind.Array; }
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) { return Items != null ? Items.Count : 0; }
                if (Kind == JsonKind.Object) { return Members != null ? Members.Count : 0; }
                return 0;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind == JsonKind.Array && Items != null && index >= 0 && index < Items.Count)
                {
                    return Items[index];
                }
                return Null;
            }
        }

        public JsonValue this[string key]
        {
            get
            {
                JsonValue found;
                if (Kind == JsonKind.Object && Members != null && key != null
                    && Members.TryGetValue(key, out found))
                {
                    return found;
                }
                return Null;
            }
        }

        /// <summary>True when the member exists, regardless of its value.</summary>
        public bool Has(string key)
        {
            return Kind == JsonKind.Object && Members != null && key != null && Members.ContainsKey(key);
        }

        public string AsString(string fallback = null)
        {
            return Kind == JsonKind.String ? StringValue : fallback;
        }

        /// <summary>Non-empty string, or null. Convenient for optional model fields.</summary>
        public string AsNonEmptyString()
        {
            if (Kind != JsonKind.String || string.IsNullOrEmpty(StringValue))
            {
                return null;
            }
            return StringValue;
        }

        public double AsDouble(double fallback = 0)
        {
            return Kind == JsonKind.Number ? NumberValue : fallback;
        }

        public int AsInt(int fallback = 0)
        {
            return Kind == JsonKind.Number ? (int)NumberValue : fallback;
        }

        public bool AsBool(bool fallback = false)
        {
            return Kind == JsonKind.Bool ? BoolValue : fallback;
        }

        /// <summary>
        /// Walks a dotted path, using a numeric segment to index an array:
        /// <c>Path("choices.0.delta.content")</c>. Returns
        /// <see cref="JsonValue.Null"/> when any step is missing.
        /// </summary>
        public JsonValue Path(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return this;
            }

            JsonValue current = this;
            int start = 0;
            while (start <= path.Length)
            {
                int dot = path.IndexOf('.', start);
                string segment = dot < 0 ? path.Substring(start) : path.Substring(start, dot - start);

                int index;
                if (int.TryParse(segment, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out index))
                {
                    current = current[index];
                }
                else
                {
                    current = current[segment];
                }

                if (current.IsNull)
                {
                    return Null;
                }
                if (dot < 0)
                {
                    break;
                }
                start = dot + 1;
            }

            return current;
        }

        public string PathString(string path)
        {
            return Path(path).AsString();
        }

        public bool TryGetString(string path, out string value)
        {
            JsonValue found = Path(path);
            if (found.Kind == JsonKind.String)
            {
                value = found.StringValue;
                return true;
            }
            value = null;
            return false;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return BoolValue ? "true" : "false";
                case JsonKind.Number: return NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonKind.String: return StringValue;
                case JsonKind.Array: return "[" + Count + " items]";
                case JsonKind.Object: return "{" + Count + " members}";
                default: return "?";
            }
        }
    }
}
