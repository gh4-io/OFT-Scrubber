using System;
using System.IO;
using System.Text;

namespace OftScrubber.Core.Oft
{
    /// <summary>Decompresses PR_RTF_COMPRESSED as specified by MS-OXRTFCP (LZFu).</summary>
    internal static class RtfCompressed
    {
        public const int MaxOutputSize = 50 * 1024 * 1024;

        private const uint Compressed = 0x75465A4C;   // "LZFu"
        private const uint Uncompressed = 0x414C454D; // "MELA"

        // The dictionary is preloaded with this 207-byte string; the line break is CR LF.
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes(
            "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript " +
            "\\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n" +
            "\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

        /// <summary>Returns the raw RTF bytes. Throws InvalidDataException when the data is corrupt.</summary>
        public static byte[] Decompress(byte[] data)
        {
            if (Prefix.Length != 207) throw new InvalidOperationException("RTF dictionary prefix has the wrong length.");
            if (data.Length < 16) throw new InvalidDataException("Compressed RTF header is truncated.");

            uint compSize = BitConverter.ToUInt32(data, 0);
            uint rawSize = BitConverter.ToUInt32(data, 4);
            uint compType = BitConverter.ToUInt32(data, 8);
            if (rawSize > MaxOutputSize) throw new InvalidDataException("Compressed RTF is too large.");

            if (compType == Uncompressed)
            {
                int length = (int)Math.Min(rawSize, (uint)(data.Length - 16));
                byte[] raw = new byte[length];
                Buffer.BlockCopy(data, 16, raw, 0, length);
                return raw;
            }
            if (compType != Compressed) throw new InvalidDataException("Unknown compressed RTF type.");

            // COMPSIZE counts the bytes that follow the COMPSIZE field itself.
            long end = Math.Min(data.Length, 4L + compSize);
            byte[] dictionary = new byte[4096];
            Buffer.BlockCopy(Prefix, 0, dictionary, 0, Prefix.Length);
            int write = Prefix.Length;

            MemoryStream output = new MemoryStream((int)Math.Min(rawSize, 1024 * 1024));
            int pos = 16;
            while (pos < end)
            {
                int control = data[pos++];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (pos >= end) break;
                    if ((control & (1 << bit)) == 0)
                    {
                        byte b = data[pos++];
                        output.WriteByte(b);
                        dictionary[write] = b;
                        write = (write + 1) & 0xFFF;
                    }
                    else
                    {
                        if (pos + 1 >= end) throw new InvalidDataException("Compressed RTF reference is truncated.");
                        int word = (data[pos] << 8) | data[pos + 1];
                        pos += 2;
                        int offset = word >> 4;
                        int length = (word & 0xF) + 2;
                        if (offset == write) return Finish(output, rawSize);
                        for (int i = 0; i < length; i++)
                        {
                            byte b = dictionary[(offset + i) & 0xFFF];
                            output.WriteByte(b);
                            dictionary[write] = b;
                            write = (write + 1) & 0xFFF;
                        }
                    }
                }
                if (output.Length > MaxOutputSize) throw new InvalidDataException("Compressed RTF expands beyond the size limit.");
            }
            return Finish(output, rawSize);
        }

        private static byte[] Finish(MemoryStream output, uint rawSize)
        {
            byte[] result = output.ToArray();
            if (result.Length <= rawSize) return result;
            byte[] trimmed = new byte[rawSize];
            Buffer.BlockCopy(result, 0, trimmed, 0, (int)rawSize);
            return trimmed;
        }
    }
}
