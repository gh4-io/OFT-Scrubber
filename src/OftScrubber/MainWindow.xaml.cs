using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OftScrubber.Core.Model;
using OftScrubber.Core.Oft;
using OftScrubber.Core.Output;
using OftScrubber.Core.Preview;
using OftScrubber.Core.Templating;
using OftScrubber.ViewModels;
using OftScrubber.Views;

namespace OftScrubber
{
    /// <summary>
    /// Orchestration only: open, scan, collect values, render, preview, output. Each step lives
    /// in Core; this class wires them to controls.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string AppTitle = "OFT Scrubber";

        private readonly TemplateEngine _engine = new TemplateEngine(TokenSyntaxRegistry.CreateDefault());
        private readonly DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _dragLeaveTimer;
        private bool _synchronizing;

        private EmailTemplate? _template;
        private VariableSet? _variables;
        private RenderedEmail? _rendered;
        private List<VariableRow> _rows = new List<VariableRow>();
        private VariableRow? _calendarRow;

        public MainWindow()
        {
            InitializeComponent();

            // Typing re-renders after a short pause rather than on every keystroke.
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _renderTimer.Tick += (s, e) =>
            {
                _renderTimer.Stop();
                Render();
            };

            // Leave fires when the pointer crosses between elements too; only hide once it stays gone.
            _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _dragLeaveTimer.Tick += (s, e) => ShowDropOverlay(false);

            Browser.NavigationBlocked += (s, e) =>
                StatusText.Text = "Links do not open from the preview. Open the draft in your mail app to follow them.";

            _synchronizing = true;
            TabHome.IsChecked = true;
            ModePreview.IsChecked = true;
            _synchronizing = false;
            ApplyTab();
            ApplyMode();

            Loaded += (s, e) =>
            {
                Browser.ShowHtml(PreviewHtml.FromPlainText(""));
                UpdateCommands();
                if (App.StartupFiles.Length > 0) AddFiles(App.StartupFiles);
            };
        }

        /// <summary>Opens the first template among files passed on the command line or forwarded by a second launch.</summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            var path = paths.FirstOrDefault(IsTemplateFile);

            if (path == null)
            {
                StatusText.Text = "Only Outlook .oft templates (and .msg messages) can be opened.";
                return;
            }

