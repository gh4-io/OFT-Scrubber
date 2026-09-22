<#
.SYNOPSIS
    Focused checks of the Core pipeline against the Release exe. Windows PowerShell 5.1.

.DESCRIPTION
    Uses only synthetic content. With -Samples, also parses every file in .staging/samples and
    prints structural counts only: never subjects, bodies, names, addresses or filenames.
#>
param(
    [string]$Exe = 'src/OftScrubber/bin/Release/net48/OftScrubber.exe',
    [switch]$Samples
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[void][Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath (Join-Path $root $Exe)).Path)
Add-Type -AssemblyName System.Windows.Forms

$script:failed = 0
function Assert($condition, $message) {
    if ($condition) { Write-Output "PASS $message" }
    else { Write-Output "FAIL $message"; $script:failed++ }
}

$engine = New-Object OftScrubber.Core.Templating.TemplateEngine ([OftScrubber.Core.Templating.TokenSyntaxRegistry]::CreateDefault())

function New-Template([string]$html, [string]$subject = '') {
    $t = New-Object OftScrubber.Core.Model.EmailTemplate
    $t.HtmlBody = $html
    $t.Subject = $subject
    return $t
}

function Render([OftScrubber.Core.Model.EmailTemplate]$template, [hashtable]$values) {
    $set = $engine.Scan($template)
    foreach ($key in $values.Keys) { $v = $set.Find($key); if ($v) { $v.Value = $values[$key] } }
    return $engine.Render($template, $set)
}

# Detection
$t = New-Template '<p>Aircraft {{Tail}} arrives.</p>' 'Arrival {{Tail}}'
$set = $engine.Scan($t)
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Count -eq 2) 'One variable in subject and body is one field used twice'

$set = $engine.Scan((New-Template '<p>{{Tail}}</p><td>{{Tail}}</td><div>{{ Tail }}</div>'))
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Count -eq 3) 'Duplicate tokens normalize to one logical variable'

$set = $engine.Scan((New-Template '<p>{{TailNumber}} [[tailnumber]] &lt;&lt;TailNumber&gt;&gt; ${TailNumber} %%TAILNUMBER%% &#171;TailNumber&#187;</p>'))
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Count -eq 6 -and @($set.Variables[0].Syntaxes).Count -eq 5) 'Different syntaxes with the same name merge case-insensitively'

$split = '<p class=MsoNormal><span style="color:red">{{Tail</span><span lang=EN-US style="color:red">Number}}</span></p>'
$set = $engine.Scan((New-Template $split))
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Name -eq 'TailNumber') 'Token split across Word inline spans is found'
$r = Render (New-Template $split) @{ TailNumber = 'N123' }
Assert ($r.HtmlBody -eq '<p class=MsoNormal><span style="color:red">N123</span><span lang=EN-US style="color:red"></span></p>') 'Split token is replaced in the first span and markup is kept'

$set = $engine.Scan((New-Template '<p>{{Tail&nbsp;Number}}</p>'))
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Name -eq 'Tail Number') 'Non-breaking space inside a token is treated as a space'

$set = $engine.Scan((New-Template '<p>{{Tail</p><p>Number}}</p>'))
Assert ($set.Variables.Count -eq 0 -and $set.StrayOpeners -eq 1) 'Tokens never span paragraphs; the stray opener is reported'

$noise = '<style>p{{color:red}} .w{width:100%} a[href]{x:1}</style><!--[if gte mso 9]>{{Hidden}}<![endif]--><p style="width:50%">50% off, 20%, {not} {{ }} [[ ]] $5 {{Name} {Name}} %20 %%</p><script>var a={{b:1}};</script>'
$set = $engine.Scan((New-Template $noise))
Assert ($set.Variables.Count -eq 0) 'CSS, comments, scripts, percentages and malformed delimiters are not variables'

# Rendering
$html = '<html><head><style>p{margin:0}</style></head><body><p style="font-family:Calibri">Hello {{Name}}, see <a href="https://x.test/p?q={{Name}}&amp;r=1">this</a> and <a href="{{Link}}">here</a>.</p></body></html>'
$r = Render (New-Template $html 'Hi {{Name}}') @{ Name = 'A <b> & "C"'; Link = 'https://a.test/?x=1&y=2' }
Assert ($r.HtmlBody.Contains('Hello A &lt;b&gt; &amp; &quot;C&quot;,')) 'Body values are HTML-escaped'
Assert ($r.HtmlBody.Contains('q=A%20%3Cb%3E%20%26%20%22C%22&amp;r=1')) 'Values inside a link are percent-encoded'
Assert ($r.HtmlBody.Contains('href="https://a.test/?x=1&amp;y=2"')) 'A token that is the whole link takes the value as a URL'
Assert ($r.Subject -eq 'Hi A <b> & "C"') 'Subject gets the plain value'
Assert ($r.HtmlBody.StartsWith('<html><head><style>p{margin:0}</style></head><body><p style="font-family:Calibri">Hello ')) 'Markup around tokens is unchanged'

