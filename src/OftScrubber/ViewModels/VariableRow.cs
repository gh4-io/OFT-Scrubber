using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using OftScrubber.Core.Templating;

namespace OftScrubber.ViewModels
{
    /// <summary>One input field bound to one logical variable, however many tokens share it.</summary>
    public sealed class VariableRow : INotifyPropertyChanged
    {
        public VariableRow(TemplateVariable variable)
        {
            Variable = variable;
        }

        public TemplateVariable Variable { get; }

        public string Name => Variable.Name;

        public string Value
        {
            get => Variable.Value;
            set => Set(Variable.Value, value, v => Variable.Value = v, nameof(Value));
        }

        public string Time
        {
            get => Variable.Time;
            set => Set(Variable.Time, value, v => Variable.Time = v, nameof(Time));
        }

        public string LinkText
        {
            get => Variable.LinkText;
            set => Set(Variable.LinkText, value, v => Variable.LinkText = v, nameof(LinkText));
        }

        public VariableType Type
        {
            get => Variable.Type;
            set
            {
                if (Variable.Type == value) return;
                Variable.Type = value;
                RaiseAll();
            }
        }

        public string FormatId
        {
            get => Variable.Format;
            set
            {
                if (Variable.Format == value) return;
                Variable.Format = value;
                RaiseAll();
            }
        }

        public IReadOnlyList<ValueFormat> Formats => VariableFormatter.FormatsFor(Type);

        public string TypeText => Type == VariableType.Text && VariableFormatter.FindFormat(Type, FormatId).Upper ? "Text, UPPERCASE" : VariableFormatter.Describe(Type);

        public string FormatText => VariableFormatter.Label(Type, VariableFormatter.FindFormat(Type, FormatId));

        public bool IsEmpty => !Variable.IsFilled;

        // Which editors the card shows for the current type.
        public bool ShowTextBox => Type != VariableType.Date && Type != VariableType.DateTime;
        public bool IsMultiLine => Type == VariableType.Text;
        public bool ShowDate => Type == VariableType.Date || Type == VariableType.DateTime;
        public bool ShowTime => Type == VariableType.DateTime;
        /// <summary>Text formats live in the type menu, so plain text cards stay compact.</summary>
        public bool ShowFormat => Type != VariableType.Text;
        public bool ShowResultRow => ShowFormat || HasResult;
        public bool ShowLinkText => (Type == VariableType.Link || Type == VariableType.Email) && FormatId == VariableFormatter.LinkFormat;

        /// <summary>Shown under the input: the problem with the value, or what the email will say.</summary>
        public string ResultText
        {
            get
            {
                var problem = Variable.Problem;
                if (problem != null) return problem;

                var formatted = Variable.Formatted;
                if (formatted == null) return Hint;

                // Plain text as typed says nothing new.
                if (Type == VariableType.Text && !VariableFormatter.FindFormat(Type, FormatId).Upper) return "";

                var shows = formatted.Linked ? formatted.Text + " → " + formatted.Url : formatted.Text;
                return "Email shows: " + shows.Replace("\r", " ").Replace("\n", " ");
            }
        }

        public bool HasResult => ResultText.Length > 0;

        public bool HasProblem => Variable.Problem != null;

        private string Hint
        {
            get
            {
                switch (Type)
                {
                    case VariableType.Date: return "Type a date, or pick one from the calendar.";
                    case VariableType.Time: return "Type a time such as 14:30, 1430 or 2:30 pm.";
                    case VariableType.DateTime: return "Pick a date, then type a time such as 14:30.";
                    case VariableType.Link: return "Type a web address such as https://example.com.";
                    case VariableType.Email: return "Type an email address.";
                    case VariableType.Number: return "Type a number.";
                    default: return "";
                }
            }
        }

        /// <summary>The value the date box shows after a calendar pick; typed input accepts other forms too.</summary>
        public void SetDate(DateTime date)
        {
            Value = date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        public DateTime? ParsedDate => VariableFormatter.TryParseDate(Value, out var date) ? date : (DateTime?)null;

        public string CountText => Variable.Count == 1 ? "1 place" : Variable.Count + " places";

        /// <summary>Where the tokens are and how they are spelled, e.g. "Subject, body · {{Tail}} [[tail]]".</summary>
        public string Detail
        {
            get
            {
                var locations = string.Join(", ", Variable.Locations.Select(Describe).Distinct());
                var spellings = Variable.Spellings.Take(4).ToList();
                return locations + " · " + string.Join("  ", spellings) + (Variable.Spellings.Count() > 4 ? " …" : "");
            }
        }

        public string AutomationName => "Value for " + Name;
        public string DateAutomationName => "Date for " + Name;
        public string TimeAutomationName => "Time for " + Name;
        public string LinkTextAutomationName => "Text to show for " + Name;
        public string TypeAutomationName => "Type of " + Name + ": " + TypeText;
        public string FormatAutomationName => "Format of " + Name + ": " + FormatText;
        public string CalendarAutomationName => "Pick a date for " + Name;

        private static string Describe(TemplateLocation location)
        {
            switch (location)
            {
                case TemplateLocation.Subject: return "Subject";
                case TemplateLocation.HtmlBody: return "Body";
                case TemplateLocation.HtmlLink: return "Link";
                case TemplateLocation.PlainBody: return "Text body";
                default: return location.ToString();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(string current, string? value, Action<string> assign, string name)
        {
            value = value ?? "";
            if (current == value) return;
            assign(value);
            Raise(name);
            RaiseResult();
        }

        private void RaiseResult()
        {
            Raise(nameof(IsEmpty));
            Raise(nameof(ResultText));
            Raise(nameof(HasResult));
            Raise(nameof(HasProblem));
            Raise(nameof(ShowResultRow));
        }

        private void RaiseAll()
        {
            // Value is raised too, so the main window re-renders on a type or format change.
            foreach (var name in new[]
            {
                nameof(Type), nameof(TypeText), nameof(FormatId), nameof(FormatText), nameof(Formats),
                nameof(ShowTextBox), nameof(IsMultiLine), nameof(ShowDate), nameof(ShowTime), nameof(ShowLinkText),
                nameof(ShowFormat), nameof(ShowResultRow), nameof(TypeAutomationName), nameof(FormatAutomationName), nameof(Value),
            })
                Raise(name);

            RaiseResult();
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
