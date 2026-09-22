using System.Collections.Generic;
using OftScrubber.Core.Model;

namespace OftScrubber.Core.Output
{
    /// <summary>
    /// Where a completed message goes. Rendering never depends on the destination, so new
    /// adapters (an Outlook draft through automation, Microsoft Graph) slot in beside the .eml
    /// draft without touching parsing or templating.
    ///
    /// An adapter must never send mail on its own: sending is always a separate, explicit user
    /// action, either in the mail app or behind a clearly labelled command.
    /// </summary>
    public interface IEmailOutput
    {
        string DisplayName { get; }

        OutputResult Create(RenderedEmail email);
    }

    public sealed class OutputResult
    {
        public OutputResult(string location, List<string> notes)
        {
            Location = location;
            Notes = notes;
        }

        /// <summary>Where the result can be found, e.g. a file path.</summary>
        public string Location { get; }

        /// <summary>Anything the user should know, e.g. recipients that could not be carried over.</summary>
        public List<string> Notes { get; }
    }
}
