using System;
using System.Collections.Generic;
using System.Text;
using OftScrubber.Core.Oft;

namespace OftScrubber.Core.Cfb
{
    /// <summary>A stream inside a compound file. Data is read on demand through its storage.</summary>
    public sealed class CfbStreamEntry
    {
        internal CfbStreamEntry(string name, uint startSector, long size)
        {
            Name = name;
            StartSector = startSector;
            Size = size;
        }

        public string Name { get; }
        public uint StartSector { get; }
        public long Size { get; }
    }

    /// <summary>A storage (folder) inside a compound file.</summary>
    public sealed class CfbStorage
    {
        private readonly CompoundFile _file;

        internal CfbStorage(CompoundFile file, string name, Guid clsid)
        {
            _file = file;
            Name = name;
            Clsid = clsid;
        }

        public string Name { get; }
        public Guid Clsid { get; }
        public Dictionary<string, CfbStorage> Storages { get; } = new Dictionary<string, CfbStorage>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CfbStreamEntry> Streams { get; } = new Dictionary<string, CfbStreamEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns the stream contents, or null when this storage has no stream of that name.</summary>
        public byte[]? ReadStream(string name)
        {
            CfbStreamEntry? entry;
            if (!Streams.TryGetValue(name, out entry)) return null;
            return _file.ReadStreamData(entry);
        }
    }

    /// <summary>
    /// Read-only MS-CFB (OLE compound file) reader over an in-memory byte array. The input is
    /// treated as untrusted: every offset is bounds-checked and every chain walk is capped.
    /// </summary>
    public sealed class CompoundFile
    {
        public const long MaxFileSize = 200L * 1024 * 1024;

        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FreeSect = 0xFFFFFFFF;
        private const uint NoStream = 0xFFFFFFFF;
        private const int MaxDepth = 64;
        private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        private readonly byte[] _data;
        private readonly int _sectorSize;
        private readonly bool _isV3;
        private readonly uint _miniCutoff;
        private uint[] _fat = new uint[0];
        private uint[] _miniFat = new uint[0];
        private byte[] _miniStream = new byte[0];

        // Directory entries may share sectors, so a small crafted file could otherwise expand
        // into gigabytes of stream reads. Honest files read each sector about once.
        private long _readBudget;

        private CompoundFile(byte[] data)
        {
            _data = data;
            _readBudget = 4L * data.Length + 1024 * 1024;
            if (data.Length < 512) throw NotCfb();
            for (int i = 0; i < Signature.Length; i++)
            {
                if (data[i] != Signature[i]) throw NotCfb();
            }

            ushort major = ReadUInt16(0x1A);
            ushort shift = ReadUInt16(0x1E);
            if (major == 3 && shift == 9) _sectorSize = 512;
            else if (major == 4 && shift == 12) _sectorSize = 4096;
            else throw Damaged("unsupported compound file version");
            _isV3 = major == 3;
            if (ReadUInt16(0x20) != 6) throw Damaged("unexpected mini sector size");
            _miniCutoff = ReadUInt32(0x38);
            if (_miniCutoff != 4096) throw Damaged("unexpected mini stream cutoff");
        }

        /// <summary>Parses the compound file and returns its root storage.</summary>
        public static CfbStorage Open(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > MaxFileSize)
            {
                throw new OftFormatException("This file is too large to be an Outlook template or message (over 200 MB).");
            }

            CompoundFile file = new CompoundFile(data);
            return file.Load();
        }

        private CfbStorage Load()
        {
            BuildFat();

            byte[] dir = ReadChain(ReadUInt32(0x30), long.MaxValue, _fat, _sectorSize, null);
            int entryCount = dir.Length / 128;
            if (entryCount == 0) throw Damaged("the directory is empty");
            if (dir[0x42] != 5) throw Damaged("the root entry is missing");

            DirEntry root = ReadEntry(dir, 0);
            _miniFat = ToUInts(ReadChain(ReadUInt32(0x3C), long.MaxValue, _fat, _sectorSize, null));
            if (root.Size > 0)
            {
                _miniStream = ReadChain(root.Start, root.Size, _fat, _sectorSize, null);
            }

            CfbStorage rootStorage = new CfbStorage(this, root.Name, root.Clsid);
            HashSet<uint> visited = new HashSet<uint> { 0 };
            AddChildren(rootStorage, root.Child, dir, entryCount, visited, 0);
            return rootStorage;
        }