$r = Render (New-Template $html) @{}
Assert ($r.HtmlBody -eq $html -and $r.UnresolvedVariables.Count -eq 2) 'Empty values leave tokens exactly as written and are reported'

$set = $engine.Scan((New-Template '<a href="https://x.test/?t=%7B%7BTail%7D%7D">x</a>'))
Assert ($set.Variables.Count -eq 1 -and $set.Variables[0].Name -eq 'Tail') 'Percent-encoded token inside a link is found'

$r = Render (New-Template '<p>{{Note}}</p>') @{ Note = "line one`r`nline two" }
Assert ($r.HtmlBody -eq '<p>line one<br>line two</p>') 'Multi-line values become line breaks in HTML'

$r = Render (New-Template '<img alt={{Name}} src=x.png><a title = "a>b {{Name}}">x</a>') @{ Name = 'John Smith' }
Assert ($r.HtmlBody -eq '<img alt=John&#32;Smith src=x.png><a title = "a>b John Smith">x</a>') 'Unquoted attribute values stay one attribute; spaced quotes are honoured'

# Variable types
$types = [OftScrubber.Core.Templating.VariableType]
$f = [OftScrubber.Core.Templating.VariableFormatter]
Assert ($f::Guess('Arrival Date') -eq $types::Date -and $f::Guess('ArrivalDate') -eq $types::Date -and $f::Guess('Update') -eq $types::Text) 'Date type is guessed from a whole word in the name'
Assert ($f::Guess('ETA Time') -eq $types::Time -and $f::Guess('Date Time') -eq $types::DateTime -and $f::Guess('Portal URL') -eq $types::Link -and $f::Guess('Contact E-mail') -eq $types::Email -and $f::Guess('Tail Number') -eq $types::Text) 'Time, date and time, link and email types are guessed from the name'

function Typed([string]$html, [string]$subject, [string]$name, $type, [string]$format, [string]$value, [string]$time = '', [string]$label = '') {
    $t = New-Template $html $subject
    $set = $engine.Scan($t)
    $v = $set.Find($name)
    $v.Type = $type
    if ($format) { $v.Format = $format }
    $v.Value = $value; $v.Time = $time; $v.LinkText = $label
    return @{ Variable = $v; Result = $engine.Render($t, $set) }
}

$x = Typed '<p>{{Day}}</p>' 'Due {{Day}}' 'Day' $types::Date 'dddd, MMMM d, yyyy' '2026-09-21'
Assert ($x.Result.HtmlBody -eq '<p>Monday, September 21, 2026</p>' -and $x.Result.Subject -eq 'Due Monday, September 21, 2026') 'Date is written in the chosen format everywhere'
$x = Typed '<p>{{Day}}</p>' '' 'Day' $types::Date 'ddMMMyy' '21 sep 2026'
Assert ($x.Result.HtmlBody -eq '<p>21SEP26</p>') 'Aviation date format is uppercase'
$x = Typed '<p>{{Day}}</p>' '' 'Day' $types::Date '' 'not a date'
Assert ($x.Result.HtmlBody -eq '<p>{{Day}}</p>' -and $x.Variable.Problem -and $x.Result.UnresolvedVariables.Count -eq 1) 'An unreadable date leaves the token and explains why'

$ok = $true
foreach ($pair in @(@('1430','14:30'), @('930','09:30'), @('2:30 pm','14:30'), @('12am','00:00'), @('14.05','14:05'))) {
    $x = Typed '<p>{{At}}</p>' '' 'At' $types::Time 'HH:mm' $pair[0]
    if ($x.Result.HtmlBody -ne "<p>$($pair[1])</p>") { $ok = $false }
}
$bad = Typed '<p>{{At}}</p>' '' 'At' $types::Time 'HH:mm' '25:00'
$bare = Typed '<p>{{At}}</p>' '' 'At' $types::Time 'HH:mm' '14'
Assert ($ok -and $bad.Variable.Problem -and $bare.Variable.Problem) 'Times parse from 1430, 930, 2:30 pm and 14.05; 25:00 and a bare 14 are rejected'