            OpenTemplate(path);
        }

        private static bool IsTemplateFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".oft", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".msg", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenTemplate(string path)
        {
            EmailTemplate template;

            try
            {
                template = OftReader.Read(path);
            }
            catch (OftFormatException ex)
            {
                ShowError("Could not read " + Path.GetFileName(path) + ".\n\n" + ex.Message);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("Could not open " + Path.GetFileName(path) + ".\n\n" + ex.Message);
                return;
            }

            foreach (var row in _rows) row.PropertyChanged -= OnValueChanged;

            _template = template;
            _variables = _engine.Scan(template);
            _rows = _variables.Variables.Select(v => new VariableRow(v)).ToList();

            foreach (var row in _rows) row.PropertyChanged += OnValueChanged;

            VariableList.ItemsSource = _rows;
            VariablesEmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VariablesEmptyText.Text = "No variables were found in this template. Supported forms: {{Name}}, [[Name]], ${Name}, <<Name>> and %%Name%%.";

            TemplateName.Text = Path.GetFileName(path);
            TemplateName.ToolTip = path;
            TemplateSummary.Text = Summarize(template);
            Title = Path.GetFileName(path) + " - " + AppTitle;

            Render();
        }

        private static string Summarize(EmailTemplate template)
        {
            var parts = new List<string>();
            parts.Add(template.HasHtml ? "HTML body" : template.PlainBody.Length > 0 ? "Plain text body" : "No body");

            var inline = template.Attachments.Count(a => a.IsInline);
            var regular = template.Attachments.Count - inline;
            if (inline > 0) parts.Add(inline == 1 ? "1 inline image" : inline + " inline images");
            if (regular > 0) parts.Add(regular == 1 ? "1 attachment" : regular + " attachments");
            if (template.Recipients.Count > 0) parts.Add(template.Recipients.Count == 1 ? "1 recipient" : template.Recipients.Count + " recipients");
            if (!template.IsOutlookTemplate) parts.Add("saved message, not a template");

            return string.Join(" · ", parts);
        }

        private void OnValueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VariableRow.Value)
                && e.PropertyName != nameof(VariableRow.Time)
                && e.PropertyName != nameof(VariableRow.LinkText)) return;
            UpdateFilledText();
            _renderTimer.Stop();
            _renderTimer.Start();
        }

        private void Render()
        {
            if (_template == null || _variables == null) return;

            _rendered = _engine.Render(_template, _variables);

            SubjectText.Text = _rendered.Subject.Length > 0 ? _rendered.Subject : "(no subject)";
            ShowRecipients(ToLabel, ToText, RecipientKind.To);
            ShowRecipients(CcLabel, CcText, RecipientKind.Cc);
            ShowRecipients(BccLabel, BccText, RecipientKind.Bcc);

            var attached = _rendered.Attachments.Where(a => !a.IsInline).Select(a => a.FileName).ToList();
            AttachText.Text = string.Join("; ", attached);
            AttachLabel.Visibility = AttachText.Visibility = attached.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var notices = new List<string>();

            if (_rendered.HasHtml)
            {
                var preview = PreviewHtml.Build(_rendered.HtmlBody, _rendered.Attachments);
                Browser.ShowHtml(preview.Html);

                if (preview.BlockedImages > 0)
                    notices.Add(preview.BlockedImages + " remote image reference(s) are not loaded in the preview. They stay in the email unchanged.");
            }
            else
            {
                Browser.ShowHtml(PreviewHtml.FromPlainText(_rendered.PlainBody));
            }

            if (_template.FidelityNotes.Count > 0) notices.Add(string.Join(" ", _template.FidelityNotes));

            PreviewNoticeText.Text = string.Join("\n", notices);
            PreviewNotice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            HtmlView.Text = _rendered.HasHtml ? _rendered.HtmlBody : _rendered.PlainBody;
            DetailsText.Text = DescribeDetails();

            UpdateFilledText();
            UpdateCommands();
        }

        private void ShowRecipients(UIElement label, System.Windows.Controls.TextBlock text, RecipientKind kind)
        {
            var recipients = _rendered!.Recipients.Where(r => r.Kind == kind).Select(r => r.ToString()).ToList();
            text.Text = string.Join("; ", recipients);
            label.Visibility = text.Visibility = recipients.Count > 0 || kind == RecipientKind.To ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFilledText()
        {
            if (_variables == null || _variables.Variables.Count == 0)
            {
                FilledText.Text = "";
                StatusText.Text = _template == null ? "Drop an .oft file anywhere in this window to begin." : "This template has no variables.";
                return;
            }

            var total = _variables.Variables.Count;
            var filled = _variables.FilledCount;
            FilledText.Text = filled + " of " + total + " filled";

            StatusText.Text = filled == total
                ? "All variables have values. Save the draft or open it in your mail app."
                : (total - filled) + " variable(s) still empty. Their tokens stay in the email exactly as written.";
        }

        private string DescribeDetails()
        {
            var template = _template!;
            var text = new StringBuilder();

            text.AppendLine("Template");
            text.AppendLine("  Type: " + (template.IsOutlookTemplate ? "Outlook template (.oft)" : "Outlook message") + (template.MessageClass.Length > 0 ? ", " + template.MessageClass : ""));
            text.AppendLine("  Body: " + (template.HasHtml ? "HTML from " + template.HtmlSource : template.PlainBody.Length > 0 ? "plain text only" : "none"));
            text.AppendLine("  Attachments: " + template.Attachments.Count + " (" + template.Attachments.Count(a => a.IsInline) + " inline)");
            text.AppendLine();

            if (template.FidelityNotes.Count > 0)
            {
                text.AppendLine("Not carried into the output");
                foreach (var note in template.FidelityNotes) text.AppendLine("  " + note);
                text.AppendLine();
            }

            if (template.Warnings.Count > 0)
            {
                text.AppendLine("Warnings");
                foreach (var warning in template.Warnings) text.AppendLine("  " + warning);
                text.AppendLine();
            }

            if (_variables!.StrayOpeners > 0)
            {
                text.AppendLine("Possible broken tokens");
                text.AppendLine("  " + _variables.StrayOpeners + " opening delimiter(s) such as {{ have no matching close or an unusual name. Check the template for typos.");
                text.AppendLine();
            }

            text.AppendLine("Variables");
            foreach (var variable in _variables.Variables)
            {
                text.AppendLine("  " + variable.Name + ": " + VariableFormatter.Describe(variable.Type).ToLowerInvariant()
                    + ", " + VariableFormatter.Label(variable.Type, VariableFormatter.FindFormat(variable.Type, variable.Format))
                    + ", " + variable.Count + " place(s)");
                foreach (var group in variable.Occurrences.GroupBy(o => o.Location + "  " + o.RawText))
                    text.AppendLine("      " + group.Key + (group.Count() > 1 ? "  ×" + group.Count() : ""));
            }

            text.AppendLine();
            text.AppendLine("Token styles");
            foreach (var syntax in _engine.Registry.All)
                text.AppendLine("  " + syntax.Example + "  " + (_engine.Registry.IsEnabled(syntax) ? "on" : "off") + ". " + syntax.Note);

            return text.ToString();
        }

        private void UpdateCommands()
        {
            var ready = _rendered != null;
            BtnSaveDraft.IsEnabled = MenuSaveDraft.IsEnabled = ready;
            BtnOpenDraft.IsEnabled = MenuOpenDraft.IsEnabled = ready;
            BtnClearValues.IsEnabled = MenuClearValues.IsEnabled = _rows.Count > 0;
        }

        private void OnOpen(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open an Outlook template",
                Filter = "Outlook templates (*.oft)|*.oft|Outlook messages (*.msg)|*.msg|All files (*.*)|*.*",
            };

            if (dialog.ShowDialog(this) == true) OpenTemplate(dialog.FileName);
        }

        private void OnSaveDraft(object sender, RoutedEventArgs e)
        {
            if (!ReadyToOutput()) return;

            var dialog = new SaveFileDialog
            {
                Title = "Save draft",
                Filter = "Email draft (*.eml)|*.eml",
                FileName = SafeFileName(_rendered!.Subject) + ".eml",
            };

            if (dialog.ShowDialog(this) == true) Deliver(new EmlDraftOutput(dialog.FileName, false));
        }

        /// <summary>
        /// Saves to a private temp folder and hands the file to the default mail app, which opens
        /// it as a draft. Sending stays a separate action the user takes in that app.
        /// </summary>
        private void OnOpenDraft(object sender, RoutedEventArgs e)
        {
            if (!ReadyToOutput()) return;

            var folder = Path.Combine(Path.GetTempPath(), "OFT Scrubber");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeFileName(_rendered!.Subject) + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".eml");

            Deliver(new EmlDraftOutput(path, true));
        }

        private bool ReadyToOutput()
        {
            if (_rendered == null) return false;

            _renderTimer.Stop();
            Render();

            if (_rendered.UnresolvedVariables.Count == 0) return true;

            var answer = MessageBox.Show(this,
                "These variables have no value, or a value that does not fit their type, so their tokens will appear in the email as written:\n\n"
                + string.Join("\n", _rendered.UnresolvedVariables.Take(12)) + (_rendered.UnresolvedVariables.Count > 12 ? "\n…" : "")
                + "\n\nContinue anyway?",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);

            return answer == MessageBoxResult.Yes;
        }

        private void Deliver(IEmailOutput output)
        {
            try
            {
                var result = output.Create(_rendered!);
                StatusText.Text = "Saved " + Path.GetFileName(result.Location) + ".";

                if (result.Notes.Count > 0)
                    MessageBox.Show(this, string.Join("\n", result.Notes.Distinct()), AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                ShowError("Could not create the draft.\n\n" + ex.Message);
            }
        }

        private static string SafeFileName(string subject)
        {
            var name = new string(subject.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray()).Trim();
            if (name.Length > 80) name = name.Substring(0, 80).Trim();
            return name.Length > 0 ? name : "Draft";
        }

        private void OnClearValues(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                row.Value = "";
                row.Time = "";
                row.LinkText = "";
            }
        }

        // Type and format chips open a menu of choices; the checked item is the current one.
        private void OnTypeMenu(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (!(button.DataContext is VariableRow row)) return;

            var choices = Enum.GetValues(typeof(VariableType)).Cast<VariableType>()
                .Select(type => (Tuple<string, bool, Action>?)Tuple.Create(VariableFormatter.Describe(type), type == row.Type, (Action)(() => row.Type = type)))
                .ToList();

            // Text has no format chip; its few formats follow the types.
            if (row.Type == VariableType.Text)
            {
                choices.Add(null);
                choices.AddRange(FormatChoices(row));
            }

            ShowChoices(button, choices);
        }

        private static IEnumerable<Tuple<string, bool, Action>> FormatChoices(VariableRow row)
        {
            return row.Formats.Select(format => Tuple.Create(VariableFormatter.Label(row.Type, format), format.Id == row.FormatId, (Action)(() => row.FormatId = format.Id)));
        }

        private void OnFormatMenu(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (!(button.DataContext is VariableRow row)) return;

            ShowChoices(button, FormatChoices(row));
        }

        /// <summary>A null choice is a separator.</summary>
        private static void ShowChoices(Button button, IEnumerable<Tuple<string, bool, Action>?> choices)
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };

            foreach (var choice in choices)
            {
                if (choice == null)
                {
                    menu.Items.Add(new Separator());
                    continue;
                }

                var item = new MenuItem { Header = choice.Item1, IsCheckable = true, IsChecked = choice.Item2 };
                var apply = choice.Item3;
                item.Click += (s, e) => apply();
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private void OnCalendar(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (!(button.DataContext is VariableRow row)) return;

            _calendarRow = null; // Setting the calendar below must not write back.
            var date = row.ParsedDate ?? DateTime.Today;
            DateCalendar.SelectedDate = row.ParsedDate;
            DateCalendar.DisplayDate = date;
            _calendarRow = row;

            CalendarPopup.PlacementTarget = button;
            CalendarPopup.IsOpen = true;
            DateCalendar.Focus();
        }

        private void OnCalendarPicked(object? sender, SelectionChangedEventArgs e)
        {
            if (_calendarRow == null || DateCalendar.SelectedDate == null) return;

            _calendarRow.SetDate(DateCalendar.SelectedDate.Value);
            CalendarPopup.IsOpen = false;
            _calendarRow = null;

            // A calendar click leaves mouse capture on the calendar; release it so the next click lands.
            Mouse.Capture(null);
        }

        private void OnAbout(object sender, RoutedEventArgs e)
        {
            new AboutDialog { Owner = this }.ShowDialog();
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Tunnelling handlers, so a file dropped on a text box or card opens the template
        // instead of the text box claiming the drop. Text drags between fields are left alone.
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var accepted = files != null && files.Any(IsTemplateFile);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;

            _dragLeaveTimer.Stop();
            if (accepted) ShowDropOverlay(true);
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _dragLeaveTimer.Stop();
            _dragLeaveTimer.Start();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            _dragLeaveTimer.Stop();
            ShowDropOverlay(false);

            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;

            e.Handled = true;
            AddFiles(files);
        }

        private void ShowDropOverlay(bool show)
        {
            _dragLeaveTimer.Stop();
            DropOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // Hidden, not collapsed, so the preview keeps its layout and document.
            BrowserHost.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        }

        // Ribbon tabs and preview modes behave like radio buttons that cannot be all off.
        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            SelectExclusive((ToggleButton)sender, TabHome, TabHelp);
            ApplyTab();
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            SelectExclusive((ToggleButton)sender, ModePreview, ModeHtml, ModeDetails);
            ApplyMode();
        }

        private void SelectExclusive(ToggleButton source, params ToggleButton[] group)
        {
            if (_synchronizing) return;
            _synchronizing = true;

            if (source.IsChecked == true)
                foreach (var other in group.Where(b => b != source)) other.IsChecked = false;
            else
                source.IsChecked = true;

            _synchronizing = false;
        }

        private void ApplyTab()
        {
            PanelHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelHelp.Visibility = TabHelp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyMode()
        {
            PreviewView.Visibility = ModePreview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HtmlView.Visibility = ModeHtml.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DetailsView.Visibility = ModeDetails.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
