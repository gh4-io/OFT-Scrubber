using System;

namespace OftScrubber.Core.Oft
{
    /// <summary>
    /// Thrown when a file cannot be read as an Outlook template or message. The message is
    /// written for the user and says what is wrong with the file.
    /// </summary>
    [Serializable]
    public sealed class OftFormatException : Exception
    {
        public OftFormatException(string message)
            : base(message)
        {
        }

        public OftFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
