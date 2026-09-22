using System.Collections.Generic;

namespace OftScrubber.Core.Model
{
    public enum RecipientKind
    {
        To = 1,
        Cc = 2,
        Bcc = 3,
    }

    public sealed class EmailRecipient
    {
        public EmailRecipient(RecipientKind kind, string displayName, string address)
        {
            Kind = kind;
            DisplayName = displayName;
            Address = address;
        }

        public RecipientKind Kind { get; }
        public string DisplayName { get; }

        /// <summary>SMTP address when known; may be an Exchange (X.500) address or empty.</summary>
        public string Address { get; }

        public override string ToString()
        {
            if (DisplayName.Length == 0) return Address;
            if (Address.Length == 0 || Address == DisplayName) return DisplayName;
            return DisplayName + " <" + Address + ">";
        }
    }

    public sealed class EmailAttachment
    {
        public EmailAttachment(string fileName, string mimeType, string contentId, byte[] data, bool isHidden)
        {
            FileName = fileName;
            MimeType = mimeType;
            ContentId = contentId;
            Data = data;
            IsHidden = isHidden;
        }

        public string FileName { get; }
        public string MimeType { get; }

        /// <summary>Content-ID without angle brackets; empty when the attachment has none.</summary>
        public string ContentId { get; }

        public byte[] Data { get; }
        public bool IsHidden { get; }

        /// <summary>
        /// Set by the reader when the HTML body refers to this attachment through cid:. Inline
        /// attachments travel inside multipart/related rather than as regular attachments.
        /// </summary>
        public bool IsInline { get; set; }
    }

    /// <summary>
    /// A message template as read from disk, before any substitution. Parsing is tolerant:
    /// recoverable problems go into Warnings, and anything the app cannot carry through to the
    /// output goes into FidelityNotes so the user is told exactly what was lost.
    /// </summary>
    public sealed class EmailTemplate
    {
        public string SourceName { get; set; } = "";

        /// <summary>True when the file carries the Outlook template CLSID (.oft) rather than a saved message.</summary>
        public bool IsOutlookTemplate { get; set; }

        public string MessageClass { get; set; } = "";
        public string Subject { get; set; } = "";

        /// <summary>Original HTML body, decoded to text but otherwise untouched. Empty when the message has none.</summary>
        public string HtmlBody { get; set; } = "";

        /// <summary>Where HtmlBody came from, e.g. "PR_HTML" or "RTF-encapsulated HTML".</summary>
        public string HtmlSource { get; set; } = "";

        public string PlainBody { get; set; } = "";

        public List<EmailRecipient> Recipients { get; } = new List<EmailRecipient>();
        public List<EmailAttachment> Attachments { get; } = new List<EmailAttachment>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> FidelityNotes { get; } = new List<string>();

        public bool HasHtml => HtmlBody.Length > 0;
    }
}
