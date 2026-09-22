using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OftScrubber.Core.Templating
{
    /// <summary>
    /// A view of HTML as the text a reader sees, mapped back to source offsets.
    ///
    /// Tokens are found in that logical text, so a token Word split across inline spans
    /// ({{Tail&lt;/span&gt;&lt;span&gt;Number}}) or wrote with entities (&amp;lt;&amp;lt;Name&amp;gt;&amp;gt;)
    /// is still one token. Replacement edits only the source characters of the token: markup is
    /// never rebuilt, so everything else in the document survives byte for byte.
    ///
    /// Text inside inline elements forms one run. Block boundaries, comments (including Word
    /// conditional comments) and raw-text elements such as style end a run, so a token can
    /// never match across a paragraph or into CSS.
    /// </summary>
    public sealed class HtmlText
    {
        private static readonly HashSet<string> InlineElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "abbr", "b", "bdi", "bdo", "big", "cite", "code", "em", "font", "i", "ins", "del",
            "kbd", "mark", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup",
            "u", "var", "o:p", "wbr",
        };

        // Content is not text: never scanned, never edited.
        private static readonly HashSet<string> RawTextElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "xml", "title", "textarea", "noscript",
        };

        // Attributes that carry reader-visible text or a link. style, class and the like are
        // left alone so CSS can never be mistaken for a token.
        private static readonly HashSet<string> ScannedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "href", "src", "title", "alt",
        };

        private static readonly Regex AttributePattern = new Regex(
            @"(?<name>[^\s""'>/=]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+)))?",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly string _source;
        private readonly List<Run> _runs = new List<Run>();

        public HtmlText(string html)
        {
            _source = html ?? "";
            Parse();
        }

        public string Source => _source;

        /// <summary>A logical text run and the source span of each of its characters.</summary>
        private sealed class Run
        {
            public readonly StringBuilder Text = new StringBuilder();
            public readonly List<int> SourceStart = new List<int>();
            public readonly List<int> SourceEnd = new List<int>();
            public bool IsAttribute;
            public bool IsUrl;
            public bool IsQuoted;

            /// <summary>Per character: inside an existing &lt;a&gt;, where a link value must not nest another.</summary>
            public readonly List<bool> InLink = new List<bool>();
            public bool InLinkNow;

            public void Add(char c, int start, int end)
            {
                Text.Append(c);
                SourceStart.Add(start);
                SourceEnd.Add(end);
                InLink.Add(InLinkNow);
            }
        }

        /// <summary>A token found in the document, with a location for the UI.</summary>
        public sealed class Found
        {
            public Found(TokenMatch match, bool inAttribute)
            {
                Match = match;
                InAttribute = inAttribute;
            }

            public TokenMatch Match { get; }
            public bool InAttribute { get; }
        }

        public List<Found> FindTokens(TokenSyntaxRegistry registry)
        {
            var result = new List<Found>();

            foreach (var run in _runs)
                foreach (var match in registry.Find(run.Text.ToString()))
                    result.Add(new Found(match, run.IsAttribute));

            return result;
        }

        public int CountStrayOpeners(TokenSyntaxRegistry registry)
        {
            var count = 0;

            foreach (var run in _runs)
            {
                var text = run.Text.ToString();
                count += registry.CountStrayOpeners(text, registry.Find(text));
            }

            return count;
        }

        /// <summary>
        /// Returns new HTML with tokens replaced. valueFor returns null to leave a token untouched.
        /// Values are plain text and are encoded here for their context; a linked value in body
        /// text becomes one &lt;a&gt; element, the only markup a value can add.
        /// </summary>
        public string Replace(TokenSyntaxRegistry registry, Func<TokenMatch, FormattedValue?> valueFor)
        {
            var edits = new List<Edit>();

            foreach (var run in _runs)
            {
                foreach (var match in registry.Find(run.Text.ToString()))
                {
                    var value = valueFor(match);
                    if (value == null) continue;

                    var encoded = run.IsAttribute ? EncodeAttribute(value, run, match)
                        : value.Linked && value.Url != null && !run.InLink[match.Start] ? EncodeLink(value)
                        : EncodeText(value.Text);
                    AddTokenEdits(run, match, encoded, edits);
                }
            }

            if (edits.Count == 0) return _source;

            edits.Sort((a, b) => a.Start.CompareTo(b.Start));

            var output = new StringBuilder(_source.Length + 256);
            var position = 0;

            foreach (var edit in edits)
            {
                if (edit.Start < position) continue; // Defensive: runs never overlap.
                output.Append(_source, position, edit.Start - position);
                output.Append(edit.Text);
                position = edit.End;
            }

            output.Append(_source, position, _source.Length - position);
            return output.ToString();
        }

        private struct Edit
        {
            public int Start;
            public int End;
            public string Text;
        }

        /// <summary>
        /// The value goes where the token's first character was, inheriting its formatting.
        /// Pieces of the token in later text segments are removed; tags between them stay.
        /// </summary>
        private static void AddTokenEdits(Run run, TokenMatch match, string encoded, List<Edit> edits)
        {
            var first = true;
            var i = match.Start;

            while (i < match.End)
            {
                var start = run.SourceStart[i];
                var end = run.SourceEnd[i];
                var j = i + 1;

                // Extend over characters that are contiguous in the source.
                while (j < match.End && run.SourceStart[j] == end)
                {
                    end = run.SourceEnd[j];
                    j++;
                }

                edits.Add(new Edit { Start = start, End = end, Text = first ? encoded : "" });
                first = false;
                i = j;
            }
        }

        private static string EncodeText(string value)
        {
            var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');

            for (var i = 0; i < lines.Length; i++)
                lines[i] = WebUtility.HtmlEncode(lines[i]);

            return string.Join("<br>", lines);
        }

        private static string EncodeLink(FormattedValue value)
        {
            return "<a href=\"" + EscapeAttribute(value.Url!) + "\">" + EncodeText(value.Text) + "</a>";
        }

        private static string EscapeAttribute(string text)
        {
            return text.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&#39;")
                .Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string EncodeAttribute(FormattedValue value, Run run, TokenMatch match)
        {
            // A token that is the whole link (href="{{Link}}") takes the value as a URL, and a
            // web or email value its own target. Anywhere else in a URL it is data, so it is
            // percent-encoded.
            var wholeValue = match.Start == 0 && match.End == run.Text.Length;
            var text = !run.IsUrl ? value.Address
                : wholeValue ? value.Url ?? value.Address
                : Uri.EscapeDataString(value.Address);

            var encoded = EscapeAttribute(text);

            // An unquoted value (alt=Name) ends at whitespace, so spaces must be references.
            if (!run.IsQuoted)
                encoded = encoded.Replace(" ", "&#32;").Replace("\t", "&#9;").Replace("\r", "&#13;").Replace("\n", "&#10;").Replace("=", "&#61;").Replace("`", "&#96;");

            return encoded;
        }

        private void Parse()
        {
            var html = _source;
            var i = 0;
            var text = new Run();
            var linkDepth = 0;

            while (i < html.Length)
            {
                var c = html[i];

                if (c == '<' && i + 1 < html.Length)
                {
                    var next = html[i + 1];

                    if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                    {
                        FlushText(ref text);
                        text.InLinkNow = linkDepth > 0; // Word puts conditional comments inside links.
                        var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = close < 0 ? html.Length : close + 3;
                        continue;
                    }

                    if (char.IsLetter(next) || next == '/' || next == '!' || next == '?')
                    {
                        var tagEnd = FindTagEnd(html, i);
                        var name = TagName(html, i);

                        if (!InlineElements.Contains(name)) FlushText(ref text);

                        if (name.Equals("a", StringComparison.OrdinalIgnoreCase))
                        {
                            if (next == '/') linkDepth = Math.Max(0, linkDepth - 1);
                            else if (html[tagEnd - 2] != '/') linkDepth++;
                        }

                        text.InLinkNow = linkDepth > 0;

                        if (next != '/' && next != '!' && next != '?')
                            ScanAttributes(i, tagEnd, name);

                        i = tagEnd;

                        if (next != '/' && RawTextElements.Contains(name))
                        {
                            var closeTag = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                            i = closeTag < 0 ? html.Length : closeTag;
                        }

                        continue;
                    }
                }

                if (c == '&')
                {
                    i = AppendEntity(html, i, html.Length, text);
                    continue;
                }

                text.Add(c == ' ' ? ' ' : c, i, i + 1);
                i++;
            }

            FlushText(ref text);
        }

        private void FlushText(ref Run run)
        {
            if (run.Text.Length > 0) _runs.Add(run);
            run = new Run();
        }

        /// <summary>Index just past the closing '&gt;', honouring quoted attribute values.</summary>
        private static int FindTagEnd(string html, int start)
        {
            char quote = '\0';

            for (var i = start + 1; i < html.Length; i++)
            {
                var c = html[i];

                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    // Only quote characters that open an attribute value count.
                    var before = i - 1;
                    while (before > start && char.IsWhiteSpace(html[before])) before--;
                    if (html[before] == '=') quote = c;
                }
                else if (c == '>')
                {
                    return i + 1;
                }
            }

            return html.Length;
        }

        private static string TagName(string html, int start)
        {
            var i = start + 1;
            if (i < html.Length && html[i] == '/') i++;

            var begin = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/')
                i++;

            return html.Substring(begin, i - begin);
        }

        private void ScanAttributes(int tagStart, int tagEnd, string name)
        {
            var bodyStart = tagStart + 1 + name.Length;
            if (bodyStart >= tagEnd) return;

            var body = _source.Substring(bodyStart, tagEnd - bodyStart);

            foreach (Match attribute in AttributePattern.Matches(body))
            {
                var value = attribute.Groups["value"];
                if (!value.Success || !ScannedAttributes.Contains(attribute.Groups["name"].Value)) continue;

                var isUrl = attribute.Groups["name"].Value.Equals("href", StringComparison.OrdinalIgnoreCase)
                    || attribute.Groups["name"].Value.Equals("src", StringComparison.OrdinalIgnoreCase);

                var quoted = value.Index > 0 && (body[value.Index - 1] == '"' || body[value.Index - 1] == '\'');
                var run = new Run { IsAttribute = true, IsUrl = isUrl, IsQuoted = quoted };
                var start = bodyStart + value.Index;
                var end = start + value.Length;
                var i = start;

                while (i < end)
                {
                    if (_source[i] == '&')
                    {
                        i = AppendEntity(_source, i, end, run);
                    }
                    else if (isUrl && _source[i] == '%' && i + 2 < end && IsHex(_source[i + 1]) && IsHex(_source[i + 2]))
                    {
                        // %7B%7BName%7D%7D: editors percent-encode braces inside links.
                        var decoded = (char)Convert.ToInt32(_source.Substring(i + 1, 2), 16);
                        run.Add(decoded, i, i + 3);
                        i += 3;
                    }
                    else
                    {
                        run.Add(_source[i], i, i + 1);
                        i++;
                    }
                }

                if (run.Text.Length > 0) _runs.Add(run);
            }
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>Decodes one character reference, or treats the ampersand as a literal.</summary>
        private static int AppendEntity(string html, int start, int limit, Run run)
        {
            var semicolon = html.IndexOf(';', start, Math.Min(limit - start, 34));

            if (semicolon > start + 1)
            {
                var entity = html.Substring(start, semicolon - start + 1);
                var decoded = WebUtility.HtmlDecode(entity);

                if (decoded != entity && decoded.Length == 1)
                {
                    run.Add(decoded[0] == ' ' ? ' ' : decoded[0], start, semicolon + 1);
                    return semicolon + 1;
                }
            }

            run.Add('&', start, start + 1);
            return start + 1;
        }
    }
}
