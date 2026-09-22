using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OftScrubber.Core.Templating
{
    /// <summary>What kind of value a variable holds; decides the editor, validation and formatting.</summary>
    public enum VariableType
    {
        Text,
        Date,
        Time,
        DateTime,
        Number,
        Link,
        Email,
    }

    /// <summary>One way to write a value, e.g. "21 Sep 2026" or "September 21, 2026".</summary>
    public sealed class ValueFormat
    {
        internal ValueFormat(string id, string pattern, bool upper = false, string? label = null)
        {
            Id = id;
            Pattern = pattern;
            Upper = upper;
            Label = label;
        }

        public string Id { get; }

        /// <summary>A .NET date/number pattern, or empty for types that are not pattern-based.</summary>
        public string Pattern { get; }

        /// <summary>Uppercase the result (21SEP26).</summary>
        public bool Upper { get; }

        /// <summary>Fixed label; null means the UI labels the format with an example value.</summary>
        public string? Label { get; }
    }

    /// <summary>A variable's value as each part of the message needs it.</summary>
    public sealed class FormattedValue
    {
        public FormattedValue(string text, string? url = null, bool linked = false, string? plainText = null, string? address = null)
        {
            Text = text;
            Url = url;
            Linked = linked;
            PlainText = plainText ?? text;
            Address = address ?? text;
        }

        /// <summary>HTML text: for a labelled link, the label.</summary>
        public string Text { get; }

        /// <summary>
        /// Subject, recipients, text attributes and data inside URLs: the value itself, never a
        /// link label, so an address field always receives an address.
        /// </summary>
        public string Address { get; }

        /// <summary>The plain-text body, where a link cannot hide its address behind a label.</summary>
        public string PlainText { get; }

        /// <summary>The target a token takes when it is a whole href/src, e.g. mailto:ops@example.invalid.</summary>
        public string? Url { get; }

        /// <summary>HTML text outside an existing link becomes a clickable link to Url.</summary>
        public bool Linked { get; }
    }

    /// <summary>
    /// Parses what the user typed for a variable and writes it in the chosen format. Output uses
    /// invariant (English) month and day names so a draft reads the same on every machine; input
    /// accepts the current culture as well.
    /// </summary>
    public static class VariableFormatter
    {
        public const string LinkFormat = "link";
        public const string TextFormat = "text";

        private static readonly CultureInfo Output = CultureInfo.InvariantCulture;

        private static readonly Dictionary<VariableType, ValueFormat[]> Formats = new Dictionary<VariableType, ValueFormat[]>
        {
            [VariableType.Text] = new[]
            {
                new ValueFormat("as-entered", "", label: "As entered"),
                new ValueFormat("upper", "", upper: true, label: "UPPERCASE"),
            },
            [VariableType.Date] = new[]
            {
                new ValueFormat("d MMM yyyy", "d MMM yyyy"),
                new ValueFormat("MMMM d, yyyy", "MMMM d, yyyy"),
                new ValueFormat("dddd, MMMM d, yyyy", "dddd, MMMM d, yyyy"),
                new ValueFormat("dddd d MMMM yyyy", "dddd d MMMM yyyy"),
                new ValueFormat("MM/dd/yyyy", "MM/dd/yyyy"),
                new ValueFormat("dd/MM/yyyy", "dd/MM/yyyy"),
                new ValueFormat("yyyy-MM-dd", "yyyy-MM-dd"),
                new ValueFormat("ddMMMyy", "ddMMMyy", upper: true),
            },
            [VariableType.Time] = new[]
            {
                new ValueFormat("HH:mm", "HH:mm"),
                new ValueFormat("HHmm", "HHmm"),
                new ValueFormat("h:mm tt", "h:mm tt"),
            },
            [VariableType.DateTime] = new[]
            {
                new ValueFormat("d MMM yyyy HH:mm", "d MMM yyyy HH:mm"),
                new ValueFormat("MMMM d, yyyy h:mm tt", "MMMM d, yyyy h:mm tt"),
                new ValueFormat("dddd, MMMM d, yyyy 'at' h:mm tt", "dddd, MMMM d, yyyy 'at' h:mm tt"),
                new ValueFormat("MM/dd/yyyy HH:mm", "MM/dd/yyyy HH:mm"),
                new ValueFormat("yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm"),
                new ValueFormat("ddMMMyy HHmm", "ddMMMyy HHmm", upper: true),
            },
            [VariableType.Number] = new[]
            {
                new ValueFormat("as-entered", "", label: "As entered"),
                new ValueFormat("grouped", "#,0.##########", label: "With thousands separators"),
                new ValueFormat("plain", "0.##########", label: "Without separators"),
            },
            [VariableType.Link] = new[]
            {
                new ValueFormat(LinkFormat, "", label: "Clickable link"),
                new ValueFormat(TextFormat, "", label: "Address as text"),
            },
            [VariableType.Email] = new[]
            {
                new ValueFormat(LinkFormat, "", label: "Clickable email link"),
                new ValueFormat(TextFormat, "", label: "Address as text"),
            },
        };

        // Accepted date input besides the current culture's own forms.
        private static readonly string[] DateInputs =
        {
            "yyyy-MM-dd", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy",
            "dddd, MMMM d, yyyy", "dddd d MMMM yyyy", "ddMMMyy", "ddMMMyyyy", "d-MMM-yyyy", "d-MMM-yy", "yyyyMMdd",
        };

        private static readonly Regex TimePattern = new Regex(
            @"^(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?(?:[:.](?<s>\d{2}))?\s*(?<ap>[ap])?\.?\s*(?:m\.?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CompactTimePattern = new Regex(
            @"^(?<h>\d{1,2})(?<m>\d{2})\s*(?<ap>[ap])?\.?\s*(?:m\.?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IReadOnlyList<ValueFormat> FormatsFor(VariableType type) => Formats[type];

        public static ValueFormat FindFormat(VariableType type, string id)
        {
            return Formats[type].FirstOrDefault(f => f.Id == id) ?? Formats[type][0];
        }

        public static string Describe(VariableType type)
        {
            switch (type)
            {
                case VariableType.DateTime: return "Date and time";
                case VariableType.Link: return "Web link";
                default: return type.ToString();
            }
        }

        /// <summary>Menu label for a format: its fixed label, or the format applied to a sample value.</summary>
        public static string Label(VariableType type, ValueFormat format)
        {
            if (format.Label != null) return format.Label;

            var sample = new DateTime(2026, 9, 21, 14, 30, 0);
            var text = sample.ToString(format.Pattern, Output);
            return format.Upper ? text.ToUpperInvariant() : text;
        }

        /// <summary>A starting type from the variable's name: "Arrival Date" is a date, "Portal URL" a link.</summary>
        public static VariableType Guess(string name)
        {
            var words = new HashSet<string>(Words(name), StringComparer.OrdinalIgnoreCase);

            if (words.Contains("datetime") || words.Contains("timestamp") || (words.Contains("date") && words.Contains("time")))
                return VariableType.DateTime;
            if (words.Contains("date")) return VariableType.Date;
            if (words.Contains("time")) return VariableType.Time;
            if (words.Contains("email") || words.Contains("e-mail")) return VariableType.Email;
            if (words.Contains("link") || words.Contains("url") || words.Contains("website") || words.Contains("href"))
                return VariableType.Link;

            return VariableType.Text;
        }

        // Splits on separators and camel case: "ArrivalDate" -> Arrival, Date; "E-mail" stays one word.
        private static IEnumerable<string> Words(string name)
        {
            var spaced = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ");
            spaced = Regex.Replace(spaced, @"(?i)\be-mail\b", "email");
            return spaced.Split(new[] { ' ', '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// The value to substitute, or null when there is none. problem explains a value that was
        /// entered but cannot be used; it is null when the field is simply empty.
        /// </summary>
        public static FormattedValue? Format(TemplateVariable variable, out string? problem)
        {
            problem = null;
            var format = FindFormat(variable.Type, variable.Format);
            var input = variable.Value.Trim();

            switch (variable.Type)
            {
                case VariableType.Text:
                    if (variable.Value.Length == 0) return null;
                    return new FormattedValue(format.Upper ? variable.Value.ToUpperInvariant() : variable.Value);

                case VariableType.Date:
                    if (input.Length == 0) return null;
                    if (!TryParseDate(input, out var date)) { problem = "Not a date. Try 21 Sep 2026, 2026-09-21 or today."; return null; }
                    return Dated(date, format);

                case VariableType.Time:
                    if (input.Length == 0) return null;
                    if (!TryParseTime(input, out var time)) { problem = "Not a time. Try 14:30, 1430 or 2:30 pm."; return null; }
                    return Dated(DateTime.Today.Add(time), format);

                case VariableType.DateTime:
                    var timeInput = variable.Time.Trim();
                    if (input.Length == 0 && timeInput.Length == 0) return null;
                    if (!TryParseDate(input, out var day)) { problem = input.Length == 0 ? "Enter a date." : "Not a date. Try 21 Sep 2026, 2026-09-21 or today."; return null; }
                    if (!TryParseTime(timeInput, out var at)) { problem = timeInput.Length == 0 ? "Enter a time." : "Not a time. Try 14:30, 1430 or 2:30 pm."; return null; }
                    return Dated(day.Date.Add(at), format);

                case VariableType.Number:
                    if (input.Length == 0) return null;
                    if (!TryParseNumber(input, out var number)) { problem = "Not a number."; return null; }
                    return new FormattedValue(format.Pattern.Length == 0 ? input : number.ToString(format.Pattern, Output));

                case VariableType.Link:
                    if (input.Length == 0) return null;
                    if (!TryNormalizeUrl(input, out var url)) { problem = "Not a web address. Use one that starts with https:// or www."; return null; }
                    return Linkable(url, url, variable.LinkText.Trim(), format);

                case VariableType.Email:
                    if (input.Length == 0) return null;
                    if (!IsEmailAddress(input)) { problem = "Not an email address."; return null; }
                    return Linkable(input, "mailto:" + input, variable.LinkText.Trim(), format);

                default:
                    return null;
            }
        }

        private static FormattedValue Dated(DateTime value, ValueFormat format)
        {
            var text = value.ToString(format.Pattern, Output);
            return new FormattedValue(format.Upper ? text.ToUpperInvariant() : text);
        }

        private static FormattedValue Linkable(string address, string url, string label, ValueFormat format)
        {
            if (format.Id != LinkFormat) return new FormattedValue(address, url);

            // A label is shown in HTML; the plain-text body keeps the address readable next to it.
            return label.Length == 0
                ? new FormattedValue(address, url, linked: true)
                : new FormattedValue(label, url, linked: true, plainText: label + " (" + address + ")", address: address);
        }

        public static bool TryParseDate(string text, out DateTime date)
        {
            text = text.Trim();

            switch (text.ToLowerInvariant())
            {
                case "today": date = DateTime.Today; return true;
                case "tomorrow": date = DateTime.Today.AddDays(1); return true;
                case "yesterday": date = DateTime.Today.AddDays(-1); return true;
            }

            const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces;

            if (DateTime.TryParseExact(text, DateInputs, CultureInfo.InvariantCulture, styles, out date)
                || DateTime.TryParseExact(text, DateInputs, CultureInfo.CurrentCulture, styles, out date)
                || DateTime.TryParse(text, CultureInfo.CurrentCulture, styles, out date))
            {
                date = date.Date;
                return true;
            }

            return false;
        }

        /// <summary>Accepts 14:30, 1430, 930, 2:30 pm, 2pm, 14.30 and "now".</summary>
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            text = text.Trim();

            if (text.Equals("now", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.Now;
                time = new TimeSpan(now.Hour, now.Minute, 0);
                return true;
            }

            var match = TimePattern.Match(text);
            if (!match.Success) match = CompactTimePattern.Match(text);
            if (!match.Success) return false;

            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            var seconds = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;

            // A bare "14" is ambiguous with a number typed by mistake; require minutes or am/pm.
            if (!match.Groups["m"].Success && !match.Groups["ap"].Success) return false;

            if (match.Groups["ap"].Success)
            {
                if (hours < 1 || hours > 12) return false;
                var pm = char.ToLowerInvariant(match.Groups["ap"].Value[0]) == 'p';
                hours = hours % 12 + (pm ? 12 : 0);
            }

            if (hours > 23 || minutes > 59 || seconds > 59) return false;

            time = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        public static bool TryParseNumber(string text, out decimal number)
        {
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number)
                || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>
        /// Only http, https and mailto links are produced, so a value can never become a script
        /// or file link. "www.example.com" gains https://.
        /// </summary>
        public static bool TryNormalizeUrl(string text, out string url)
        {
            url = text.Trim();

            if (url.Length == 0 || url.Length > 2048 || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"' || c == '<' || c == '>'))
                return false;

            if (url.IndexOf("://", StringComparison.Ordinal) < 0 && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                if (url.IndexOf('.') <= 0 || url.IndexOf(':') >= 0) return false;
                url = "https://" + url;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)
                && (uri.Scheme == Uri.UriSchemeMailto || uri.Host.Length > 0);
        }

        /// <summary>A plain address (name@domain.tld), no display name, nothing that could break a header.</summary>
        public static bool IsEmailAddress(string text)
        {
            if (text.Length > 254) return false;

            var at = text.IndexOf('@');
            if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1) return false;

            var domain = text.Substring(at + 1);
            if (domain.IndexOf('.') <= 0 || domain.EndsWith(".", StringComparison.Ordinal) || domain.Contains("..")) return false;

            return text.All(c => c > ' ' && c < 127 && "()<>[]\\,;:\"".IndexOf(c) < 0);
        }
    }
}
