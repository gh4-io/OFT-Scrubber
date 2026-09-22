using System;
using System.Collections.Generic;
using System.Text;
using OftScrubber.Core.Cfb;

namespace OftScrubber.Core.Oft
{
    /// <summary>
    /// MAPI properties of one .msg object (the message, a recipient or an attachment), as laid
    /// out by MS-OXMSG: fixed-size values inline in __properties_version1.0 and variable-size
    /// values in __substg1.0_IIIITTTT streams.
    /// </summary>
    internal sealed class MapiProperties
    {
        public const int TopLevelHeaderSize = 32;
        public const int EmbeddedHeaderSize = 24;
        public const int ChildHeaderSize = 8;

        private const ushort TypeInt32 = 0x0003;
        private const ushort TypeBoolean = 0x000B;
        private const ushort TypeString8 = 0x001E;
        private const ushort TypeUnicode = 0x001F;
        private const ushort TypeBinary = 0x0102;

        private readonly Dictionary<uint, byte[]> _inline = new Dictionary<uint, byte[]>();

        public MapiProperties(CfbStorage storage, int headerSize)
        {
            Storage = storage;
            byte[]? stream = storage.ReadStream("__properties_version1.0");
            HasPropertyStream = stream != null;
            if (stream == null) return;

            for (int offset = headerSize; offset + 16 <= stream.Length; offset += 16)
            {
                uint tag = BitConverter.ToUInt32(stream, offset);
                byte[] value = new byte[8];
                Buffer.BlockCopy(stream, offset + 8, value, 0, 8);
                _inline[tag] = value;
            }
        }

        public CfbStorage Storage { get; }
        public bool HasPropertyStream { get; }

        /// <summary>Code page used for 8-bit (PT_STRING8) strings.</summary>
        public int CodePage { get; set; } = 1252;

        public int? GetInt32(ushort id)
        {
            byte[]? value;
            if (!_inline.TryGetValue(Tag(id, TypeInt32), out value)) return null;
            return BitConverter.ToInt32(value, 0);
        }

        public bool? GetBoolean(ushort id)
        {
            byte[]? value;
            if (!_inline.TryGetValue(Tag(id, TypeBoolean), out value)) return null;
            return value[0] != 0;
        }

        /// <summary>Returns the Unicode or 8-bit string property, or null when absent.</summary>
        public string? GetString(ushort id)
        {
            byte[]? unicode = Storage.ReadStream(StreamName(id, TypeUnicode));
            if (unicode != null)
            {
                return Encoding.Unicode.GetString(unicode, 0, unicode.Length & ~1).TrimEnd('\0');
            }

            byte[]? ansi = Storage.ReadStream(StreamName(id, TypeString8));
            if (ansi != null) return GetEncoding(CodePage).GetString(ansi).TrimEnd('\0');
            return null;
        }

        public byte[]? GetBinary(ushort id)
        {
            return Storage.ReadStream(StreamName(id, TypeBinary));
        }

        public static string StreamName(ushort id, ushort type)
        {
            return "__substg1.0_" + id.ToString("X4") + type.ToString("X4");
        }

        /// <summary>Returns the encoding for a Windows code page, falling back to 1252 when it is unknown.</summary>
        public static Encoding GetEncoding(int codePage)
        {
            if (codePage > 0 && codePage < 65536)
            {
                try
                {
                    return Encoding.GetEncoding(codePage);
                }
                catch (ArgumentException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
            return Encoding.GetEncoding(1252);
        }

        private static uint Tag(ushort id, ushort type)
        {
            return ((uint)id << 16) | type;
        }
    }
}
