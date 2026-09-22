# Decisions

Significant decisions only. Newest last.

## D1. Family stack: WPF on .NET Framework 4.8, single exe, no runtime packages (2026-09-21)
Matches ICS Scrubber: .NET Framework 4.8 ships with Windows, so one exe runs with nothing
installed. Carried over: csproj pattern (embedded PDB, no .exe.config, informational version
`x.y.z+build.<UTC>`), app.manifest, single-instance launcher, crash dialog, Fluent/Shell themes,
ribbon + command bar + panes + status bar layout, code-behind UI, PowerShell test scripts.
Not carried: ICS parsing/scrub/diff code, settings and presets (nothing to persist yet).
Deliberate differences: assistant configuration (CLAUDE.md, `.claude/`, docs/PROJECT_STATE.md) is
tracked in the development repository but, as with ICS Scrubber, left out of published releases;
commits use conventional prefixes.

## D2. In-house read-only OFT parser (2026-09-21)
.oft is a compound file with the MS-OXMSG layout (template CLSID `0006F046-…`; `.msg` uses
`00020D0B-…`, and some files named .oft carry it). MsgReader (MIT) would read it but pulls in
five runtime packages; OpenMcdf is MPL-2.0 and also a runtime dependency. A read-only CFB reader,
MAPI property reader, LZFu decompressor and RTF HTML de-encapsulation were written from the
MS-CFB, MS-OXMSG, MS-OXRTFCP and MS-OXRTFEX specs (~1,100 lines). No writing of .msg/.oft.

## D3. HTML-preserving token replacement over a logical-text map (2026-09-21)
Word-generated HTML splits tokens across spans and encodes `<<` as entities, so tokens are
matched on decoded logical text mapped back to source offsets, and replacement edits only token
characters. The HTML is never parsed into a DOM and re-serialized, so untouched markup is
byte-identical. Tokens cannot span block elements, comments or style/script content.

## D4. Variable identity and syntaxes (2026-09-21)
Syntaxes are a registry of small strategies: `{{ }}`, `[[ ]]`, `${ }`, `<< >>` (also « »),
`%% %%` on by default; `%Name%` off by default (percentages and URL escapes). Names are narrow
identifiers (letters, digits, underscore, internal space/dot/hyphen). Same name in any syntax or
location → one variable; comparison is case-insensitive with whitespace collapsed; the first
spelling is shown. Revisit if real templates need case-sensitive distinctions.

## D5. Preview in a locked-down WinForms WebBrowser (2026-09-21)
WebView2 needs a runtime package and native loader (breaks D1); the WPF WebBrowser cannot supply
ambient properties. The WinForms WebBrowser in a WindowsFormsHost answers DISPID_AMBIENT_DLCONTROL
(no scripts/ActiveX/Java/behaviours/frames, forced offline) and cancels navigation; the preview
copy is also sanitized. X-UA-Compatible meta selects the modern engine without a registry write.
Verified by an automated check that script runs in a plain WebBrowser and not in ours.
Trade-off: Trident rendering differs slightly from Outlook's Word engine.

## D6. Transport boundary; `.eml` draft first (2026-09-21)
Rendering produces a `RenderedEmail`; `IEmailOutput` adapters deliver it. v0.1 ships an `.eml`
draft (`X-Unsent: 1`, Message-ID) that classic and new Outlook open as an editable draft, so
nothing locks the project to classic Outlook COM (absent in new Outlook) or Graph (needs app
registration). The user always presses Send. Final delivery method is pending user input.

## D7. Hidden, unreferenced attachments are dropped with a notice (2026-09-21)
Outlook does not show hidden attachments the body no longer references (e.g. images left by an
edited signature). Carrying them would silently send invisible files, so they are omitted and a
FidelityNote tells the user.

## D8. Typed variables; a link value may add one anchor (2026-09-21)
Variables carry a type and format so dates, times, numbers, links and emails are entered once
and written consistently. Types are guessed from the name (whole words: date, time, url, email…)
and are not persisted, matching "no settings persistence". Output uses invariant English month
and day names so a draft reads the same on any machine; input also accepts the current culture.
A clickable link is the one exception to "values are text": it is written as a single escaped
`<a href>` over the token characters only, never inside an existing link, and only for http,
https or mailto targets. Invalid input leaves the token as written, like an empty value.
