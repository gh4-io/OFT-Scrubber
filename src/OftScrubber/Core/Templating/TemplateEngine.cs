using System.Collections.Generic;
using System.Linq;
using System.Text;
using OftScrubber.Core.Model;

namespace OftScrubber.Core.Templating
{
    /// <summary>
    /// Finds variables in a template and renders a completed message. Every location is scanned
    /// and replaced by the same rules, so the preview, the output and the variable list agree.
    /// </summary>
    public sealed class TemplateEngine
    {
        public TemplateEngine(TokenSyntaxRegistry registry)
        {
            Registry = registry;
        }

        public TokenSyntaxRegistry Registry { get; }

        public VariableSet Scan(EmailTemplate template)
        {
            var set = new VariableSet();

            ScanPlain(set, TemplateLocation.Subject, template.Subject);

            if (template.HasHtml)
            {
                var html = new HtmlText(template.HtmlBody);

                foreach (var found in html.FindTokens(Registry))
                {
                    var location = found.InAttribute ? TemplateLocation.HtmlLink : TemplateLocation.HtmlBody;
                    set.Add(found.Match.Name, new TokenOccurrence(location, found.Match.RawText, found.Match.Syntax));
                }

                set.StrayOpeners += html.CountStrayOpeners(Registry);
            }

            ScanPlain(set, TemplateLocation.PlainBody, template.PlainBody);

            foreach (var recipient in template.Recipients)
            {
                var location = LocationOf(recipient.Kind);
                ScanPlain(set, location, recipient.DisplayName);
                ScanPlain(set, location, recipient.Address);
            }

            return set;
        }

        private void ScanPlain(VariableSet set, TemplateLocation location, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var matches = Registry.Find(text);

            foreach (var match in matches)
                set.Add(match.Name, new TokenOccurrence(location, match.RawText, match.Syntax));

            set.StrayOpeners += Registry.CountStrayOpeners(text, matches);
        }

        /// <summary>
        /// Replaces every token whose variable has a value. Unfilled tokens stay exactly as
        /// written, so an incomplete message is obvious rather than silently blank.
        /// </summary>
        public RenderedEmail Render(EmailTemplate template, VariableSet variables)
        {
            // Formatted once per render: every token of a variable gets the same text.
            var values = variables.Variables.ToDictionary(v => v.Key, v => v.Formatted);

            FormattedValue? ValueFor(TokenMatch match)
            {
                return values.TryGetValue(TokenSyntaxRegistry.CanonicalKey(match.Name), out var value) ? value : null;
            }

            string? TextFor(TokenMatch match) => ValueFor(match)?.Address;
            string? PlainTextFor(TokenMatch match) => ValueFor(match)?.PlainText;

            var rendered = new RenderedEmail(template)
            {
                Subject = ReplacePlain(template.Subject, TextFor, singleLine: true),
                HtmlBody = template.HasHtml ? new HtmlText(template.HtmlBody).Replace(Registry, ValueFor) : "",
                PlainBody = ReplacePlain(template.PlainBody, PlainTextFor, singleLine: false),
            };

            foreach (var recipient in template.Recipients)
            {
                rendered.Recipients.Add(new EmailRecipient(recipient.Kind,
                    ReplacePlain(recipient.DisplayName, TextFor, singleLine: true),
                    ReplacePlain(recipient.Address, TextFor, singleLine: true)));
            }

            rendered.UnresolvedVariables.AddRange(variables.Variables.Where(v => !v.IsFilled).Select(v => v.Name));
            return rendered;
        }

        private string ReplacePlain(string text, System.Func<TokenMatch, string?> valueFor, bool singleLine)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            var output = new StringBuilder(text.Length);
            var position = 0;

            foreach (var match in Registry.Find(text))
            {
                var value = valueFor(match);
                if (value == null) continue;

                // Subjects and addresses are header values: a line break would corrupt them.
                if (singleLine) value = value.Replace("\r", " ").Replace("\n", " ");

                output.Append(text, position, match.Start - position);
                output.Append(value);
                position = match.End;
            }

            output.Append(text, position, text.Length - position);
            return output.ToString();
        }

        private static TemplateLocation LocationOf(RecipientKind kind)
        {
            if (kind == RecipientKind.Cc) return TemplateLocation.Cc;
            if (kind == RecipientKind.Bcc) return TemplateLocation.Bcc;
            return TemplateLocation.To;
        }
    }
}
