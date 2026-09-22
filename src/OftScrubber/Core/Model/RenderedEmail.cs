using System.Collections.Generic;

namespace OftScrubber.Core.Model
{
    /// <summary>
    /// A completed message ready for an output adapter. Attachments are carried over from the
    /// template unchanged; attachment content is never templated.
    /// </summary>
    public sealed class RenderedEmail
    {
        public RenderedEmail(EmailTemplate template)
        {
            Template = template;
        }

        public EmailTemplate Template { get; }

        public string Subject { get; set; } = "";
        public string HtmlBody { get; set; } = "";
        public string PlainBody { get; set; } = "";
        public List<EmailRecipient> Recipients { get; } = new List<EmailRecipient>();

        public IReadOnlyList<EmailAttachment> Attachments => Template.Attachments;

        /// <summary>Names of variables left without a value; their tokens remain in the output.</summary>
        public List<string> UnresolvedVariables { get; } = new List<string>();

        public bool HasHtml => HtmlBody.Length > 0;
    }
}