        private void BuildFat()
        {
            uint fatSectorCount = ReadUInt32(0x2C);
            int maxSectors = _data.Length / _sectorSize + 1;
            if (fatSectorCount > maxSectors) throw Damaged("the allocation table is larger than the file");

            List<uint> fatSectors = new List<uint>();
            for (int i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
            {
                fatSectors.Add(ReadUInt32(0x4C + i * 4));
            }

            uint difat = ReadUInt32(0x44);
            int perDifat = _sectorSize / 4 - 1;
            int guard = 0;
            while (fatSectors.Count < fatSectorCount && difat != EndOfChain && difat != FreeSect)
            {
                if (++guard > maxSectors) throw Damaged("the allocation table index loops");
                int offset = SectorOffset(difat);
                for (int i = 0; i < perDifat && fatSectors.Count < fatSectorCount; i++)
                {
                    fatSectors.Add(ReadUInt32(offset + i * 4));
                }
                difat = ReadUInt32(offset + perDifat * 4);
            }

            if (fatSectors.Count < fatSectorCount) throw Damaged("the allocation table is incomplete");

            int perSector = _sectorSize / 4;
            uint[] fat = new uint[fatSectors.Count * perSector];
            for (int s = 0; s < fatSectors.Count; s++)
            {
                int offset = SectorOffset(fatSectors[s]);
                for (int i = 0; i < perSector; i++)
                {
                    fat[s * perSector + i] = ReadUInt32(offset + i * 4);
                }
            }
            _fat = fat;
        }

        private void AddChildren(CfbStorage parent, uint firstSid, byte[] dir, int entryCount, HashSet<uint> visited, int depth)
        {
            if (depth > MaxDepth) throw Damaged("storages are nested too deeply");

            Stack<uint> pending = new Stack<uint>();
            if (firstSid != NoStream) pending.Push(firstSid);
            while (pending.Count > 0)
            {
                uint sid = pending.Pop();
                if (sid >= entryCount) throw Damaged("a directory entry points outside the directory");
                if (!visited.Add(sid)) throw Damaged("the directory tree loops");

                DirEntry entry = ReadEntry(dir, (int)sid);
                if (entry.Left != NoStream) pending.Push(entry.Left);
                if (entry.Right != NoStream) pending.Push(entry.Right);

                if (entry.Type == 1)
                {
                    CfbStorage child = new CfbStorage(this, entry.Name, entry.Clsid);
                    parent.Storages[entry.Name] = child;
                    AddChildren(child, entry.Child, dir, entryCount, visited, depth + 1);
                }
                else if (entry.Type == 2)
                {
                    parent.Streams[entry.Name] = new CfbStreamEntry(entry.Name, entry.Start, entry.Size);
                }
            }
        }

        internal byte[] ReadStreamData(CfbStreamEntry entry)
        {
            if (entry.Size == 0) return new byte[0];
            _readBudget -= entry.Size;
            if (_readBudget < 0) throw Damaged("its streams overlap far more than a real message allows");
            if (entry.Size < _miniCutoff)
            {
                return ReadChain(entry.StartSector, entry.Size, _miniFat, 64, _miniStream);
            }
            return ReadChain(entry.StartSector, entry.Size, _fat, _sectorSize, null);
        }

        /// <summary>
        /// Follows a sector chain and concatenates its sectors. When source is null the sectors
        /// are regular file sectors; otherwise they are 64-byte slices of the mini stream.
        /// </summary>
        private byte[] ReadChain(uint start, long size, uint[] table, int sectorSize, byte[]? source)
        {
            if (start == EndOfChain || start == FreeSect)
            {
                if (size == long.MaxValue || size == 0) return new byte[0];
                throw Damaged("a stream has no data");
            }

            long limit = source == null ? _data.Length : source.Length;
            if (size != long.MaxValue && size > limit) throw Damaged("a stream is larger than the file");

            // A valid chain can never hold more sectors than the file (or mini stream) contains.
            long maxSectors = limit / sectorSize + 1;
            List<uint> chain = new List<uint>();
            uint sector = start;
            while (sector != EndOfChain)
            {
                if (sector >= table.Length) throw Damaged("a sector chain points outside the file");
                if (chain.Count >= maxSectors) throw Damaged("a sector chain loops");
                chain.Add(sector);
                if (size != long.MaxValue && (long)chain.Count * sectorSize >= size) break;
                sector = table[sector];
            }

            long total = (long)chain.Count * sectorSize;
            long length = size == long.MaxValue ? total : size;
            if (length > total) throw Damaged("a stream is shorter than its recorded size");
            if (length > limit) throw Damaged("a stream is larger than the file");

            byte[] result = new byte[length];
            long written = 0;
            foreach (uint s in chain)
            {
                long offset = source == null ? SectorOffset(s) : (long)s * sectorSize;
                int count = (int)Math.Min(sectorSize, length - written);
                byte[] from = source ?? _data;
                // The last sector of a file may be truncated; copy what exists and leave zeros.
                int available = (int)Math.Max(0, Math.Min(count, from.Length - offset));
                if (available < count && written + count < length) throw Damaged("a sector lies past the end of the file");
                if (available > 0) Buffer.BlockCopy(from, (int)offset, result, (int)written, available);
                written += count;
                if (written >= length) break;
            }
            return result;
        }

        private DirEntry ReadEntry(byte[] dir, int index)
        {
            int o = index * 128;
            int nameLength = BitConverter.ToUInt16(dir, o + 0x40);
            if (nameLength > 64) nameLength = 64;
            string name = nameLength >= 2 ? Encoding.Unicode.GetString(dir, o, nameLength - 2) : "";
            name = name.TrimEnd('\0');

            byte[] clsid = new byte[16];
            Buffer.BlockCopy(dir, o + 0x50, clsid, 0, 16);

            long size = _isV3 ? BitConverter.ToUInt32(dir, o + 0x78) : BitConverter.ToInt64(dir, o + 0x78);
            if (size < 0) throw Damaged("a stream has an invalid size");

            return new DirEntry
            {
                Name = name,
                Type = dir[o + 0x42],
                Left = BitConverter.ToUInt32(dir, o + 0x44),
                Right = BitConverter.ToUInt32(dir, o + 0x48),
                Child = BitConverter.ToUInt32(dir, o + 0x4C),
                Clsid = new Guid(clsid),
                Start = BitConverter.ToUInt32(dir, o + 0x74),
                Size = size,
            };
        }

        private int SectorOffset(uint sector)
        {
            long offset = ((long)sector + 1) * _sectorSize;
            if (sector >= 0xFFFFFFFA || offset >= _data.Length) throw Damaged("a sector lies past the end of the file");
            return (int)offset;
        }

        private ushort ReadUInt16(int offset)
        {
            if (offset < 0 || offset + 2 > _data.Length) throw Damaged("unexpected end of file");
            return BitConverter.ToUInt16(_data, offset);
        }

        private uint ReadUInt32(int offset)
        {
            if (offset < 0 || offset + 4 > _data.Length) throw Damaged("unexpected end of file");
            return BitConverter.ToUInt32(_data, offset);
        }

        private static uint[] ToUInts(byte[] bytes)
        {
            uint[] result = new uint[bytes.Length / 4];
            for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToUInt32(bytes, i * 4);
            return result;
        }

        private static OftFormatException NotCfb()
        {
            return new OftFormatException("This file is not an Outlook template or message (it is not a Compound File).");
        }

        private static OftFormatException Damaged(string detail)
        {
            return new OftFormatException("This Outlook file is damaged and cannot be read (" + detail + ").");
        }

        private sealed class DirEntry
        {
            public string Name = "";
            public byte Type;
            public uint Left;
            public uint Right;
            public uint Child;
            public Guid Clsid;
            public uint Start;
            public long Size;
        }
    }
}
