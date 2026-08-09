using System.Buffers.Binary;
using System.Text;
using CloudEmuera.Application.GamePackages;

namespace CloudEmuera.Infrastructure.GamePackages;

internal sealed record ValidatedZipEntry(
    string RawName,
    ushort Flags,
    ushort Method,
    uint Crc32,
    long CompressedBytes,
    long ExpandedBytes,
    uint ExternalAttributes,
    long LocalHeaderOffset,
    long DataOffset);

internal static class ZipStructureInspector
{
    private const uint EndSignature = 0x06054b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private const uint DataDescriptorSignature = 0x08074b50;

    public static IReadOnlyList<ValidatedZipEntry> Inspect(string archivePath, GamePackageIngestionLimits limits)
    {
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 22) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP end record is missing.");
        int tailLength = checked((int)Math.Min(stream.Length, 65_557));
        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        int eocd = FindEndRecord(tail);
        if (eocd < 0) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP end record is missing.");
        ReadOnlySpan<byte> end = tail.AsSpan(eocd);
        ushort disk = U16(end, 4);
        ushort centralDisk = U16(end, 6);
        ushort entriesOnDisk = U16(end, 8);
        ushort entries = U16(end, 10);
        uint centralBytes = U32(end, 12);
        uint centralOffset = U32(end, 16);
        ushort commentBytes = U16(end, 20);
        long endAbsolute = stream.Length - tailLength + eocd;
        if (endAbsolute + 22L + commentBytes != stream.Length)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "ZIP trailing data is forbidden.");
        if (entries == ushort.MaxValue || centralBytes == uint.MaxValue || centralOffset == uint.MaxValue)
            Reject(GamePackageRejectionCodes.Zip64Unsupported, "ZIP64 is unsupported.");
        if (disk != 0 || centralDisk != 0 || entriesOnDisk != entries)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP is unsupported.");
        if (entries > limits.MaxEntryCount) Reject(GamePackageRejectionCodes.EntryCountExceeded, "ZIP entry count exceeds the configured limit.");
        if (centralBytes > limits.MaxCentralDirectoryBytes)
            Reject(GamePackageRejectionCodes.CentralDirectoryTooLarge, "ZIP central directory exceeds the configured limit.");
        if (centralOffset + (long)centralBytes != endAbsolute || centralOffset < 0)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory is invalid.");

        var result = new List<ValidatedZipEntry>(entries);
        var dataRanges = new List<(long Start, long End)>();
        byte[] headerBuffer = new byte[46];
        byte[] localBuffer = new byte[30];
        stream.Position = centralOffset;
        for (int index = 0; index < entries; index++)
        {
            Span<byte> header = headerBuffer;
            stream.ReadExactly(header);
            if (U32(header, 0) != CentralSignature) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central header is invalid.");
            ushort madeBy = U16(header, 4);
            ushort flags = U16(header, 8);
            ushort method = U16(header, 10);
            uint crc = U32(header, 16);
            uint compressed = U32(header, 20);
            uint expanded = U32(header, 24);
            ushort nameLength = U16(header, 28);
            ushort extraLength = U16(header, 30);
            ushort entryCommentLength = U16(header, 32);
            ushort startDisk = U16(header, 34);
            uint external = U32(header, 38);
            uint localOffset = U32(header, 42);
            if (compressed == uint.MaxValue || expanded == uint.MaxValue || localOffset == uint.MaxValue)
                Reject(GamePackageRejectionCodes.Zip64Unsupported, "ZIP64 entry is unsupported.");
            if (startDisk != 0) Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP entry is unsupported.");
            if ((flags & 0x1) != 0) Reject(GamePackageRejectionCodes.ArchiveEncrypted, "Encrypted ZIP entry is unsupported.");
            if (method is not (0 or 8)) Reject(GamePackageRejectionCodes.ZipMethodUnsupported, "ZIP compression method is unsupported.");
            if (nameLength == 0) Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry name is empty.");
            byte[] nameBytes = new byte[nameLength];
            stream.ReadExactly(nameBytes);
            byte[] extra = new byte[extraLength];
            stream.ReadExactly(extra);
            if (entryCommentLength > 0) stream.Position += entryCommentLength;
            string? unicodeName = ValidateExtra(extra, nameBytes);
            string name = DecodeName(nameBytes, flags, unicodeName);
            ValidateUnixType(madeBy, external, name);

            long saved = stream.Position;
            stream.Position = localOffset;
            Span<byte> local = localBuffer;
            stream.ReadExactly(local);
            if (U32(local, 0) != LocalSignature || U16(local, 6) != flags || U16(local, 8) != method)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local header differs from central header.");
            if ((flags & 0x8) == 0)
            {
                if (U32(local, 14) != crc || U32(local, 18) != compressed || U32(local, 22) != expanded)
                    Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local CRC or size differs from central header.");
            }
            else if ((U32(local, 14) != 0 && U32(local, 14) != crc)
                || (U32(local, 18) != 0 && U32(local, 18) != compressed)
                || (U32(local, 22) != 0 && U32(local, 22) != expanded))
            {
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local deferred CRC or size conflicts with the central header.");
            }
            ushort localNameLength = U16(local, 26);
            ushort localExtraLength = U16(local, 28);
            byte[] localName = new byte[localNameLength];
            stream.ReadExactly(localName);
            if (!localName.AsSpan().SequenceEqual(nameBytes)) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local entry name differs from central entry name.");
            byte[] localExtra = new byte[localExtraLength];
            stream.ReadExactly(localExtra);
            string? localUnicodeName = ValidateExtra(localExtra, localName);
            if (localUnicodeName is not null && !string.Equals(localUnicodeName, name, StringComparison.Ordinal))
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local Unicode path differs from the central entry name.");
            long dataOffset = stream.Position;
            long dataEnd = checked(dataOffset + compressed);
            if (dataOffset < 0 || dataEnd > centralOffset) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry data is out of range.");
            if ((flags & 0x8) != 0)
            {
                stream.Position = dataEnd;
                Span<byte> descriptor = new byte[16];
                if (centralOffset - dataEnd < 12) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor is truncated.");
                stream.ReadExactly(descriptor[..4]);
                bool signature = U32(descriptor, 0) == DataDescriptorSignature;
                int remainder = signature ? 12 : 8;
                if (centralOffset - stream.Position < remainder) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor is truncated.");
                stream.ReadExactly(descriptor.Slice(4, remainder));
                int values = signature ? 4 : 0;
                if (U32(descriptor, values) != crc || U32(descriptor, values + 4) != compressed || U32(descriptor, values + 8) != expanded)
                    Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor differs from the central header.");
                dataEnd = stream.Position;
            }
            dataRanges.Add((localOffset, dataEnd));
            stream.Position = saved;
            result.Add(new(name, flags, method, crc, compressed, expanded, external, localOffset, dataOffset));
        }
        if (stream.Position != centralOffset + centralBytes) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory length is inconsistent.");
        (long Start, long End)[] orderedRanges = dataRanges.OrderBy(range => range.Start).ToArray();
        for (int index = 1; index < orderedRanges.Length; index++)
        {
            if (orderedRanges[index - 1].End > orderedRanges[index].Start) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry ranges overlap.");
        }
        return result;
    }

    private static int FindEndRecord(ReadOnlySpan<byte> bytes)
    {
        for (int i = bytes.Length - 22; i >= 0; i--)
            if (U32(bytes, i) == EndSignature) return i;
        return -1;
    }

    private static string DecodeName(byte[] bytes, ushort flags, string? unicodeName)
    {
        try
        {
            if ((flags & 0x800) != 0)
            {
                string utf8 = new UTF8Encoding(false, true).GetString(bytes);
                if (unicodeName is not null && !string.Equals(unicodeName, utf8, StringComparison.Ordinal))
                    Reject(GamePackageRejectionCodes.PathInvalid, "ZIP Unicode path conflicts with its UTF-8 name.");
                return utf8;
            }
            if (unicodeName is not null) return unicodeName;
            if (bytes.Any(value => value > 0x7f)) Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry names without the UTF-8 flag must be ASCII.");
            return Encoding.ASCII.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "ZIP entry name is not valid UTF-8.", innerException: exception);
        }
    }

    private static string? ValidateExtra(ReadOnlySpan<byte> extra, ReadOnlySpan<byte> rawName)
    {
        int offset = 0;
        var ids = new HashSet<ushort>();
        string? unicodePath = null;
        while (offset < extra.Length)
        {
            if (extra.Length - offset < 4) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field is truncated.");
            ushort id = U16(extra, offset);
            ushort length = U16(extra, offset + 2);
            offset += 4;
            if (length > extra.Length - offset) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field length is invalid.");
            if (!ids.Add(id)) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field is duplicated.");
            if (id == 0x0001) Reject(GamePackageRejectionCodes.Zip64Unsupported, "ZIP64 extra field is unsupported.");
            if (id is 0x000d or 0x5855 or 0x756e) Reject(GamePackageRejectionCodes.LinkEntryForbidden, "Unix link metadata is forbidden.");
            if (id == 0x7075)
            {
                ReadOnlySpan<byte> value = extra.Slice(offset, length);
                if (value.Length < 6 || value[0] != 1 || U32(value, 1) != ComputeCrc32(rawName))
                    Reject(GamePackageRejectionCodes.PathInvalid, "ZIP Unicode path extra field is invalid.");
                try { unicodePath = new UTF8Encoding(false, true).GetString(value[5..]); }
                catch (DecoderFallbackException exception)
                {
                    throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "ZIP Unicode path is not valid UTF-8.", innerException: exception);
                }
            }
            offset += length;
        }
        return unicodePath;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return crc ^ uint.MaxValue;
    }

    private static void ValidateUnixType(ushort madeBy, uint external, string name)
    {
        int creator = madeBy >> 8;
        if (creator != 3) return;
        int type = (int)((external >> 16) & 0xF000);
        if (type == 0) return;
        bool directory = name.EndsWith('/');
        if (type == 0xA000) Reject(GamePackageRejectionCodes.LinkEntryForbidden, "Symbolic links are forbidden.");
        if (type != (directory ? 0x4000 : 0x8000)) Reject(GamePackageRejectionCodes.SpecialEntryForbidden, "Special ZIP entries are forbidden.");
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static void Reject(string code, string message) => throw new GamePackageIngestionException(code, message);
}
