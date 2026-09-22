# Architecture

OFT Scrubber is a single WPF executable targeting .NET Framework 4.8, built like its sibling
ICS Scrubber (`../ICS-Parser`): SDK-style project, no solution file, no runtime packages.

## Pipeline

```
.oft file ─▶ Cfb.CompoundFile ─▶ Oft.OftReader ─▶ Model.EmailTemplate
                                                        │
                                  Templating.TemplateEngine.Scan ─▶ VariableSet (one value per name)
                                                        │   ◀── values typed in the UI
                                  Templating.TemplateEngine.Render ─▶ Model.RenderedEmail
                                                        │
                        ┌───────────────────────────────┴──────────────────────────┐
                Preview.PreviewHtml ─▶ SafeWebBrowser                  Output.IEmailOutput
                (sanitized copy, display only)                         (EmlDraftOutput today)
```

## Source layout (`src/OftScrubber`)

| Folder | Responsibility |
|---|---|
| `Core/Cfb` | Read-only MS-CFB (compound file) reader: header, DIFAT, FAT, miniFAT, directory tree. |
| `Core/Oft` | MS-OXMSG property reading, body selection (PR_HTML → RTF-encapsulated HTML → plain), recipients, attachments, LZFu and RTF de-encapsulation. |
| `Core/Model` | `EmailTemplate` (parsed, with Warnings and FidelityNotes) and `RenderedEmail`. |
| `Core/Templating` | `TokenSyntax` registry, `HtmlText` (HTML → logical text map), `TemplateVariable`/`VariableSet`, `VariableFormatter` (types, formats, parsing), `TemplateEngine`. |
| `Core/Output` | `IEmailOutput` transport boundary; `EmlDraftWriter` MIME builder and `EmlDraftOutput`. |
| `Core/Preview` | `PreviewHtml`: sanitized preview copy (cid → data URIs, remote resources blocked). |
| `Preview` | `SafeWebBrowser`: WinForms WebBrowser with the download-control ambient property (no script, ActiveX, Java, network, navigation). |
| `ViewModels` | `VariableRow`: binds one input to one logical variable. |
| `MainWindow`, `Views`, `Themes` | Code-behind UI, About dialog, family theme copied from ICS Scrubber. |

Core never references WPF or WinForms.

## Key mechanisms

**Variable detection.** Each `TokenSyntax` is a narrow regex with a `name` group, applied to
logical text. `HtmlText` builds that text from the HTML: entities decoded, NBSP as space, inline
elements (span, b, font, a…) transparent, block elements/comments/`style`/`script`/`xml` ending a
run, and `href`/`src`/`title`/`alt` attribute values scanned separately (entity and percent
decoded). Every logical character keeps its source span. Tokens with the same normalized name
(case-insensitive, whitespace collapsed) share one `TemplateVariable`, whatever their syntax.

**Rendering.** Replacement writes the encoded value over the token's first source segment and
deletes the token's remaining segments; tags in between remain. Nothing else in the HTML changes,
so a render with no values is byte-identical to the input. Values are HTML-encoded in text,
percent-encoded inside URLs (unless the token is the whole URL) and forced to one line in headers.
Unfilled tokens stay as written and are listed as unresolved.

**Variable types.** Each variable has a `VariableType` (text, date, time, date and time, number,
web link, email) guessed from whole words in its name and changeable in the UI, plus a format id.
`VariableFormatter` parses what was typed (flexible input: 1430, 2:30 pm, today, 21SEP26) and
writes a `FormattedValue` with invariant English names. A value that cannot be read counts as
unfilled and carries a `Problem` message. A clickable link or email value in HTML text becomes one
`<a href>` (never nested inside an existing link); a whole-`href` token takes its URL (`mailto:`
for email); subject, recipients and other attributes take the text; the plain body shows
"label (address)". Only http, https and mailto URLs are accepted.

**Preview safety.** Two layers. `PreviewHtml` strips scripts, handlers, frames, forms, meta refresh,
`javascript:` URLs and CSS expressions, inlines cid images as data URIs and replaces remote
resources with a blank image. `SafeWebBrowser` answers `DISPID_AMBIENT_DLCONTROL` (via
`ICustomQueryInterface`, because the WinForms site is not COM-visible) with no-scripts, no-ActiveX,
no-Java, no-behaviours, no-frames, forced-offline, and cancels every navigation except
`about:blank`. `tools/test-core.ps1` proves a script runs in a plain WebBrowser and not in ours.

**Output.** `IEmailOutput.Create(RenderedEmail)` is the transport boundary. `EmlDraftOutput`
writes MIME with `X-Unsent: 1`: multipart/mixed (attachments) ⊃ multipart/related (cid images) ⊃
multipart/alternative (plain, HTML). The HTML part keeps the charset its meta tag declares when it
can hold the content. Opening the file hands it to the default mail app as an editable draft;
the user sends from there.

## Verification

`tools/test-core.ps1` loads the Release exe and checks the templating rules, EML structure,
preview sanitizing and script blocking with synthetic content. `-Samples` additionally parses
`.staging/samples/*` and prints structural counts only.
