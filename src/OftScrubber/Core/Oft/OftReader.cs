using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OftScrubber.Core.Cfb;
using OftScrubber.Core.Model;

namespace OftScrubber.Core.Oft
{
    /// <summary>
    /// Reads an Outlook template (.oft) or saved message (.msg) into an EmailTemplate. Read-only:
    /// nothing in the file is executed, and named properties are ignored.
    /// </summary>
    public static class OftReader
    {
        private static readonly Guid TemplateClsid = new Guid("0006F046-0000-0000-C000-000000000046");
        private static readonly Guid MessageClsid = new Guid("00020D0B-0000-0000-C000-000000000046");

        private const string RecipientPrefix = "__recip_version1.0_#";
        private const string AttachmentPrefix = "__attach_version1.0_#";
        private const int MaxChildren = 2048;
        private const int MaxHtmlLength = 32 * 1024 * 1024;

        private const ushort PrMessageClass = 0x001A;
        private const ushort PrSubject = 0x0037;
        private const ushort PrRecipientType = 0x0C15;
        private const ushort PrBody = 0x1000;
        private const ushort PrRtfCompressed = 0x1009;
        private const ushort PrHtml = 0x1013;
        private const ushort PrDisplayName = 0x3001;
        private const ushort PrAddrType = 0x3002;
        private const ushort PrEmailAddress = 0x3003;
        private const ushort PrSmtpAddress = 0x39FE;
        private const ushort PrAttachData = 0x3701;
        private const ushort PrAttachFilename = 0x3704;
        private const ushort PrAttachMethod = 0x3705;
        private const ushort PrAttachLongFilename = 0x3707;
        private const ushort PrAttachMimeTag = 0x370E;
        private const ushort PrAttachContentId = 0x3712;
        private const ushort PrInternetCpid = 0x3FDE;
        private const ushort PrMessageCodepage = 0x3FFD;
        private const ushort PrAttachmentHidden = 0x7FFE;

        public static EmailTemplate Read(string path)
        {
            byte[] data;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) throw new OftFormatException("The file could not be found: " + path);
                if (info.Length > CompoundFile.MaxFileSize)
                {
                    throw new OftFormatException("This file is too large to be an Outlook template or message (over 200 MB).");
                }
                data = File.ReadAllBytes(path);
            }
            catch (IOException ex)
            {
                throw new OftFormatException("The file could not be opened. Close it in any other program and try again. (" + ex.Message + ")", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new OftFormatException("You do not have permission to open this file.", ex);
            }
            return Read(data, Path.GetFileName(path));
        }

