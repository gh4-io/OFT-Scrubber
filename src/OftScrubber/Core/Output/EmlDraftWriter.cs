using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OftScrubber.Core.Model;

namespace OftScrubber.Core.Output
{
    /// <summary>
    /// Writes a completed message as MIME (.eml) marked X-Unsent: 1, which Outlook (classic and
    /// new) and most mail apps open as an editable draft. Nothing is sent: the user reviews the
    /// draft and presses Send in their mail app.
    ///
    /// Structure: multipart/mixed (regular attachments) around multipart/related (cid: images)
    /// around multipart/alternative (plain text and HTML). Levels with nothing to add are omitted.
    /// The HTML is written exactly as rendered; it is never the sanitized preview copy.
    /// </summary>
    public static class EmlDraftWriter
    {
        private const string Crlf = "\r\n";

        public static byte[] ToBytes(RenderedEmail email, out List<string> notes)
        {
            notes = new List<string>();
            var mime = new StringBuilder();

            mime.Append("X-Unsent: 1").Append(Crlf);
            var now = DateTimeOffset.Now;
            mime.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture))
                .Append(now.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture).Replace(":", "")).Append(Crlf);
            // Some New Outlook builds refuse to save a draft without a Message-ID.
            mime.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@oft-scrubber.invalid>").Append(Crlf);
            AppendAddressHeader(mime, "To", email.Recipients.Where(r => r.Kind == RecipientKind.To), notes);
            AppendAddressHeader(mime, "Cc", email.Recipients.Where(r => r.Kind == RecipientKind.Cc), notes);
            AppendAddressHeader(mime, "Bcc", email.Recipients.Where(r => r.Kind == RecipientKind.Bcc), notes);
            mime.Append("Subject: ").Append(EncodeHeaderText(email.Subject)).Append(Crlf);
            mime.Append("MIME-Version: 1.0").Append(Crlf);

            var inline = email.HasHtml ? email.Attachments.Where(a => a.IsInline).ToList() : new List<EmailAttachment>();
            var regular = email.Attachments.Where(a => !inline.Contains(a)).ToList();

            AppendMixed(mime, email, inline, regular);
            return Encoding.ASCII.GetBytes(mime.ToString());
        }

        private static void AppendMixed(StringBuilder mime, RenderedEmail email, List<EmailAttachment> inline, List<EmailAttachment> regular)
        {
            if (regular.Count == 0)
            {
                AppendRelated(mime, email, inline);
                return;
            }

            var boundary = NewBoundary("mixed");
            mime.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append('"').Append(Crlf).Append(Crlf);
            mime.Append("--").Append(boundary).Append(Crlf);
            AppendRelated(mime, email, inline);

            foreach (var attachment in regular)
            {
                mime.Append(Crlf).Append("--").Append(boundary).Append(Crlf);
                AppendAttachment(mime, attachment, "attachment");
            }

            mime.Append(Crlf).Append("--").Append(boundary).Append("--").Append(Crlf);
        }

        private static void AppendRelated(StringBuilder mime, RenderedEmail email, List<EmailAttachment> inline)
        {
            if (inline.Count == 0)
            {
                AppendAlternative(mime, email);
                return;
            }

            var boundary = NewBoundary("related");
            mime.Append("Content-Type: multipart/related; type=\"text/html\"; boundary=\"").Append(boundary).Append('"').Append(Crlf).Append(Crlf);
            mime.Append("--").Append(boundary).Append(Crlf);
            AppendAlternative(mime, email);

            foreach (var attachment in inline)
            {
                mime.Append(Crlf).Append("--").Append(boundary).Append(Crlf);
                AppendAttachment(mime, attachment, "inline");
            }

            mime.Append(Crlf).Append("--").Append(boundary).Append("--").Append(Crlf);
        }

        private static void AppendAlternative(StringBuilder mime, RenderedEmail email)
        {
            var hasPlain = email.PlainBody.Length > 0;

            if (!email.HasHtml)
            {
                AppendText(mime, "text/plain", email.PlainBody, Encoding.UTF8);
                return;
            }

            if (!hasPlain)
            {
                AppendHtml(mime, email.HtmlBody);
                return;
            }

            var boundary = NewBoundary("alt");
            mime.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append('"').Append(Crlf).Append(Crlf);
            mime.Append("--").Append(boundary).Append(Crlf);
            AppendText(mime, "text/plain", email.PlainBody, Encoding.UTF8);
            mime.Append(Crlf).Append("--").Append(boundary).Append(Crlf);
            AppendHtml(mime, email.HtmlBody);
            mime.Append(Crlf).Append("--").Append(boundary).Append("--").Append(Crlf);
        }

        /// <summary>
        /// Keeps the charset the HTML declares, so its meta tag stays truthful and the markup is
        /// not touched. Characters that charset cannot hold (typically typed values) are written
        /// as numeric character references, which every HTML reader decodes.
        /// </summary>
        private static void AppendHtml(StringBuilder mime, string html)
        {
            var encoding = Encoding.UTF8;
            var declared = Regex.Match(html, @"<meta[^>]+charset\s*=\s*[""']?(?<cs>[A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase);

            if (declared.Success)
            {
                try
                {
                    encoding = Encoding.GetEncoding(declared.Groups["cs"].Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    html = ReferenceUnencodable(html, encoding);
                }
                catch (ArgumentException)
                {
                    encoding = Encoding.UTF8; // Unknown charset name.
                }
            }

            AppendText(mime, "text/html", html, encoding);
        }

        private static string ReferenceUnencodable(string html, Encoding encoding)
        {
            var output = new StringBuilder(html.Length);

            for (var i = 0; i < html.Length; i++)
            {
                var length = char.IsHighSurrogate(html[i]) && i + 1 < html.Length && char.IsLowSurrogate(html[i + 1]) ? 2 : 1;
                var element = html.Substring(i, length);

                try
                {
                    encoding.GetByteCount(element);
                    output.Append(element);
                }
                catch (EncoderFallbackException)
                {
                    output.Append("&#").Append(char.ConvertToUtf32(element, 0)).Append(';');
                }

                i += length - 1;
            }

            return output.ToString();
        }

        private static void AppendText(StringBuilder mime, string type, string text, Encoding encoding)
        {
            mime.Append("Content-Type: ").Append(type).Append("; charset=\"").Append(encoding.WebName).Append('"').Append(Crlf);
            mime.Append("Content-Transfer-Encoding: base64").Append(Crlf).Append(Crlf);
            AppendBase64(mime, encoding.GetBytes(text));
        }

        private static void AppendAttachment(StringBuilder mime, EmailAttachment attachment, string disposition)
        {
            // The MIME tag comes from the file: accept a bare type/subtype token and nothing else.
            var type = MimeTypeToken.IsMatch(attachment.MimeType.Trim()) ? attachment.MimeType.Trim() : "application/octet-stream";

            mime.Append("Content-Type: ").Append(type).Append(';').Append(FileNameParameter("name", attachment.FileName)).Append(Crlf);
            mime.Append("Content-Disposition: ").Append(disposition).Append(';').Append(FileNameParameter("filename", attachment.FileName)).Append(Crlf);

            if (attachment.ContentId.Length > 0)
                mime.Append("Content-ID: <").Append(AsciiHeaderValue(attachment.ContentId)).Append('>').Append(Crlf);

            mime.Append("Content-Transfer-Encoding: base64").Append(Crlf).Append(Crlf);
            AppendBase64(mime, attachment.Data);
        }

        private static readonly Regex MimeTypeToken = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]*/[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// ASCII names as a quoted string; anything else as an RFC 2231 extended parameter
        /// (name*=utf-8''...), never as encoded words inside quotes.
        /// </summary>
        private static string FileNameParameter(string parameter, string fileName)
        {
            var name = SafeHeaderValue(fileName);

            if (IsPlainAscii(name))
                return " " + parameter + "=\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            var encoded = new StringBuilder();

            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                var c = (char)b;
                var unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
                if (unreserved) encoded.Append(c);
                else encoded.Append('%').Append(b.ToString("X2"));
            }

            return Crlf + " " + parameter + "*=utf-8''" + encoded;
        }

        /// <summary>For values that must be ASCII (addresses, Content-IDs): anything else is dropped, never turned into '?'.</summary>
        private static string AsciiHeaderValue(string text)
        {
            return new string(SafeHeaderValue(text).Where(c => c > 0x20 && c < 0x7F).ToArray());
        }

        private static void AppendAddressHeader(StringBuilder mime, string header, IEnumerable<EmailRecipient> recipients, List<string> notes)
        {
            var parts = new List<string>();

            foreach (var recipient in recipients)
            {
                var address = recipient.Address.Trim();

                if (address.IndexOf('@') < 1 || AsciiHeaderValue(address) != address)
                {
                    notes.Add(header + " recipient without a plain SMTP address was left out; add it in the mail app.");
                    continue;
                }

                var display = recipient.DisplayName.Trim();
                parts.Add(display.Length == 0 || display == address
                    ? "<" + address + ">"
                    : EncodeDisplayName(display) + " <" + address + ">");
            }

            if (parts.Count > 0)
                mime.Append(header).Append(": ").Append(string.Join("," + Crlf + " ", parts)).Append(Crlf);
        }

        private static string EncodeDisplayName(string display)
        {
            if (IsPlainAscii(display) && !Regex.IsMatch(display, @"[()<>\[\]:;@\\,."" ]"))
                return display;
            if (IsPlainAscii(display))
                return "\"" + display.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return EncodeHeaderText(display);
        }

        /// <summary>RFC 2047 encoded words for non-ASCII text, split so no line grows too long.</summary>
        private static string EncodeHeaderText(string text)
        {
            text = SafeHeaderValue(text);
            if (IsPlainAscii(text)) return text;

            var words = new List<string>();
            var chunk = new StringBuilder();

            foreach (var element in SplitTextElements(text))
            {
                if (Encoding.UTF8.GetByteCount(chunk.ToString() + element) > 45)
                {
                    words.Add(EncodedWord(chunk.ToString()));
                    chunk.Clear();
                }

                chunk.Append(element);
            }

            if (chunk.Length > 0) words.Add(EncodedWord(chunk.ToString()));
            return string.Join(Crlf + " ", words);
        }

        private static IEnumerable<string> SplitTextElements(string text)
        {
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext()) yield return (string)enumerator.Current;
        }

        private static string EncodedWord(string text)
        {
            return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
        }

        private static bool IsPlainAscii(string text)
        {
            return text.All(c => c >= 0x20 && c < 0x7F);
        }

        /// <summary>Header values must never carry line breaks: that would inject headers.</summary>
        private static string SafeHeaderValue(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        private static void AppendBase64(StringBuilder mime, byte[] data)
        {
            var encoded = Convert.ToBase64String(data);

            for (var i = 0; i < encoded.Length; i += 76)
                mime.Append(encoded, i, Math.Min(76, encoded.Length - i)).Append(Crlf);
        }

        private static string NewBoundary(string kind)
        {
            return "=_oft_" + kind + "_" + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>Saves the completed message as an .eml draft, optionally opening it in the default mail app.</summary>
    public sealed class EmlDraftOutput : IEmailOutput
    {
        private readonly string _path;
        private readonly bool _openAfterSave;

        public EmlDraftOutput(string path, bool openAfterSave)
        {
            _path = path;
            _openAfterSave = openAfterSave;
        }

        public string DisplayName => "Draft file (.eml)";

        public OutputResult Create(RenderedEmail email)
        {
            var bytes = EmlDraftWriter.ToBytes(email, out var notes);
            File.WriteAllBytes(_path, bytes);

            if (_openAfterSave)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true });

            return new OutputResult(_path, notes);
        }
    }
}
