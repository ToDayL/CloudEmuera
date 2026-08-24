using System;
using System.Drawing;
using System.IO;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

/// <summary>
/// Reads the advance metrics from the one catalogued TTF bound to the current
/// headless runtime. libgdiplus can create the private FontFamily but its text
/// measurement path treats CJK glyphs as Latin-width glyphs, so it cannot be
/// the authority for structured physical layout.
/// </summary>
internal static class HeadlessFontMetrics
{
    private static FontTable current;

    public static void Configure(string fontPath, string familyName)
    {
        current = FontTable.Load(fontPath, familyName);
    }

    public static void Clear() => current = null;

    public static bool TryMeasure(ReadOnlySpan<char> text, Font font, out int width)
    {
        FontTable table = current;
        if (table is null || font is null || !string.Equals(font.FontFamily.Name, table.FamilyName, StringComparison.Ordinal))
        {
            width = 0;
            return false;
        }

        double totalUnits = 0;
        for (int index = 0; index < text.Length;)
        {
            int codePoint;
            char first = text[index++];
            if (char.IsHighSurrogate(first) && index < text.Length && char.IsLowSurrogate(text[index]))
                codePoint = char.ConvertToUtf32(first, text[index++]);
            else
                codePoint = first;

            totalUnits += table.GetDisplayAdvance(codePoint);
        }

        width = checked((int)Math.Ceiling(totalUnits * font.Size / table.UnitsPerEm));
        return true;
    }

    private sealed class FontTable
    {
        private readonly byte[] data;
        private readonly uint hmtxOffset;
        private readonly ushort numberOfHMetrics;
        private readonly ushort numberOfGlyphs;
        private readonly Cmap cmap;

        private FontTable(
            byte[] data,
            string familyName,
            ushort unitsPerEm,
            uint hmtxOffset,
            ushort numberOfHMetrics,
            ushort numberOfGlyphs,
            Cmap cmap)
        {
            this.data = data;
            FamilyName = familyName;
            UnitsPerEm = unitsPerEm;
            this.hmtxOffset = hmtxOffset;
            this.numberOfHMetrics = numberOfHMetrics;
            this.numberOfGlyphs = numberOfGlyphs;
            this.cmap = cmap;
        }

        public string FamilyName { get; }
        public ushort UnitsPerEm { get; }

        public static FontTable Load(string path, string familyName)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("The runtime font metrics path must be absolute.");
            if (string.IsNullOrWhiteSpace(familyName))
                throw new InvalidDataException("The runtime font metrics family name is empty.");

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12)
                throw new InvalidDataException("The runtime TTF header is truncated.");

            ushort tableCount = ReadUInt16(data, 4);
            long tableDirectoryEnd = 12L + tableCount * 16L;
            if (tableDirectoryEnd > data.Length)
                throw new InvalidDataException("The runtime TTF table directory is truncated.");

            var tables = new System.Collections.Generic.Dictionary<string, (uint Offset, uint Length)>(StringComparer.Ordinal);
            for (int index = 0; index < tableCount; index++)
            {
                int offset = checked(12 + index * 16);
                string tag = ReadTag(data, offset);
                uint tableOffset = ReadUInt32(data, offset + 8);
                uint tableLength = ReadUInt32(data, offset + 12);
                EnsureRange(data, tableOffset, tableLength, $"TTF table '{tag}'");
                tables[tag] = (tableOffset, tableLength);
            }

            (uint headOffset, uint headLength) = RequireTable(tables, "head");
            if (headLength < 20)
                throw new InvalidDataException("The runtime TTF head table is truncated.");
            ushort unitsPerEm = ReadUInt16(data, checked((int)headOffset + 18));
            if (unitsPerEm == 0)
                throw new InvalidDataException("The runtime TTF unitsPerEm is zero.");