$x = Typed '<p>{{When}}</p>' '' 'When' $types::DateTime 'd MMM yyyy HH:mm' '2026-09-21' '0715'
$y = Typed '<p>{{When}}</p>' '' 'When' $types::DateTime '' '2026-09-21' ''
Assert ($x.Result.HtmlBody -eq '<p>21 Sep 2026 07:15</p>' -and $y.Variable.Problem -eq 'Enter a time.') 'Date and time combine both inputs and require both'

$x = Typed '<p>{{Qty}}</p>' '' 'Qty' $types::Number 'grouped' '1234567.5'
$y = Typed '<p>{{Qty}}</p>' '' 'Qty' $types::Number '' 'twelve'
Assert ($x.Result.HtmlBody -eq '<p>1,234,567.5</p>' -and $y.Variable.Problem) 'Numbers are formatted and validated'

$x = Typed '<p>See {{Site}} now</p>' 'Site {{Site}}' 'Site' $types::Link 'link' 'www.example.test/a?b=1&c=2'
Assert ($x.Result.HtmlBody -eq '<p>See <a href="https://www.example.test/a?b=1&amp;c=2">https://www.example.test/a?b=1&amp;c=2</a> now</p>' -and $x.Result.Subject -eq 'Site https://www.example.test/a?b=1&c=2') 'Clickable link becomes one anchor in the body and the address elsewhere'
$x = Typed '<p>{{Site}}</p>' '' 'Site' $types::Link 'link' 'https://example.test/' '' 'Open <portal>'
Assert ($x.Result.HtmlBody -eq '<p><a href="https://example.test/">Open &lt;portal&gt;</a></p>') 'Link text is shown and escaped'
$x = Typed '<p><a href="{{Site}}">{{Site}}</a></p>' '' 'Site' $types::Link 'link' 'https://example.test/'
Assert ($x.Result.HtmlBody -eq '<p><a href="https://example.test/">https://example.test/</a></p>') 'A link token already inside a link is not wrapped again'
$bad = @('javascript:alert(1)', 'file:///c:/x', 'https://a b.test', 'no-dot') | Where-Object { (Typed '<p>{{Site}}</p>' '' 'Site' $types::Link 'link' $_).Variable.IsFilled }
Assert (@($bad).Count -eq 0) 'Only http, https and mailto links are accepted'
$x = Typed '<p>{{Site}}</p>' '' 'Site' $types::Link 'text' 'https://example.test/'
Assert ($x.Result.HtmlBody -eq '<p>https://example.test/</p>') 'Address as text is not linked'

$x = Typed '<p>Mail {{Contact Email}}</p><a href="{{Contact Email}}">x</a>' '' 'Contact Email' $types::Email 'link' 'ops@example.invalid'
Assert ($x.Result.HtmlBody -eq '<p>Mail <a href="mailto:ops@example.invalid">ops@example.invalid</a></p><a href="mailto:ops@example.invalid">x</a>') 'Email link uses mailto in body text and whole hrefs'
$bad = @('a@b', 'a b@c.test', 'x@y.test>', 'Name <a@b.test>', "a@b.test`r`nBcc: x@y.test") | Where-Object { (Typed '<p>{{E}}</p>' '' 'E' $types::Email 'text' $_).Variable.IsFilled }
Assert (@($bad).Count -eq 0) 'Malformed or header-breaking email addresses are rejected'

$t = New-Template '<p>{{Site}}</p>'
$t.PlainBody = 'Go to {{Site}}'
$set = $engine.Scan($t); $v = $set.Find('Site'); $v.Type = $types::Link; $v.Value = 'https://example.test/'; $v.LinkText = 'the portal'
$r = $engine.Render($t, $set)
Assert ($r.PlainBody -eq 'Go to the portal (https://example.test/)') 'Plain text body keeps a labelled link readable'

$t = New-Template '<p>{{Contact Email}}</p><a href="https://x.test/?u={{Contact Email}}">x</a>' 'For {{Contact Email}}'
$t.Recipients.Add((New-Object OftScrubber.Core.Model.EmailRecipient ([OftScrubber.Core.Model.RecipientKind]::To), '{{Contact Email}}', '{{Contact Email}}'))
$set = $engine.Scan($t); $v = $set.Find('Contact Email'); $v.Value = 'ops@example.invalid'; $v.LinkText = 'Ops desk'
$r = $engine.Render($t, $set)
Assert ($r.Recipients[0].Address -eq 'ops@example.invalid' -and $r.Subject -eq 'For ops@example.invalid' -and $r.HtmlBody.Contains('?u=ops%40example.invalid') -and $r.HtmlBody.Contains('>Ops desk</a>')) 'A link label never replaces the address in recipients, subject or URL data'

