using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OftScrubber.Core.Templating
{
    /// <summary>
    /// One delimiter style, such as {{Name}}. Each syntax is a small regular expression over
    /// logical text (HTML already decoded), so adding a style never touches the HTML handling.
    /// </summary>
    public sealed class TokenSyntax
    {
        // Letters, digits and underscores, with single spaces, dots or hyphens between words.
        // Deliberately narrow: CSS, JSON and prose rarely produce a delimited identifier.
        private const string SpacedName = @"[A-Za-z_][A-Za-z0-9_]*(?:[ .\-]+[A-Za-z0-9_]+){0,7}";
        private const string IdentifierName = @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+){0,7}";

        public TokenSyntax(string id, string example, string pattern, bool enabledByDefault, string note)
        {
            Id = id;
            Example = example;
            Pattern = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
            EnabledByDefault = enabledByDefault;
            Note = note;
        }

        public string Id { get; }

        /// <summary>How the syntax looks, for the UI, e.g. "{{Name}}".</summary>
        public string Example { get; }

        /// <summary>Must define a group called "name".</summary>
        public Regex Pattern { get; }

        public bool EnabledByDefault { get; }
        public string Note { get; }

        public static readonly TokenSyntax DoubleBrace = new TokenSyntax(
            "double-brace", "{{Name}}",
            @"(?<!\{)\{\{[ ]*(?<name>" + SpacedName + @")[ ]*\}\}(?!\})", true,
            "Most common template style.");

        public static readonly TokenSyntax DoubleBracket = new TokenSyntax(
            "double-bracket", "[[Name]]",
            @"(?<!\[)\[\[[ ]*(?<name>" + SpacedName + @")[ ]*\]\](?!\])", true,
            "Rare in ordinary email text.");

        public static readonly TokenSyntax DollarBrace = new TokenSyntax(
            "dollar-brace", "${Name}",
            @"\$\{[ ]*(?<name>" + IdentifierName + @")[ ]*\}", true,
            "Identifier names only.");

        // Matched on decoded text, so &lt;&lt;Name&gt;&gt; in HTML is found. Word may
        // autocorrect << >> into guillemets, which are accepted too.
        public static readonly TokenSyntax DoubleAngle = new TokenSyntax(
            "double-angle", "<<Name>>",
            @"(?:(?<!<)<<[ ]*(?<name>" + SpacedName + @")[ ]*>>(?!>)|«[ ]*(?<name>" + SpacedName + @")[ ]*»)", true,
            "Also accepts «Name», which Word produces by autocorrect.");

        public static readonly TokenSyntax DoublePercent = new TokenSyntax(
            "double-percent", "%%Name%%",
            @"(?<!%)%%(?<name>" + IdentifierName + @")%%(?!%)", true,
            "Identifier names only.");

        // Off by default: "50%off 20%" style text and URL escapes make single percent risky.
        public static readonly TokenSyntax SinglePercent = new TokenSyntax(
            "single-percent", "%Name%",
            @"(?<![%\w])%(?<name>[A-Za-z_][A-Za-z0-9_]*)%(?![%\w])", false,
            "Off by default: prone to false positives around percentages.");
    }

    /// <summary>A token found in logical text.</summary>
    public sealed class TokenMatch
    {
        public TokenMatch(int start, int length, string rawText, string name, TokenSyntax syntax)
        {
            Start = start;
            Length = length;
            RawText = rawText;
            Name = name;
            Syntax = syntax;
        }

        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;

        /// <summary>The token as written, after HTML decoding, e.g. "{{ Tail Number }}".</summary>
        public string RawText { get; }

        /// <summary>The name inside the delimiters with whitespace collapsed.</summary>
        public string Name { get; }

        public TokenSyntax Syntax { get; }
    }

    /// <summary>
    /// The set of syntaxes in use. A plain ordered list of strategies: add a TokenSyntax to
    /// support another delimiter style. Order breaks ties when two matches start together.
    /// </summary>
    public sealed class TokenSyntaxRegistry
    {
        private readonly List<TokenSyntax> _all;
        private readonly HashSet<string> _enabled;

        public TokenSyntaxRegistry(IEnumerable<TokenSyntax> syntaxes)
        {
            _all = syntaxes.ToList();
            _enabled = new HashSet<string>(_all.Where(s => s.EnabledByDefault).Select(s => s.Id), StringComparer.Ordinal);
        }

        public static TokenSyntaxRegistry CreateDefault()
        {
            return new TokenSyntaxRegistry(new[]
            {
                TokenSyntax.DoubleBrace,
                TokenSyntax.DoubleBracket,
                TokenSyntax.DollarBrace,
                TokenSyntax.DoubleAngle,
                TokenSyntax.DoublePercent,
                TokenSyntax.SinglePercent,
            });
        }

        public IReadOnlyList<TokenSyntax> All => _all;

        public bool IsEnabled(TokenSyntax syntax) => _enabled.Contains(syntax.Id);

        public void SetEnabled(TokenSyntax syntax, bool enabled)
        {
            if (enabled) _enabled.Add(syntax.Id);
            else _enabled.Remove(syntax.Id);
        }

        /// <summary>
        /// Finds non-overlapping tokens. When matches overlap, the earliest wins, then the longest,
        /// then the syntax listed first.
        /// </summary>
        public List<TokenMatch> Find(string text)
        {
            var candidates = new List<TokenMatch>();

            for (var order = 0; order < _all.Count; order++)
            {
                var syntax = _all[order];
                if (!IsEnabled(syntax)) continue;

                foreach (Match match in syntax.Pattern.Matches(text))
                    candidates.Add(new TokenMatch(match.Index, match.Length, match.Value,
                        NormalizeName(match.Groups["name"].Value), syntax));
            }

            var ordered = candidates
                .OrderBy(m => m.Start)
                .ThenByDescending(m => m.Length)
                .ThenBy(m => _all.IndexOf(m.Syntax))
                .ToList();

            var result = new List<TokenMatch>();
            var end = 0;

            foreach (var match in ordered)
            {
                if (match.Start < end) continue;
                result.Add(match);
                end = match.End;
            }

            return result;
        }

        /// <summary>Collapses internal whitespace. Identity is case-insensitive; see CanonicalKey.</summary>
        public static string NormalizeName(string name)
        {
            return Regex.Replace(name.Trim(), @"\s+", " ");
        }

        public static string CanonicalKey(string name)
        {
            return NormalizeName(name).ToUpperInvariant();
        }

        /// <summary>
        /// Opening delimiters that are not part of any token: usually a typo or a token Word
        /// split with something other than inline formatting. Reported, never guessed at.
        /// </summary>
        public int CountStrayOpeners(string text, IList<TokenMatch> matches)
        {
            var count = 0;

            foreach (var opener in new[] { "{{", "[[", "${" })
            {
                var index = text.IndexOf(opener, StringComparison.Ordinal);

                while (index >= 0)
                {
                    var position = index;
                    if (!matches.Any(m => position >= m.Start && position < m.End)) count++;
                    index = text.IndexOf(opener, index + opener.Length, StringComparison.Ordinal);
                }
            }

            return count;
        }
    }
}
