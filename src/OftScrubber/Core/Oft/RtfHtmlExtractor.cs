using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OftScrubber.Core.Oft
{
    /// <summary>
    /// Recovers the original HTML from RTF that encapsulates it, following MS-OXRTFEX. Only RTF
    /// marked with \fromhtml1 is handled; anything else is left to the plain-text body.
    /// </summary>
    internal static class RtfHtmlExtractor
    {
        private const int MaxGroupDepth = 4096;

        private static readonly HashSet<string> SkippedDestinations = new HashSet<string>(StringComparer.Ordinal)
        {
            "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "listtable", "listoverridetable",
            "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping", "latentstyles", "datastore",
            "filetbl", "revtbl", "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr", "footerf",
        };

        private sealed class GroupState
        {
            public bool Suppressed;   // inside \htmlrtf ... \htmlrtf0
            public bool InHtmlTag;    // inside {\*\htmltag ...}
            public bool Skip;         // inside a destination that produces no HTML
            public bool ExpectStar;   // saw \* and the next control word names the destination
            public int UnicodeSkip = 1;

            public GroupState Clone()
            {
                return (GroupState)MemberwiseClone();
            }
        }

        /// <summary>Returns the encapsulated HTML, or null when the RTF was not produced from HTML.</summary>
        public static string? Extract(byte[] rtf)
        {
            if (!IsFromHtml(rtf)) return null;

            Encoding encoding = MapiProperties.GetEncoding(1252);
            StringBuilder html = new StringBuilder(rtf.Length);
            List<byte> pendingBytes = new List<byte>();
            Stack<GroupState> stack = new Stack<GroupState>();
            GroupState state = new GroupState();
            bool groupStart = false;
            int skipChars = 0;
            int i = 0;

            while (i < rtf.Length)
            {
                byte c = rtf[i];
                if (c == (byte)'{')
                {
                    if (stack.Count >= MaxGroupDepth) throw new InvalidDataException("RTF groups are nested too deeply.");
                    stack.Push(state);
                    state = state.Clone();
                    state.ExpectStar = false;
                    groupStart = true;
                    skipChars = 0;
                    i++;
                    continue;
                }

                if (c == (byte)'}')
                {
                    if (stack.Count == 0) break;
                    state = stack.Pop();
                    groupStart = false;
                    skipChars = 0;
                    i++;
                    continue;
                }

                if (c == (byte)'\r' || c == (byte)'\n')
                {
                    i++;
                    continue;
                }

                bool atGroupStart = groupStart;
                groupStart = false;

                if (c != (byte)'\\')
                {
                    i++;
                    state.ExpectStar = false;
                    if (skipChars > 0) { skipChars--; continue; }
                    if (Emits(state)) pendingBytes.Add(c);
                    continue;
                }

                // Control symbol or control word.
                i++;
                if (i >= rtf.Length) break;
                byte next = rtf[i];
                if (!IsLetter(next))
                {
                    i++;
                    if (next == (byte)'\'')
                    {
                        int value = i + 1 < rtf.Length ? HexValue(rtf[i], rtf[i + 1]) : -1;
                        if (value >= 0) i += 2;
                        if (skipChars > 0) { skipChars--; continue; }
                        if (value >= 0 && Emits(state)) pendingBytes.Add((byte)value);
                        continue;
                    }
                    if (next == (byte)'*')
                    {
                        state.ExpectStar = atGroupStart;
                        continue;
                    }
                    state.ExpectStar = false;
                    if (skipChars > 0) { skipChars--; continue; }
                    string? symbol = SymbolText(next);
                    if (symbol != null && Emits(state)) Append(html, pendingBytes, encoding, symbol);
                    continue;
                }

                int wordStart = i;
                while (i < rtf.Length && IsLetter(rtf[i]) && i - wordStart < 32) i++;
                string word = Encoding.ASCII.GetString(rtf, wordStart, i - wordStart);
                bool hasParam = false;
                bool negative = false;
                long param = 0;
                if (i < rtf.Length && rtf[i] == (byte)'-') { negative = true; i++; }
                int digitStart = i;
                while (i < rtf.Length && rtf[i] >= (byte)'0' && rtf[i] <= (byte)'9' && i - digitStart < 10)
                {
                    param = param * 10 + (rtf[i] - (byte)'0');
                    i++;
                }
                hasParam = i > digitStart;
                if (negative) param = -param;
                if (i < rtf.Length && rtf[i] == (byte)' ') i++;

                if (word == "bin")
                {
                    // Raw binary data: never interpreted, only skipped.
                    long skip = hasParam && param > 0 ? param : 0;
                    i = (int)Math.Min(rtf.Length, i + skip);
                    continue;
                }

                if (state.ExpectStar)
                {
                    // {\* followed by the destination name. Only htmltag is kept; mhtmltag holds a
                    // rewritten copy of an htmltag and, like other ignorable destinations, is skipped.
                    state.ExpectStar = false;
                    if (word == "htmltag" && !state.Skip)
                    {
                        FlushBytes(html, pendingBytes, encoding);
                        state.InHtmlTag = true;
                    }
                    else
                    {
                        state.Skip = true;
                    }
                    continue;
                }

                if (SkippedDestinations.Contains(word))
                {
                    state.Skip = true;
                    continue;
                }

                switch (word)
                {
                    case "ansicpg":
                        if (hasParam)
                        {
                            FlushBytes(html, pendingBytes, encoding);
                            encoding = MapiProperties.GetEncoding((int)param);
                        }
                        continue;
                    case "htmlrtf":
                        state.Suppressed = !hasParam || param != 0;
                        continue;
                    case "uc":
                        state.UnicodeSkip = hasParam && param >= 0 && param < 16 ? (int)param : 1;
                        continue;
                    case "u":
                        if (hasParam && Emits(state))
                        {
                            int code = (int)(param < 0 ? param + 65536 : param) & 0xFFFF;
                            Append(html, pendingBytes, encoding, ((char)code).ToString());
                        }
                        skipChars = state.UnicodeSkip;
                        continue;
                }

                if (skipChars > 0) { skipChars--; continue; }
                string? text = WordText(word);
                if (text != null && Emits(state)) Append(html, pendingBytes, encoding, text);
            }

            FlushBytes(html, pendingBytes, encoding);
            return html.ToString();
        }

        private static bool Emits(GroupState state)
        {
            if (state.Skip) return false;
            return state.InHtmlTag || !state.Suppressed;
        }

        /// <summary>MS-OXRTFEX 2.2.3.1: \fromhtml must appear within the first 10 tokens, which may only be groups and control words.</summary>
        private static bool IsFromHtml(byte[] rtf)
        {
            int tokens = 0;
            int i = 0;
            while (i < rtf.Length && tokens < 10)
            {
                byte c = rtf[i];
                if (c == (byte)'\r' || c == (byte)'\n' || c == (byte)' ') { i++; continue; }
                if (c == (byte)'{') { tokens++; i++; continue; }
                if (c != (byte)'\\' || i + 1 >= rtf.Length) return false;
                i++;
                if (!IsLetter(rtf[i]))
                {
                    if (rtf[i] != (byte)'*') return false;
                    tokens++;
                    i++;
                    continue;
                }
                int start = i;
                while (i < rtf.Length && IsLetter(rtf[i]) && i - start < 32) i++;
                string word = Encoding.ASCII.GetString(rtf, start, i - start);
                if (word == "fromhtml") return true;
                if (word == "fromtext") return false;
                if (i < rtf.Length && rtf[i] == (byte)'-') i++;
                while (i < rtf.Length && rtf[i] >= (byte)'0' && rtf[i] <= (byte)'9') i++;
                tokens++;
            }
            return false;
        }

        private static string? SymbolText(byte symbol)
        {
            switch ((char)symbol)
            {
                case '{': return "{";
                case '}': return "}";
                case '\\': return "\\";
                case '~': return " ";
                case '_': return "­";
                default: return null;
            }
        }

        private static string? WordText(string word)
        {
            switch (word)
            {
                case "par": return "\r\n";
                case "line": return "\r\n";
                case "tab": return "\t";
                case "lquote": return "‘";
                case "rquote": return "’";
                case "ldblquote": return "“";
                case "rdblquote": return "”";
                case "bullet": return "•";
                case "endash": return "–";
                case "emdash": return "—";
                default: return null;
            }
        }

        private static void Append(StringBuilder html, List<byte> pendingBytes, Encoding encoding, string text)
        {
            FlushBytes(html, pendingBytes, encoding);
            html.Append(text);
        }

        /// <summary>Decodes accumulated 8-bit text in one go so double-byte code pages decode correctly.</summary>
        private static void FlushBytes(StringBuilder html, List<byte> pendingBytes, Encoding encoding)
        {
            if (pendingBytes.Count == 0) return;
            html.Append(encoding.GetString(pendingBytes.ToArray()));
            pendingBytes.Clear();
        }

        private static bool IsLetter(byte b)
        {
            return (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');
        }

        private static int HexValue(byte high, byte low)
        {
            int h = HexDigit(high);
            int l = HexDigit(low);
            return h < 0 || l < 0 ? -1 : (h << 4) | l;
        }

        private static int HexDigit(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - 'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - 'A' + 10;
            return -1;
        }
    }
}