            (uint maxpOffset, uint maxpLength) = RequireTable(tables, "maxp");
            if (maxpLength < 6)
                throw new InvalidDataException("The runtime TTF maxp table is truncated.");
            ushort numberOfGlyphs = ReadUInt16(data, checked((int)maxpOffset + 4));
            if (numberOfGlyphs == 0)
                throw new InvalidDataException("The runtime TTF has no glyphs.");

            (uint hheaOffset, uint hheaLength) = RequireTable(tables, "hhea");
            if (hheaLength < 36)
                throw new InvalidDataException("The runtime TTF hhea table is truncated.");
            ushort numberOfHMetrics = ReadUInt16(data, checked((int)hheaOffset + 34));
            if (numberOfHMetrics == 0 || numberOfHMetrics > numberOfGlyphs)
                throw new InvalidDataException("The runtime TTF horizontal metric count is invalid.");

            (uint hmtxOffset, uint hmtxLength) = RequireTable(tables, "hmtx");
            long requiredHmtxLength = (long)numberOfHMetrics * 4 + (numberOfGlyphs - numberOfHMetrics) * 2L;
            if (requiredHmtxLength > hmtxLength)
                throw new InvalidDataException("The runtime TTF hmtx table is truncated.");

            (uint cmapOffset, uint cmapLength) = RequireTable(tables, "cmap");
            Cmap cmap = Cmap.Load(data, cmapOffset, cmapLength);
            return new FontTable(data, familyName, unitsPerEm, hmtxOffset, numberOfHMetrics, numberOfGlyphs, cmap);
        }

        public ushort GetGlyph(int codePoint) => cmap.GetGlyph(codePoint);

        public ushort GetAdvance(ushort glyph)
        {
            if (glyph >= numberOfGlyphs)
                glyph = 0;
            int metricIndex = Math.Min(glyph, (ushort)(numberOfHMetrics - 1));
            return ReadUInt16(data, checked((int)hmtxOffset + metricIndex * 4));
        }

        public ushort GetDisplayAdvance(int codePoint)
        {
            ushort nativeAdvance = GetAdvance(GetGlyph(codePoint));
            if (!UsesEraWideCell(codePoint))
                return nativeAdvance;

            // Japanese Era maps use these East-Asian-width ambiguous glyphs
            // as one CJK cell. The bundled coding faces encode them as a
            // half-cell, unlike the Japanese Windows fonts targeted by those
            // maps. Preserve a future face's already-wide metric; otherwise
            // use U+3000 as the selected face's CJK-cell authority.
            return Math.Max(nativeAdvance, GetAdvance(GetGlyph(0x3000)));
        }

        private static bool UsesEraWideCell(int codePoint) =>
            codePoint == 0x2015 || // HORIZONTAL BAR
            codePoint == 0x2225 || // PARALLEL TO
            codePoint is >= 0x2500 and <= 0x257F || // Box Drawing
            codePoint is 0x25A0 or 0x25A1 or 0x25CB or 0x25CF or 0x2605 or 0x2606;

        private static (uint Offset, uint Length) RequireTable(
            System.Collections.Generic.IReadOnlyDictionary<string, (uint Offset, uint Length)> tables,
            string tag) => tables.TryGetValue(tag, out (uint Offset, uint Length) table)
                ? table
                : throw new InvalidDataException($"The runtime TTF is missing the '{tag}' table.");
    }

    private abstract class Cmap
    {
        public static Cmap Load(byte[] data, uint offset, uint length)
        {
            int start = checked((int)offset);
            EnsureRange(data, offset, length, "TTF cmap table");
            if (length < 4)
                throw new InvalidDataException("The runtime TTF cmap table is truncated.");

            ushort subtableCount = ReadUInt16(data, start + 2);
            long recordsEnd = start + 4L + subtableCount * 8L;
            if (recordsEnd > start + length || recordsEnd > data.Length)
                throw new InvalidDataException("The runtime TTF cmap records are truncated.");

            var candidates = new System.Collections.Generic.List<(int Format, uint Offset, ushort Platform, ushort Encoding)>();
            for (int index = 0; index < subtableCount; index++)
            {
                int record = checked(start + 4 + index * 8);
                ushort platform = ReadUInt16(data, record);
                ushort encoding = ReadUInt16(data, record + 2);
                uint subtableOffset = ReadUInt32(data, record + 4);
                if (subtableOffset >= length || subtableOffset > int.MaxValue)
                    continue;
                int subtable = checked(start + (int)subtableOffset);
                if (subtable + 2 > start + length)
                    continue;
                candidates.Add((ReadUInt16(data, subtable), subtableOffset, platform, encoding));
            }

            foreach (int wantedFormat in new[] { 12, 13, 4 })
            {
                foreach ((int format, uint subtableOffset, ushort platform, ushort encoding) in candidates)
                {
                    if (format != wantedFormat || !IsUnicodeCandidate(platform, encoding))
                        continue;
                    int subtable = checked(start + (int)subtableOffset);
                    try
                    {
                        return wantedFormat switch
                        {
                            4 => CmapFormat4.Parse(data, subtable, start + checked((int)length)),
                            12 => CmapFormat12.Parse(data, subtable, start + checked((int)length), constantGlyph: false),
                            13 => CmapFormat12.Parse(data, subtable, start + checked((int)length), constantGlyph: true),
                            _ => throw new InvalidDataException("Unsupported runtime TTF cmap format."),
                        };
                    }
                    catch (InvalidDataException)
                    {
                        // Try another Unicode subtable before failing the font.
                    }
                }
            }

            throw new InvalidDataException("The runtime TTF has no supported Unicode cmap subtable.");
        }

        public abstract ushort GetGlyph(int codePoint);

        private static bool IsUnicodeCandidate(ushort platform, ushort encoding) =>
            platform == 0 || platform == 3 && encoding is 1 or 10;
    }

    private sealed class CmapFormat4 : Cmap
    {
        private readonly ushort[] endCodes;
        private readonly ushort[] startCodes;
        private readonly short[] idDeltas;
        private readonly ushort[] idRangeOffsets;
        private readonly int idRangeOffsetArray;
        private readonly byte[] data;

        private CmapFormat4(byte[] data, ushort[] endCodes, ushort[] startCodes, short[] idDeltas, ushort[] idRangeOffsets, int idRangeOffsetArray)
        {
            this.data = data;
            this.endCodes = endCodes;
            this.startCodes = startCodes;
            this.idDeltas = idDeltas;
            this.idRangeOffsets = idRangeOffsets;
            this.idRangeOffsetArray = idRangeOffsetArray;
        }

        public static CmapFormat4 Parse(byte[] data, int offset, int limit)
        {
            if (offset + 14 > limit)
                throw new InvalidDataException("The runtime TTF cmap format 4 header is truncated.");
            ushort length = ReadUInt16(data, offset + 2);
            ushort segmentCountX2 = ReadUInt16(data, offset + 6);
            int segmentCount = segmentCountX2 / 2;
            int endCodes = checked(offset + 14);
            int startCodes = checked(endCodes + segmentCount * 2 + 2);
            int idDeltas = checked(startCodes + segmentCount * 2);
            int idRangeOffsets = checked(idDeltas + segmentCount * 2);
            int tableEnd = checked(offset + length);
            if (segmentCount == 0 || segmentCountX2 % 2 != 0 || tableEnd > limit || idRangeOffsets + segmentCount * 2 > tableEnd)
                throw new InvalidDataException("The runtime TTF cmap format 4 ranges are truncated.");

            var end = new ushort[segmentCount];
            var start = new ushort[segmentCount];
            var delta = new short[segmentCount];
            var range = new ushort[segmentCount];
            for (int index = 0; index < segmentCount; index++)
            {
                end[index] = ReadUInt16(data, endCodes + index * 2);
                start[index] = ReadUInt16(data, startCodes + index * 2);
                delta[index] = ReadInt16(data, idDeltas + index * 2);
                range[index] = ReadUInt16(data, idRangeOffsets + index * 2);
                if (start[index] > end[index])
                    throw new InvalidDataException("The runtime TTF cmap format 4 range is reversed.");
            }
            return new CmapFormat4(data, end, start, delta, range, idRangeOffsets);
        }

        public override ushort GetGlyph(int codePoint)
        {
            if ((uint)codePoint > ushort.MaxValue)
                return 0;
            int low = 0;
            int high = endCodes.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (codePoint > endCodes[middle]) low = middle + 1;
                else if (codePoint < startCodes[middle]) high = middle - 1;
                else
                {
                    ushort range = idRangeOffsets[middle];
                    if (range == 0)
                        return unchecked((ushort)(codePoint + idDeltas[middle]));
                    int glyphAddress = checked(idRangeOffsetArray + middle * 2 + range + (codePoint - startCodes[middle]) * 2);
                    ushort glyph = ReadUInt16(data, glyphAddress);
                    return glyph == 0 ? (ushort)0 : unchecked((ushort)(glyph + idDeltas[middle]));
                }
            }
            return 0;
        }
    }

    private sealed class CmapFormat12 : Cmap
    {
        private readonly byte[] data;
        private readonly int groupsOffset;
        private readonly int groupCount;
        private readonly bool constantGlyph;

        private CmapFormat12(byte[] data, int groupsOffset, int groupCount, bool constantGlyph)
        {
            this.data = data;
            this.groupsOffset = groupsOffset;
            this.groupCount = groupCount;
            this.constantGlyph = constantGlyph;
        }

        public static CmapFormat12 Parse(byte[] data, int offset, int limit, bool constantGlyph)
        {
            if (offset + 16 > limit)
                throw new InvalidDataException("The runtime TTF cmap format 12 header is truncated.");
            uint length = ReadUInt32(data, offset + 4);
            uint groupCount = ReadUInt32(data, offset + 12);
            long tableEnd = offset + length;
            long groupsEnd = offset + 16L + groupCount * 12L;
            if (length < 16 || tableEnd > limit || groupsEnd > tableEnd || groupCount > int.MaxValue)
                throw new InvalidDataException("The runtime TTF cmap format 12 ranges are truncated.");
            return new CmapFormat12(data, offset + 16, (int)groupCount, constantGlyph);
        }

        public override ushort GetGlyph(int codePoint)
        {
            if (codePoint < 0)
                return 0;
            int low = 0;
            int high = groupCount - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int group = checked(groupsOffset + middle * 12);
                uint start = ReadUInt32(data, group);
                uint end = ReadUInt32(data, group + 4);
                if ((uint)codePoint < start) high = middle - 1;
                else if ((uint)codePoint > end) low = middle + 1;
                else
                {
                    uint glyph = ReadUInt32(data, group + 8);
                    if (!constantGlyph) glyph = checked(glyph + ((uint)codePoint - start));
                    return glyph <= ushort.MaxValue ? (ushort)glyph : (ushort)0;
                }
            }
            return 0;
        }
    }

    private static string ReadTag(byte[] data, int offset) =>
        new string(new[] { (char)data[offset], (char)data[offset + 1], (char)data[offset + 2], (char)data[offset + 3] });

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        EnsureRange(data, checked((uint)offset), 2, "TTF field");
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static short ReadInt16(byte[] data, int offset) => unchecked((short)ReadUInt16(data, offset));

    private static uint ReadUInt32(byte[] data, int offset)
    {
        EnsureRange(data, checked((uint)offset), 4, "TTF field");
        return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }

    private static void EnsureRange(byte[] data, uint offset, uint length, string description)
    {
        if (offset > data.Length || length > data.Length - offset)
            throw new InvalidDataException($"The {description} is outside the TTF file.");
    }
}