$x = Typed '<p><a href="https://a.test/"><!--[if mso]>c<![endif]-->{{Site}}</a></p>' '' 'Site' $types::Link 'link' 'https://b.test/'
Assert ($x.Result.HtmlBody -eq '<p><a href="https://a.test/"><!--[if mso]>c<![endif]-->https://b.test/</a></p>') 'A comment inside a link does not let a link value nest another link'

$x = Typed '<p>{{Code}}</p>' '' 'Code' $types::Text 'upper' 'n123ab'
Assert ($x.Result.HtmlBody -eq '<p>N123AB</p>') 'Text can be uppercased'

# Output
$t = New-Template '<html><head><meta charset="utf-8"></head><body><img src="cid:logo@x"> {{Name}}</body></html>' 'Caf{{Name}}'
$logo = New-Object OftScrubber.Core.Model.EmailAttachment 'logo.png', 'image/png', 'logo@x', ([byte[]](1,2,3)), $true
$logo.IsInline = $true
$t.Attachments.Add($logo)
$t.Attachments.Add((New-Object OftScrubber.Core.Model.EmailAttachment 'doc.pdf', 'application/pdf', '', ([byte[]](4,5)), $false))
$t.Recipients.Add((New-Object OftScrubber.Core.Model.EmailRecipient ([OftScrubber.Core.Model.RecipientKind]::To), 'Ops Desk', 'ops@example.invalid'))
$r = Render $t @{ Name = ([string][char]0x00E9) }
$notes = $null
$eml = [Text.Encoding]::ASCII.GetString([OftScrubber.Core.Output.EmlDraftWriter]::ToBytes($r, [ref]$notes))
Assert ($eml.StartsWith("X-Unsent: 1`r`n") -and $eml.Contains('Message-ID: <') -and $eml.Contains('To: "Ops Desk" <ops@example.invalid>')) 'Draft is marked unsent with a Message-ID and recipients'
Assert ($eml.Contains('multipart/mixed') -and $eml.Contains('multipart/related') -and $eml.Contains('Content-ID: <logo@x>') -and $eml.Contains('Content-Disposition: attachment; filename="doc.pdf"')) 'Inline images are related parts and files are attachments'
Assert ($eml.Contains('Subject: =?utf-8?B?') -and ![regex]::IsMatch($eml, '(?<!\r)\n')) 'Non-ASCII subject is encoded and lines end in CRLF'

$t2 = New-Template '<html><head><meta http-equiv=Content-Type content="text/html; charset=iso-8859-1"></head><body>{{Name}}</body></html>'
$t2.Attachments.Add((New-Object OftScrubber.Core.Model.EmailAttachment ('r' + [char]0x00E9 + 'sum' + [char]0x00E9 + '.pdf'), "text/plain`r`nX-Evil: 1", '', ([byte[]](1)), $false))
$r = Render $t2 @{ Name = ([string][char]0x4E2D + [char]0x00E9) }
$eml = [Text.Encoding]::ASCII.GetString([OftScrubber.Core.Output.EmlDraftWriter]::ToBytes($r, [ref]$notes))
$htmlPart = [regex]::Match($eml, 'charset="iso-8859-1"\r\nContent-Transfer-Encoding: base64\r\n\r\n(?<b>[A-Za-z0-9+/=\r\n]+)').Groups['b'].Value
$decoded = [Text.Encoding]::GetEncoding('iso-8859-1').GetString([Convert]::FromBase64String($htmlPart))
Assert ($decoded.Contains('<body>&#20013;&#233;</body>')) 'Declared HTML charset is kept; characters it cannot hold become references'
Assert (!$eml.Contains('X-Evil') -and $eml.Contains('Content-Type: application/octet-stream;') -and $eml.Contains("filename*=utf-8''r%C3%A9sum%C3%A9.pdf")) 'Attachment MIME type cannot inject headers; non-ASCII names use RFC 2231'

