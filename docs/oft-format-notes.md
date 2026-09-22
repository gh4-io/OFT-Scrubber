# OFT format notes

Condensed from MS-CFB, MS-OXMSG, MS-OXRTFCP and MS-OXRTFEX. Structural facts only; never add
content from real samples here.

## Container
- Compound File Binary (signature `D0 CF 11 E0 A1 B1 1A E1`), 512-byte (v3) or 4096-byte (v4)
  sectors, mini stream for streams under 4096 bytes.
- Root CLSID `{0006F046-0000-0000-C000-000000000046}` = Outlook template; `{00020D0B-…}` = saved
  message. Files named `.oft` can carry either; both are accepted (`IsOutlookTemplate`).
- `PR_MESSAGE_CLASS` (0x001A) stays `IPM.Note` for templates.

## Properties
- `__properties_version1.0`: header 32 bytes (top level), 24 (embedded message), 8
  (recipient/attachment); 16-byte entries (tag, flags, 8-byte value). Fixed-size values inline.
- Variable-size values: `__substg1.0_TTTTYYYY` streams; type 001F UTF-16LE, 001E 8-bit (code page
  from PR_MESSAGE_CODEPAGE 0x3FFD or PR_INTERNET_CPID 0x3FDE, else 1252), 0102 binary.

| Tag | Meaning |
|---|---|
| 0x0037 | Subject |
| 0x1000 | Plain-text body |
| 0x1013 | HTML body, usually binary (decode with 0x3FDE, then meta charset, then UTF-8, then 1252) |
| 0x1009 | PR_RTF_COMPRESSED (LZFu); may hold HTML encapsulated with `\fromhtml1` |
| 0x0C15 | Recipient type: 1 To, 2 Cc, 3 Bcc |
| 0x3001 / 0x39FE / 0x3003 / 0x3002 | Display name / SMTP address / email address / address type (`EX` = Exchange) |
| 0x3705 | Attach method (1 by value, 5 embedded message, 6 OLE, 2–4 references) |
| 0x3701 / 0x3707 / 0x3704 | Attachment data / long filename / short filename |
| 0x370E / 0x3712 / 0x7FFE | MIME tag / Content-ID (for `cid:`) / hidden flag |

Recipient storages `__recip_version1.0_#XXXXXXXX`, attachment storages
`__attach_version1.0_#XXXXXXXX`. Named properties (`__nameid_version1.0`) are not read in v0.1.

## Body selection
1. PR_HTML (0x1013) if present.
2. Else RTF → LZFu decompress → `\fromhtml1` de-encapsulation (`\*\htmltag` groups kept,
   `\htmlrtf … \htmlrtf0` suppressed, `\mhtmltag` skipped, `\'hh` decoded with `\ansicpg`).
3. Else plain body, with a FidelityNote that formatting is lost.

Known gaps: RTF CRC not checked; per-font `\fcharset` code pages ignored; embedded-message and
OLE attachments are reported, not carried.

## Word-generated HTML quirks the scanner handles
- Tokens split across `<span>` runs, `SpellE`/`GramE` spans, `<o:p>`.
- `&lt;&lt;Name&gt;&gt;`, `&nbsp;` inside tokens, « » from autocorrect.
- Conditional comments `<!--[if mso]>` and `<xml>` blocks (skipped).
- Percent-encoded braces in `href` (`%7B%7BName%7D%7D`).

## Observations from samples (structure only)
- Sample set 1 (one file): CLSID of a saved message despite the `.oft` name; PR_HTML present, no
  plain or RTF body; unreferenced hidden PNG attachments; tokens only in `{{ }}` form, all intact
  within text runs.
