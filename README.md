# OFT Scrubber

Version 0.1.1. A Windows tool that opens Outlook `.oft` templates, finds their
variables, lets you fill each one once, previews the finished HTML email safely, and saves it as a
draft that your mail app opens for review and sending.

Part of the same family as ICS Scrubber.

## Using it

1. **Open** a template (Ctrl+O, drop it anywhere on the window, or pass the path on the command
   line).
2. **Detected variables** lists one field per variable name. A badge shows how many places use
   it; typing once fills them all. A red dot marks variables that are still empty.
   Each variable has a type (text, date, time, date and time, number, web link, email), guessed
   from its name and changeable from the chip on its card, and a format such as `21 Sep 2026`,
   `September 21, 2026`, `21SEP26`, `14:30` or `2:30 PM`. Dates can be picked from a calendar;
   times accept `1430`, `14:30` or `2:30 pm`. Web and email values can become clickable links
   with optional text to show. A value that cannot be read is explained and left out.
3. **Preview** shows the subject, recipients, attachments and the rendered HTML. The **HTML** tab
   shows the exact markup that will be written. **Details** lists where each variable was found,
   anything that could not be carried over, and parser warnings.
4. **Save draft** writes an `.eml` file. **Open in mail app** saves a draft to your temp folder and
   opens it; Outlook shows it as an unsent message for you to review and send.

Supported token styles: `{{Name}}`, `[[Name]]`, `${Name}`, `<<Name>>` (also `«Name»`) and
`%%Name%%`. The same name in different styles, or in different letter case, is one variable.
Empty variables stay in the email exactly as written.

## Safety

Templates are treated as untrusted. Nothing embedded in them is executed. The preview runs with
scripts, plug-ins, network access and link navigation switched off, and remote images are not
loaded. Nothing is uploaded or sent; sending is always your action in your mail app.

## Build and test

Requires the .NET SDK (for building only). The output runs on the .NET Framework 4.8 built into Windows.

```powershell
dotnet build src/OftScrubber/OftScrubber.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-core.ps1
```

The executable is `src/OftScrubber/bin/Release/net48/OftScrubber.exe`. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/DECISIONS.md](docs/DECISIONS.md).