# Preview
$p = [OftScrubber.Core.Preview.PreviewHtml]::Build('<html><head></head><body onload="x()"><script>alert(1)</script><img src="cid:logo@x"><img src="https://tracker.test/p.gif"><a href="javascript:alert(1)">x</a><div style="background:url(http://x.test/b.png)"></div></body></html>', $t.Attachments)
Assert (!$p.Html.Contains('<script') -and !$p.Html.Contains('onload') -and !$p.Html.Contains('javascript:')) 'Preview copy strips scripts, handlers and script links'
$p2 = [OftScrubber.Core.Preview.PreviewHtml]::Build('<style>@import "\\host\x.css"; p{}</style><svg><image xlink:href="\\host\i.png"/></svg><a href="https://ok.test/">x</a>', $t.Attachments)
Assert (!$p2.Html.Contains('\\host') -and $p2.Html.Contains('href="https://ok.test/"')) 'Preview drops @import and resource hrefs but keeps links'
Assert ($p.Html.Contains('src="data:image/png;base64,AQID"') -and $p.BlockedImages -eq 2 -and !$p.Html.Contains('tracker.test') -and !$p.Html.Contains('x.test/b.png')) 'Preview shows cid images and blocks remote ones'

# Browser host: scripts must not run even if the sanitizer missed one.
function Get-ScriptResult($browser) {
    $done = $false
    $handler = [Windows.Forms.WebBrowserDocumentCompletedEventHandler]{ $script:done = $true }
    $browser.add_DocumentCompleted($handler)
    $script:done = $false
    $html = '<html><body><div id="t">no</div><script>document.getElementById("t").innerText="ran";</script></body></html>'
    if ($browser -is [OftScrubber.Preview.SafeWebBrowser]) { $browser.ShowHtml($html) } else { $browser.DocumentText = $html }
    $deadline = [DateTime]::Now.AddSeconds(10)
    while (!$script:done -and [DateTime]::Now -lt $deadline) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    for ($i = 0; $i -lt 10; $i++) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    return $browser.Document.GetElementById('t').InnerText
}
$form = New-Object Windows.Forms.Form
$form.ShowInTaskbar = $false
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point -32000, -32000
$plain = New-Object Windows.Forms.WebBrowser
$safe = New-Object OftScrubber.Preview.SafeWebBrowser
$form.Controls.Add($plain); $form.Controls.Add($safe)
$form.Show()
$control = Get-ScriptResult $plain
$locked = Get-ScriptResult $safe
$lockedAgain = Get-ScriptResult $safe
$form.Close()
Assert ($control -eq 'ran' -and $locked -eq 'no' -and $lockedAgain -eq 'no') "Preview host blocks scripts (control browser: $control, preview: $locked)"

if ($Samples) {
    $folder = Join-Path $root '.staging/samples'
    $index = 0
    foreach ($file in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Extension -in '.oft', '.msg' }) {
        $index++
        try {
            $template = [OftScrubber.Core.Oft.OftReader]::Read($file.FullName)
            $s = $engine.Scan($template)
            $counts = ($s.Variables | ForEach-Object { $_.Count }) -join ','
            Write-Output ("SAMPLE {0}: template={1} html={2} ({3} chars) plain={4} recipients={5} attachments={6} inline={7} variables={8} occurrences=[{9}] stray={10} warnings={11} fidelity={12}" -f `
                $index, $template.IsOutlookTemplate, $template.HtmlSource, $template.HtmlBody.Length, $template.PlainBody.Length, $template.Recipients.Count,
                $template.Attachments.Count, @($template.Attachments | Where-Object IsInline).Count, $s.Variables.Count, $counts, $s.StrayOpeners,
                $template.Warnings.Count, $template.FidelityNotes.Count)
            $r = $engine.Render($template, $s)
            Assert ($r.HtmlBody -eq $template.HtmlBody) "Sample $index renders byte-identical HTML when no values are given"
            $n = 0
            foreach ($v in $s.Variables) { $n++; $v.Type = [OftScrubber.Core.Templating.VariableType]::Text; $v.Value = "Value $n <&>" }
            $r = $engine.Render($template, $s)
            $left = $engine.Registry.Find($r.Subject).Count + (New-Object OftScrubber.Core.Templating.HtmlText $r.HtmlBody).FindTokens($engine.Registry).Count
            $notes = $null
            $bytes = [OftScrubber.Core.Output.EmlDraftWriter]::ToBytes($r, [ref]$notes)
            [IO.File]::WriteAllBytes((Join-Path $root ".staging/out/sample-$index.eml"), $bytes)
            Assert ($left -eq 0 -and $r.UnresolvedVariables.Count -eq 0 -and $bytes.Length -gt 0) "Sample $index fills every token and writes a draft to .staging/out"
        }
        catch { Write-Output "FAIL sample $index could not be read: $($_.Exception.InnerException.Message)"; $script:failed++ }
    }
}

if ($script:failed -gt 0) { Write-Output "$script:failed check(s) failed"; exit 1 }
Write-Output 'All checks passed'
