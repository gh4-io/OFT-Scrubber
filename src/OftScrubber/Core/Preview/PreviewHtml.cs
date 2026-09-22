using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OftScrubber.Core.Model;

namespace OftScrubber.Core.Preview
{
    /// <summary>
    /// Builds the copy of the HTML shown in the preview. This is one of two layers: the browser
    /// host also refuses scripts, ActiveX, Java, behaviours, refresh and network access. The
    /// output file always uses the rendered HTML, never this copy.
    ///
    /// Regex-based on purpose: it only needs to disarm, not to understand, and anything it
    /// misses is still blocked by the host.
    /// </summary>
    public static class PreviewHtml
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

        // A 1x1 transparent GIF standing in for blocked remote images.
        private const string BlankImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

        private static readonly Regex DangerousElements = new Regex(
            @"<(script|iframe|object|applet|frameset|noscript|form)\b.*?</\1\s*>", Options);

        private static readonly Regex DangerousTags = new Regex(
            @"<(script|iframe|object|embed|applet|frame|frameset|base|link|form|input|button|meta\s[^>]*http-equiv\s*=\s*[""']?refresh)\b[^>]*>", Options);

        private static readonly Regex EventAttributes = new Regex(
            @"(?<=<[^>]*?)\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", Options);

        private static readonly Regex ScriptUrls = new Regex(
            @"(?<attr>\b(?:href|src|action|background|lowsrc|dynsrc)\s*=\s*[""']?)\s*(?:javascript|vbscript|livescript|data:text/html)\s*:?", Options);

        private static readonly Regex CssExpression = new Regex(@"expression\s*\(|behavior\s*:|-moz-binding", Options);

        private static readonly Regex SourceAttributes = new Regex(
            @"(?<attr>\b(?:src|background|lowsrc|dynsrc|poster)\s*=\s*)(?<q>[""']?)(?<url>[^""'\s>]*)\k<q>", Options);

        // String-form @import "x.css" has no url( ), and a UNC path there would reach SMB.
        private static readonly Regex CssImports = new Regex(@"@import\b[^;<]*;?", Options);

        // href loads a resource on everything except links (SVG image/use, VML): route it
        // through the same resolver as src.
        private static readonly Regex ResourceHrefs = new Regex(
            @"(?<tag><(?!a\b|area\b)[a-z][\w:\-]*\b[^>]*?\s)(?<attr>(?:xlink:)?href\s*=\s*)(?<q>[""']?)(?<url>[^""'\s>]*)\k<q>", Options);

        private static readonly Regex CssUrls = new Regex(
            @"url\(\s*(?<q>[""']?)(?<url>[^""')]*)\k<q>\s*\)", Options);

        public sealed class Result
        {
            public Result(string html, int blockedImages)
            {
                Html = html;
                BlockedImages = blockedImages;
            }

            public string Html { get; }

            /// <summary>Remote images not loaded, so opening a preview never contacts a server.</summary>
            public int BlockedImages { get; }
        }

        public static Result Build(string html, IEnumerable<EmailAttachment> attachments)
        {
            var byContentId = new Dictionary<string, EmailAttachment>(StringComparer.OrdinalIgnoreCase);
            foreach (var attachment in attachments.Where(a => a.ContentId.Length > 0))
                byContentId[attachment.ContentId] = attachment;

            var blocked = 0;

            html = DangerousElements.Replace(html, "");
            html = DangerousTags.Replace(html, "");
            html = EventAttributes.Replace(html, "");
            html = ScriptUrls.Replace(html, m => m.Groups["attr"].Value + "#blocked:");
            html = CssExpression.Replace(html, "x-blocked(");

            string Resolve(string url)
            {
                if (url.Length == 0 || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return url;

                if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                    && byContentId.TryGetValue(Uri.UnescapeDataString(url.Substring(4)), out var image))
                    return "data:" + ImageType(image) + ";base64," + Convert.ToBase64String(image.Data);

                blocked++;
                return BlankImage;
            }

            html = SourceAttributes.Replace(html, m => m.Groups["attr"].Value + "\"" + Resolve(m.Groups["url"].Value) + "\"");
            html = ResourceHrefs.Replace(html, m => m.Groups["tag"].Value + m.Groups["attr"].Value + "\"" + Resolve(m.Groups["url"].Value) + "\"");
            html = CssImports.Replace(html, "");
            html = CssUrls.Replace(html, m => "url(\"" + Resolve(m.Groups["url"].Value) + "\")");

            return new Result(WithStandardsMode(html), blocked);
        }

        /// <summary>
        /// The WebBrowser control defaults to IE7 rendering. A leading X-UA-Compatible meta asks
        /// for the newest mode without writing the per-user FEATURE_BROWSER_EMULATION registry key.
        /// </summary>
        private static string WithStandardsMode(string html)
        {
            const string Meta = "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">";
            var head = Regex.Match(html, @"<head\b[^>]*>", Options);

            if (head.Success) return html.Insert(head.Index + head.Length, Meta);
            return Meta + html;
        }

        private static string ImageType(EmailAttachment image)
        {
            if (image.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return image.MimeType;

            var name = image.FileName.ToLowerInvariant();
            if (name.EndsWith(".png")) return "image/png";
            if (name.EndsWith(".gif")) return "image/gif";
            if (name.EndsWith(".bmp")) return "image/bmp";
            return "image/jpeg";
        }

        /// <summary>Plain text shown as HTML when a template has no HTML body.</summary>
        public static string FromPlainText(string text)
        {
            return "<html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"></head>"
                + "<body style=\"font-family:Segoe UI,sans-serif;font-size:10.5pt;white-space:pre-wrap\">"
                + System.Net.WebUtility.HtmlEncode(text ?? "") + "</body></html>";
        }
    }
}
