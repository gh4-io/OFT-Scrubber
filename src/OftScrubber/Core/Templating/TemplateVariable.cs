using System;
using System.Collections.Generic;
using System.Linq;

namespace OftScrubber.Core.Templating
{
    public enum TemplateLocation
    {
        Subject,
        HtmlBody,
        HtmlLink,
        PlainBody,
        To,
        Cc,
        Bcc,
    }

    /// <summary>One place a token appears.</summary>
    public sealed class TokenOccurrence
    {
        public TokenOccurrence(TemplateLocation location, string rawText, TokenSyntax syntax)
        {
            Location = location;
            RawText = rawText;
            Syntax = syntax;
        }

        public TemplateLocation Location { get; }

        /// <summary>The token exactly as written (after HTML decoding), e.g. "{{Tail Number}}".</summary>
        public string RawText { get; }

        public TokenSyntax Syntax { get; }
    }

    /// <summary>
    /// A logical variable: every token with the same name, whatever its syntax or location,
    /// shares one value. Names compare case-insensitively with whitespace collapsed.
    ///
    /// Deliberately independent of the UI. Later metadata (default value, required) belongs
    /// here as new properties.
    /// </summary>
    public sealed class TemplateVariable
    {
        public TemplateVariable(string name)
        {
            Name = name;
            Key = TokenSyntaxRegistry.CanonicalKey(name);
        }

        /// <summary>Display name: the spelling of the first occurrence.</summary>
        public string Name { get; }

        /// <summary>Canonical identity used for matching.</summary>
        public string Key { get; }

        public List<TokenOccurrence> Occurrences { get; } = new List<TokenOccurrence>();

        private VariableType _type;

        /// <summary>Decides how Value is read and written. Changing it resets an unsuitable Format.</summary>
        public VariableType Type
        {
            get => _type;
            set
            {
                _type = value;
                Format = VariableFormatter.FindFormat(value, Format).Id;
            }
        }

        /// <summary>Id of a <see cref="ValueFormat"/> for the current type.</summary>
        public string Format { get; set; } = VariableFormatter.FormatsFor(VariableType.Text)[0].Id;

        /// <summary>What the user typed: text, a date, a time, a number, a web or email address.</summary>
        public string Value { get; set; } = "";

        /// <summary>The time half of a date-and-time value.</summary>
        public string Time { get; set; } = "";

        /// <summary>Optional text shown for a clickable link instead of its address.</summary>
        public string LinkText { get; set; } = "";

        /// <summary>The value to substitute, or null when it is empty or cannot be read.</summary>
        public FormattedValue? Formatted => VariableFormatter.Format(this, out _);

        /// <summary>Why an entered value cannot be used; null when it is fine or empty.</summary>
        public string? Problem
        {
            get
            {
                VariableFormatter.Format(this, out var problem);
                return problem;
            }
        }

        public bool IsFilled => Formatted != null;

        public int Count => Occurrences.Count;

        public IEnumerable<TokenSyntax> Syntaxes => Occurrences.Select(o => o.Syntax).Distinct();

        public IEnumerable<TemplateLocation> Locations => Occurrences.Select(o => o.Location).Distinct();

        /// <summary>Distinct spellings, so the UI can show when {{a}} and [[A]] were merged.</summary>
        public IEnumerable<string> Spellings => Occurrences.Select(o => o.RawText).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Variables in first-seen order, plus scan diagnostics.</summary>
    public sealed class VariableSet
    {
        private readonly Dictionary<string, TemplateVariable> _byKey = new Dictionary<string, TemplateVariable>(StringComparer.Ordinal);

        public List<TemplateVariable> Variables { get; } = new List<TemplateVariable>();

        /// <summary>Opening delimiters with no matching token: likely typos, reported to the user.</summary>
        public int StrayOpeners { get; set; }

        public void Add(string name, TokenOccurrence occurrence)
        {
            var key = TokenSyntaxRegistry.CanonicalKey(name);

            if (!_byKey.TryGetValue(key, out var variable))
            {
                variable = new TemplateVariable(name) { Type = VariableFormatter.Guess(name) };
                _byKey.Add(key, variable);
                Variables.Add(variable);
            }

            variable.Occurrences.Add(occurrence);
        }

        public TemplateVariable? Find(string name)
        {
            return _byKey.TryGetValue(TokenSyntaxRegistry.CanonicalKey(name), out var variable) ? variable : null;
        }

        public int FilledCount => Variables.Count(v => v.IsFilled);
    }
}
