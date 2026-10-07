using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Expanse.Domain.Colonies
{
    // Narrow, bounded DTO codec for Unity's stripped framework. No runtime type
    // metadata, arbitrary constructors, external assemblies or converter discovery.
    internal static class ColonyJson
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly Dictionary<Type, PropertyInfo[]> Properties = new Dictionary<Type, PropertyInfo[]>();
        static PropertyInfo[] Members(Type type)
        {
            if (type.Namespace != typeof(ColonyState).Namespace || !type.IsClass || type.GetConstructor(Type.EmptyTypes) == null) throw new InvalidDataException("Unsupported colony DTO type.");
            lock (Properties)
            {
                if (!Properties.TryGetValue(type, out var members))
                {
                    members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                    Properties.Add(type, members);
                }
                return members;
            }
        }
        public static byte[] Serialize<T>(T value, int maximumBytes)
        {
            var writer = new Writer(maximumBytes); writer.Value(value, 0); var bytes = Utf8.GetBytes(writer.Text.ToString());
            if (bytes.Length == 0 || bytes.Length > maximumBytes) throw new InvalidDataException("Colony JSON exceeds its payload bound.");
            return bytes;
        }
        public static T Deserialize<T>(byte[] bytes, int maximumBytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > maximumBytes) throw new InvalidDataException("Invalid colony JSON size.");
            try
            {
                var reader = new Reader(Utf8.GetString(bytes)); var value = reader.Value(0); reader.White();
                if (reader.Position != reader.Text.Length || value.Object == null) throw new InvalidDataException("Expected one complete JSON object.");
                return (T)value.Decode(typeof(T))!;
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is DecoderFallbackException || ex is TargetInvocationException)
            { throw new InvalidDataException("Invalid colony JSON term.", ex); }
        }
        sealed class Writer
        {
            internal readonly StringBuilder Text = new StringBuilder(); readonly int maximum; int nodes;
            internal Writer(int maximum) { this.maximum = maximum; }
            void Check() { if (Text.Length > maximum) throw new InvalidDataException("Colony JSON exceeds its payload bound."); }
            internal void Value(object? value, int depth)
            {
                if (depth > 64 || ++nodes > 250000) throw new InvalidDataException("Colony JSON graph bound exceeded.");
                if (value == null) Text.Append("null");
                else if (value is string text) Quoted(text);
                else if (value is bool boolean) Text.Append(boolean ? "true" : "false");
                else if (value is double number) { if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Non-finite JSON number."); Text.Append(number.ToString("R", CultureInfo.InvariantCulture)); }
                else if (value is decimal || value is long || value is int || value is uint) Text.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                else if (value is IDictionary dictionary)
                {
                    Text.Append('{'); bool first = true; var keys = dictionary.Keys.Cast<object>().ToArray();
                    if (keys.Any(x => !(x is string))) throw new InvalidDataException("JSON dictionaries require string keys.");
                    foreach (string key in keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal)) { if (!first) Text.Append(','); first = false; Quoted(key); Text.Append(':'); Value(dictionary[key], depth + 1); }
                    Text.Append('}');
                }
                else if (value is IList list)
                { Text.Append('['); bool first = true; foreach (var item in list) { if (!first) Text.Append(','); first = false; Value(item, depth + 1); } Text.Append(']'); }
                else
                {
                    Text.Append('{'); bool first = true;
                    foreach (var member in Members(value.GetType())) { if (!first) Text.Append(','); first = false; Quoted(member.Name); Text.Append(':'); Value(member.GetValue(value, null), depth + 1); }
                    Text.Append('}');
                }
                Check();
            }
            void Quoted(string text)
            {
                Text.Append('"');
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '"' || c == '\\') Text.Append('\\').Append(c);
                    else if (c < 32) Text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsSurrogate(c)) { if (!char.IsHighSurrogate(c) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new InvalidDataException("Invalid Unicode surrogate."); Text.Append(c).Append(text[++i]); }
                    else Text.Append(c);
                    if ((i & 255) == 0) Check();
                }
                Text.Append('"'); Check();
            }
        }
        sealed class JsonValue
        {
            internal Dictionary<string, JsonValue>? Object;
            internal List<JsonValue>? Array;
            internal string? atom, decoded;
            internal object? Decode(Type type)
            {
                var underlying = Nullable.GetUnderlyingType(type);
                if (atom == "null") { if (type.IsValueType && underlying == null) throw new InvalidDataException("Null supplied for required scalar."); return null; }
                if (underlying != null) return Decode(underlying);
                if (type == typeof(string)) { if (decoded == null) throw new InvalidDataException("Expected JSON string."); return decoded; }
                if (type == typeof(bool)) { if (atom != "true" && atom != "false") throw new InvalidDataException("Expected JSON boolean."); return atom == "true"; }
                if ((type == typeof(double) || type == typeof(decimal) || type == typeof(long) || type == typeof(int) || type == typeof(uint)) && (atom == null || decoded != null)) throw new InvalidDataException("Expected JSON numeric atom.");
                if (type == typeof(double)) { double n = double.Parse(atom!, NumberStyles.Float, CultureInfo.InvariantCulture); if (double.IsNaN(n) || double.IsInfinity(n)) throw new InvalidDataException("Invalid finite JSON number."); return n; }
                if (type == typeof(decimal)) return decimal.Parse(atom!, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (type == typeof(long)) return long.Parse(atom!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (type == typeof(int)) return int.Parse(atom!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (type == typeof(uint)) return uint.Parse(atom!, NumberStyles.None, CultureInfo.InvariantCulture);
                if (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    if (Array == null) throw new InvalidDataException("Expected JSON array.");
                    Type itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
                    if (type.IsArray) { var result = System.Array.CreateInstance(itemType, Array.Count); for (int i = 0; i < Array.Count; i++) result.SetValue(Array[i].Decode(itemType), i); return result; }
                    var list = (IList)Activator.CreateInstance(type)!; foreach (var item in Array) list.Add(item.Decode(itemType)); return list;
                }
                if (Object == null) throw new InvalidDataException("Expected JSON object.");
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
                { var dictionary = (IDictionary)Activator.CreateInstance(type)!; var itemType = type.GetGenericArguments()[1]; foreach (var item in Object) dictionary.Add(item.Key, item.Value.Decode(itemType)); return dictionary; }
                var members = Members(type); object target = Activator.CreateInstance(type)!;
                // Additive unknown fields cannot instantiate types. Owning contract
                // validators enforce schema version, required values and identities.
                foreach (var member in members) if (Object.TryGetValue(member.Name, out var term)) member.SetValue(target, term.Decode(member.PropertyType), null);
                return target;
            }
        }
        private sealed class Reader
        {
            internal readonly string Text; internal int Position; private int count;
            internal Reader(string text) { Text = text; }
            internal void White() { while (Position < Text.Length && (Text[Position] == ' ' || Text[Position] == '\t' || Text[Position] == '\r' || Text[Position] == '\n')) Position++; }
            private void Need(char c) { White(); if (Position >= Text.Length || Text[Position++] != c) throw new InvalidDataException("Malformed JSON package structure"); }
            internal JsonValue Value(int depth)
            {
                if (depth > 64 || ++count > 250000) throw new InvalidDataException("JSON package graph exceeds bounded depth/items");
                White(); if (Position >= Text.Length) throw new InvalidDataException("Truncated JSON package");
                var result = new JsonValue(); char c = Text[Position];
                if (c == '{')
                {
                    Position++; White(); result.Object = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    if (Position < Text.Length && Text[Position] == '}') { Position++; return result; }
                    while (true)
                    {
                        var key = Quoted(); Need(':'); var value = Value(depth + 1); 
                        if (key.decoded == null || result.Object.ContainsKey(key.decoded)) throw new InvalidDataException("Missing or duplicate decoded JSON key"); result.Object.Add(key.decoded, value);
                        White(); if (Position < Text.Length && Text[Position] == '}') { Position++; break; } Need(',');
                    }
                }
                else if (c == '[')
                {
                    Position++; White(); result.Array = new List<JsonValue>();
                    if (Position < Text.Length && Text[Position] == ']') { Position++; return result; }
                    while (true) { result.Array.Add(Value(depth + 1)); White(); if (Position < Text.Length && Text[Position] == ']') { Position++; break; } Need(','); }
                }
                else if (c == '"') result = Quoted();
                else
                {
                    int begin = Position;
                    while (Position < Text.Length && ",]} \r\n\t".IndexOf(Text[Position]) < 0) Position++;
                    result.atom = Text.Substring(begin, Position - begin);
                    if (result.atom != "true" && result.atom != "false" && result.atom != "null" && !ValidNumber(result.atom)) throw new InvalidDataException("Invalid JSON package atom");
                }
                return result;
            }
            private JsonValue Quoted()
            {
                White(); int begin = Position; Need('"'); var b = new StringBuilder(); bool ended = false;
                while (Position < Text.Length)
                {
                    char c = Text[Position++]; if (c == '"') { ended = true; break; }
                    if (c < 32) throw new InvalidDataException("Control character in JSON package string");
                    if (c == '\\')
                    {
                        if (Position >= Text.Length) throw new InvalidDataException("Truncated JSON package escape");
                        c = Text[Position++];
                        if (c == 'u')
                        { if (Position + 4 > Text.Length) throw new InvalidDataException("Truncated Unicode escape"); c = (char)int.Parse(Text.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); Position += 4; }
                        else { int k = "\"\\/bfnrt".IndexOf(c); if (k < 0) throw new InvalidDataException("Invalid JSON escape"); c = "\"\\/\b\f\n\r\t"[k]; }
                    }
                    b.Append(c); if (b.Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized JSON package string");
                }
                if (!ended) throw new InvalidDataException("Unclosed JSON package string");
                string decoded = b.ToString();
                for (int i = 0; i < decoded.Length; i++) if (char.IsSurrogate(decoded[i])) { if (!char.IsHighSurrogate(decoded[i]) || i + 1 >= decoded.Length || !char.IsLowSurrogate(decoded[++i])) throw new InvalidDataException("Invalid JSON Unicode surrogate"); }
                return new JsonValue { atom = Text.Substring(begin, Position - begin), decoded = decoded };
            }
            private static bool ValidNumber(string text)
            {
                int i = 0; if (text.Length > 128 || text.Length == 0) return false; if (text[i] == '-') { if (++i == text.Length) return false; }
                if (text[i] == '0') i++; else { if (text[i] < '1' || text[i] > '9') return false; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; }
                if (i < text.Length && text[i] == '.') { int start = ++i; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; if (start == i) return false; }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E')) { i++; if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++; int start = i; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; if (start == i) return false; }
                return i == text.Length;
            }
        }
    }
}