        public static EmailTemplate Read(byte[] data, string sourceName)
        {
            CfbStorage root = CompoundFile.Open(data);
            MapiProperties message = new MapiProperties(root, MapiProperties.TopLevelHeaderSize);

            EmailTemplate template = new EmailTemplate { SourceName = sourceName };
            if (root.Clsid == TemplateClsid) template.IsOutlookTemplate = true;
            else if (root.Clsid != MessageClsid && !message.HasPropertyStream)
            {
                throw new OftFormatException("This file is not an Outlook template or message (it is a different kind of Compound File, such as an old Office document).");
            }

            message.CodePage = message.GetInt32(PrMessageCodepage) ?? message.GetInt32(PrInternetCpid) ?? 1252;

            template.MessageClass = message.GetString(PrMessageClass) ?? "";
            if (!template.MessageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
            {
                string kind = template.MessageClass.Length == 0 ? "has no item type" : "is a '" + template.MessageClass + "' item";
                template.Warnings.Add("This Outlook item " + kind + " rather than an email message (IPM.Note); only its email fields are read.");
            }

            template.Subject = message.GetString(PrSubject) ?? "";
            template.PlainBody = message.GetString(PrBody) ?? "";
            ReadBody(message, template);
            ReadRecipients(root, message.CodePage, template);
            ReadAttachments(root, message.CodePage, template);

            foreach (EmailAttachment attachment in template.Attachments)
            {
                if (attachment.ContentId.Length > 0 &&
                    template.HtmlBody.IndexOf("cid:" + attachment.ContentId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    attachment.IsInline = true;
                }
            }

            // Outlook does not show hidden attachments the body no longer refers to (typically
            // images left behind by an edited signature). Carrying them would send invisible files.
            int orphaned = template.Attachments.RemoveAll(a => a.IsHidden && !a.IsInline);
            if (orphaned > 0)
            {
                template.FidelityNotes.Add(orphaned + " hidden attachment(s) that the body does not use were left out, matching what Outlook shows.");
            }
            return template;
        }

        private static void ReadBody(MapiProperties message, EmailTemplate template)
        {
            byte[]? htmlBytes = message.GetBinary(PrHtml);
            string? html = htmlBytes != null ? DecodeHtml(htmlBytes, message.GetInt32(PrInternetCpid)) : message.GetString(PrHtml);
            if (html != null && html.Length > MaxHtmlLength)
                throw new OftFormatException("The HTML body is larger than 32 million characters, far beyond a real email template.");
            if (!string.IsNullOrEmpty(html))
            {
                template.HtmlBody = html!;
                template.HtmlSource = "PR_HTML";
                return;
            }

            byte[]? compressed = message.GetBinary(PrRtfCompressed);
            if (compressed != null)
            {
                string? fromRtf = null;
                try
                {
                    fromRtf = RtfHtmlExtractor.Extract(RtfCompressed.Decompress(compressed));
                }
                catch (InvalidDataException)
                {
                    template.Warnings.Add("The rich text (RTF) body is damaged and could not be decoded; only the plain-text body is available.");
                    return;
                }

                if (!string.IsNullOrEmpty(fromRtf))
                {
                    template.HtmlBody = fromRtf!;
                    template.HtmlSource = "RTF-encapsulated HTML";
                    return;
                }
                template.FidelityNotes.Add("The body is stored as rich text (RTF) without HTML; only the plain-text body is available. Formatting will be lost.");
                return;
            }

            template.Warnings.Add(template.PlainBody.Length > 0
                ? "The message has no HTML or rich text body; only the plain-text body is available."
                : "The message has no body.");
        }

        /// <summary>Decodes PR_HTML bytes: PR_INTERNET_CPID, then a meta charset, then UTF-8 if valid, then Windows-1252.</summary>
        private static string DecodeHtml(byte[] bytes, int? internetCpid)
        {
            Encoding? encoding = null;
            if (internetCpid.HasValue) encoding = MapiProperties.GetEncoding(internetCpid.Value);

            if (encoding == null)
            {
                string ascii = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 16384));
                Match match = Regex.Match(ascii, "<meta[^>]*charset\\s*=\\s*[\"']?([A-Za-z0-9_.:-]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    try
                    {
                        encoding = Encoding.GetEncoding(match.Groups[1].Value);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            if (encoding == null)
            {
                try
                {
                    return StripBom(new UTF8Encoding(false, true).GetString(bytes));
                }
                catch (DecoderFallbackException)
                {
                    encoding = MapiProperties.GetEncoding(1252);
                }
            }
            return StripBom(encoding.GetString(bytes)).TrimEnd('\0');
        }

        private static string StripBom(string text)
        {
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        private static IEnumerable<CfbStorage> ChildStorages(CfbStorage root, string prefix)
        {
            List<CfbStorage> storages = root.Storages
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value)
                .ToList();

            // Outlook allows far fewer recipients or attachments than this; more means a crafted file.
            if (storages.Count > MaxChildren)
                throw new OftFormatException("This file lists more than " + MaxChildren + " recipients or attachments, so it is not treated as a genuine Outlook item.");
            return storages;
        }

        private static void ReadRecipients(CfbStorage root, int codePage, EmailTemplate template)
        {
            int number = 0;
            foreach (CfbStorage storage in ChildStorages(root, RecipientPrefix))
            {
                number++;
                MapiProperties props = new MapiProperties(storage, MapiProperties.ChildHeaderSize) { CodePage = codePage };
                int type = props.GetInt32(PrRecipientType) ?? 1;
                RecipientKind kind = type == 2 ? RecipientKind.Cc : type == 3 ? RecipientKind.Bcc : RecipientKind.To;

                string displayName = props.GetString(PrDisplayName) ?? "";
                string address = props.GetString(PrSmtpAddress) ?? "";
                if (address.Length == 0)
                {
                    address = props.GetString(PrEmailAddress) ?? "";
                    if (string.Equals(props.GetString(PrAddrType), "EX", StringComparison.OrdinalIgnoreCase))
                    {
                        template.Warnings.Add("Recipient " + number + " uses an Exchange address without an SMTP address.");
                    }
                }
                template.Recipients.Add(new EmailRecipient(kind, displayName, address));
            }
        }

        private static void ReadAttachments(CfbStorage root, int codePage, EmailTemplate template)
        {
            int number = 0;
            foreach (CfbStorage storage in ChildStorages(root, AttachmentPrefix))
            {
                number++;
                MapiProperties props = new MapiProperties(storage, MapiProperties.ChildHeaderSize) { CodePage = codePage };
                int method = props.GetInt32(PrAttachMethod) ?? 1;
                string name = NonEmpty(props.GetString(PrAttachLongFilename))
                    ?? NonEmpty(props.GetString(PrAttachFilename))
                    ?? "attachment-" + number;

                bool embedded = storage.Storages.ContainsKey(MapiProperties.StreamName(PrAttachData, 0x000D));
                if (method == 5 || method == 6 || embedded)
                {
                    template.FidelityNotes.Add("Attachment '" + name + "' is an embedded Outlook item/OLE object and is not carried into the output.");
                    continue;
                }

                byte[]? data = props.GetBinary(PrAttachData);
                if (data == null)
                {
                    if (method >= 2 && method <= 4)
                    {
                        template.FidelityNotes.Add("Attachment '" + name + "' is a link to a file rather than a stored copy and is not carried into the output.");
                    }
                    else
                    {
                        template.Warnings.Add("Attachment " + number + " has no data and was skipped.");
                    }
                    continue;
                }

                string mimeType = NonEmpty(props.GetString(PrAttachMimeTag)) ?? "application/octet-stream";
                string contentId = (props.GetString(PrAttachContentId) ?? "").Trim().TrimStart('<').TrimEnd('>');
                bool hidden = props.GetBoolean(PrAttachmentHidden) ?? false;
                template.Attachments.Add(new EmailAttachment(name, mimeType, contentId, data, hidden));
            }
        }

        private static string? NonEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
