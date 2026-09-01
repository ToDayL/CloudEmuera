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
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private const uint DataDescriptorSignature = 0x08074b50;
    private const ushort Zip64ExtraId = 0x0001;

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
        long endAbsolute = checked(stream.Length - tailLength + eocd);
        if (endAbsolute + 22L + commentBytes != stream.Length)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "ZIP trailing data is forbidden.");
        if (disk != 0 || centralDisk != 0)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP is unsupported.");

        bool zip64Sentinel = entriesOnDisk == ushort.MaxValue || entries == ushort.MaxValue
            || centralBytes == uint.MaxValue || centralOffset == uint.MaxValue;
        bool hasZip64Locator = TryReadZip64Locator(stream, endAbsolute, out Zip64Locator locator);
        if (zip64Sentinel && !hasZip64Locator)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 locator is missing.");

        ZipDirectoryMetadata directory = hasZip64Locator
            ? ReadZip64Directory(stream, endAbsolute, locator, entriesOnDisk, entries, centralBytes, centralOffset)
            : new(entries, centralBytes, centralOffset, endAbsolute);
        if (!hasZip64Locator && entriesOnDisk != entries)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP is unsupported.");
        if (directory.EntryCount > (ulong)limits.MaxEntryCount)
            Reject(GamePackageRejectionCodes.EntryCountExceeded, "ZIP entry count exceeds the configured limit.");
        if (directory.CentralDirectoryBytes > (ulong)limits.MaxCentralDirectoryBytes)
            Reject(GamePackageRejectionCodes.CentralDirectoryTooLarge, "ZIP central directory exceeds the configured limit.");

        long centralDirectoryOffset = ToInt64(directory.CentralDirectoryOffset, "ZIP central directory offset is too large.");
        long centralDirectoryBytes = ToInt64(directory.CentralDirectoryBytes, "ZIP central directory is too large.");
        if (centralDirectoryOffset < 0 || centralDirectoryOffset > stream.Length
            || centralDirectoryBytes > directory.CentralDirectoryEnd - centralDirectoryOffset
            || centralDirectoryOffset + centralDirectoryBytes != directory.CentralDirectoryEnd)
        {
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory is invalid.");
        }

        var result = new List<ValidatedZipEntry>(checked((int)directory.EntryCount));
        var dataRanges = new List<(long Start, long End)>(result.Capacity);
        byte[] headerBuffer = new byte[46];
        byte[] localBuffer = new byte[30];
        stream.Position = centralDirectoryOffset;
        for (int index = 0; index < result.Capacity; index++)
        {
            if (stream.Position < centralDirectoryOffset || directory.CentralDirectoryEnd - stream.Position < headerBuffer.Length)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory entry is truncated.");
            Span<byte> header = headerBuffer;
            stream.ReadExactly(header);
            if (U32(header, 0) != CentralSignature) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central header is invalid.");
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
            if (startDisk != 0 && startDisk != ushort.MaxValue)
                Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP entry is unsupported.");
            if ((flags & 0x1) != 0) Reject(GamePackageRejectionCodes.ArchiveEncrypted, "Encrypted ZIP entry is unsupported.");
            if (method is not (0 or 8)) Reject(GamePackageRejectionCodes.ZipMethodUnsupported, "ZIP compression method is unsupported.");
            if (nameLength == 0) Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry name is empty.");

            long variableEnd = checked(stream.Position + nameLength + extraLength + entryCommentLength);
            if (variableEnd > directory.CentralDirectoryEnd)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central entry exceeds the central directory.");
            byte[] nameBytes = new byte[nameLength];
            stream.ReadExactly(nameBytes);
            byte[] extra = new byte[extraLength];
            stream.ReadExactly(extra);
            ZipExtraInfo centralExtra = ValidateExtra(extra, nameBytes,
                expanded == uint.MaxValue, compressed == uint.MaxValue, localOffset == uint.MaxValue,
                startDisk == ushort.MaxValue);
            string name = DecodeName(nameBytes, flags, centralExtra.UnicodePath);
            long compressedBytes = ResolveZip64Value(compressed, centralExtra.CompressedBytes, "ZIP compressed size is invalid.");
            long expandedBytes = ResolveZip64Value(expanded, centralExtra.ExpandedBytes, "ZIP expanded size is invalid.");
            long localHeaderOffset = ResolveZip64Value(localOffset, centralExtra.LocalHeaderOffset, "ZIP local header offset is invalid.");
            uint startDiskNumber = startDisk == ushort.MaxValue
                ? centralExtra.StartDiskNumber ?? MissingZip64DiskValue("ZIP64 disk number is missing.")
                : startDisk;
            if (startDiskNumber != 0)
                Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP entry is unsupported.");
            stream.Position = variableEnd;

            long saved = stream.Position;
            if (localHeaderOffset < 0 || localHeaderOffset >= centralDirectoryOffset)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local header offset is out of range.");
            stream.Position = localHeaderOffset;
            Span<byte> local = localBuffer;
            stream.ReadExactly(local);
            if (U32(local, 0) != LocalSignature || U16(local, 6) != flags || U16(local, 8) != method)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local header differs from central header.");
            uint localCrc = U32(local, 14);
            uint localCompressed = U32(local, 18);
            uint localExpanded = U32(local, 22);
            bool deferred = (flags & 0x8) != 0;
            if ((!deferred && localCrc != crc) || (deferred && localCrc != 0 && localCrc != crc))
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local CRC differs from the central header.");
            ushort localNameLength = U16(local, 26);
            ushort localExtraLength = U16(local, 28);
            long localVariableEnd = checked(stream.Position + localNameLength + localExtraLength);
            if (localVariableEnd > centralDirectoryOffset)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local header exceeds the entry data area.");
            byte[] localName = new byte[localNameLength];
            stream.ReadExactly(localName);
            if (!localName.AsSpan().SequenceEqual(nameBytes))
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local entry name differs from central entry name.");
            byte[] localExtra = new byte[localExtraLength];
            stream.ReadExactly(localExtra);
            ZipExtraInfo localExtraInfo = ValidateExtra(localExtra, localName,
                localExpanded == uint.MaxValue, localCompressed == uint.MaxValue, false, false);
            string? localUnicodeName = localExtraInfo.UnicodePath;
            if (localUnicodeName is not null && !string.Equals(localUnicodeName, name, StringComparison.Ordinal))
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP local Unicode path differs from the central entry name.");
            ValidateLocalSize(localCompressed, compressedBytes, localExtraInfo.CompressedBytes, deferred,
                "ZIP local compressed size conflicts with the central header.");
            ValidateLocalSize(localExpanded, expandedBytes, localExtraInfo.ExpandedBytes, deferred,
                "ZIP local expanded size conflicts with the central header.");

            long dataOffset = stream.Position;
            if (dataOffset < 0 || dataOffset > centralDirectoryOffset
                || compressedBytes > centralDirectoryOffset - dataOffset)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry data is out of range.");
            long dataEnd = dataOffset + compressedBytes;
            if (deferred)
            {
                dataEnd = ReadDataDescriptor(stream, dataEnd, centralDirectoryOffset, crc,
                    compressedBytes, expandedBytes, compressed == uint.MaxValue || expanded == uint.MaxValue);
            }
            dataRanges.Add((localHeaderOffset, dataEnd));
            stream.Position = saved;
            result.Add(new(name, flags, method, crc, compressedBytes, expandedBytes, external, localHeaderOffset, dataOffset));
        }
        if (stream.Position != directory.CentralDirectoryEnd)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory length is inconsistent.");
        (long Start, long End)[] orderedRanges = dataRanges.OrderBy(range => range.Start).ToArray();
        for (int index = 1; index < orderedRanges.Length; index++)
        {
            if (orderedRanges[index - 1].End > orderedRanges[index].Start)
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry ranges overlap.");
        }
        return result;
    }

    private static bool TryReadZip64Locator(FileStream stream, long endAbsolute, out Zip64Locator locator)
    {
        locator = default;
        if (endAbsolute < 20) return false;
        stream.Position = endAbsolute - 20;
        Span<byte> bytes = stackalloc byte[20];
        stream.ReadExactly(bytes);
        if (U32(bytes, 0) != Zip64LocatorSignature) return false;
        ulong recordOffset = U64(bytes, 8);
        if (recordOffset > long.MaxValue)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 central directory offset is too large.");
        locator = new((long)recordOffset);
        return true;
    }

    private static ZipDirectoryMetadata ReadZip64Directory(
        FileStream stream,
        long endAbsolute,
        Zip64Locator locator,
        ushort legacyEntriesOnDisk,
        ushort legacyEntries,
        uint legacyCentralBytes,
        uint legacyCentralOffset)
    {
        long locatorOffset = checked(endAbsolute - 20);
        stream.Position = locatorOffset;
        Span<byte> locatorBytes = stackalloc byte[20];
        stream.ReadExactly(locatorBytes);
        if (U32(locatorBytes, 0) != Zip64LocatorSignature)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 locator is invalid.");
        if (U32(locatorBytes, 4) != 0 || U32(locatorBytes, 16) != 1)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP is unsupported.");
        ulong locatorRecordOffset = U64(locatorBytes, 8);
        if (locatorRecordOffset != (ulong)locator.RecordOffset)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 locator offset is inconsistent.");
        if (locator.RecordOffset < 0 || locatorOffset < 56 || locator.RecordOffset > locatorOffset - 56)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 end record is out of range.");

        stream.Position = locator.RecordOffset;
        Span<byte> record = stackalloc byte[56];
        stream.ReadExactly(record);
        if (U32(record, 0) != Zip64EndSignature)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 end record is invalid.");
        ulong recordSize = U64(record, 4);
        if (recordSize < 44 || recordSize > (ulong)(long.MaxValue - 12))
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 end record length is invalid.");
        long recordLength = checked(12 + (long)recordSize);
        if (recordLength > locatorOffset - locator.RecordOffset
            || checked(locator.RecordOffset + recordLength) != locatorOffset)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 end record boundary is invalid.");

        uint disk = U32(record, 16);
        uint centralDisk = U32(record, 20);
        ulong entriesOnDisk = U64(record, 24);
        ulong entries = U64(record, 32);
        ulong centralBytes = U64(record, 40);
        ulong centralOffset = U64(record, 48);
        if (disk != 0 || centralDisk != 0 || entriesOnDisk != entries)
            Reject(GamePackageRejectionCodes.ArchiveFormatUnsupported, "Multi-disk ZIP is unsupported.");
        if (legacyEntriesOnDisk != ushort.MaxValue && (ulong)legacyEntriesOnDisk != entriesOnDisk)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry count differs between ZIP64 and legacy records.");
        if (legacyEntries != ushort.MaxValue && (ulong)legacyEntries != entries)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry count differs between ZIP64 and legacy records.");
        if (legacyCentralBytes != uint.MaxValue && (ulong)legacyCentralBytes != centralBytes)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory size differs between ZIP64 and legacy records.");
        if (legacyCentralOffset != uint.MaxValue && (ulong)legacyCentralOffset != centralOffset)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP central directory offset differs between ZIP64 and legacy records.");
        return new(entries, centralBytes, centralOffset, locator.RecordOffset);
    }

    private static long ReadDataDescriptor(
        FileStream stream,
        long dataEnd,
        long centralDirectoryOffset,
        uint expectedCrc,
        long expectedCompressed,
        long expectedExpanded,
        bool zip64Sizes)
    {
        int sizeBytes = zip64Sizes ? sizeof(ulong) : sizeof(uint);
        int payloadBytes = sizeof(uint) + sizeBytes + sizeBytes;
        if (dataEnd < 0 || centralDirectoryOffset < dataEnd || centralDirectoryOffset - dataEnd < payloadBytes)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor is truncated.");
        stream.Position = dataEnd;
        Span<byte> descriptor = stackalloc byte[24];
        stream.ReadExactly(descriptor[..4]);
        bool signature = U32(descriptor, 0) == DataDescriptorSignature;
        int totalBytes = checked(payloadBytes + (signature ? sizeof(uint) : 0));
        if (centralDirectoryOffset - dataEnd < totalBytes)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor is truncated.");
        if (signature)
            stream.ReadExactly(descriptor.Slice(4, payloadBytes));
        else
            stream.ReadExactly(descriptor.Slice(4, payloadBytes - sizeof(uint)));
        int valueOffset = signature ? sizeof(uint) : 0;
        uint actualCrc = U32(descriptor, valueOffset);
        int sizeOffset = valueOffset + sizeof(uint);
        ulong actualCompressed = zip64Sizes ? U64(descriptor, sizeOffset) : U32(descriptor, sizeOffset);
        ulong actualExpanded = zip64Sizes ? U64(descriptor, sizeOffset + sizeBytes) : U32(descriptor, sizeOffset + sizeBytes);
        if (actualCrc != expectedCrc || actualCompressed != (ulong)expectedCompressed || actualExpanded != (ulong)expectedExpanded)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP data descriptor differs from the central header.");
        return checked(dataEnd + totalBytes);
    }

    private static void ValidateLocalSize(
        uint legacyValue,
        long centralValue,
        ulong? extendedValue,
        bool deferred,
        string message)
    {
        if (legacyValue == uint.MaxValue)
        {
            if (extendedValue is not null)
            {
                if (ToInt64(extendedValue.Value, message) != centralValue) Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
            }
            else if (!deferred)
            {
                Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
            }
            return;
        }
        if (extendedValue is not null)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
        if (centralValue > uint.MaxValue)
        {
            if (!deferred || legacyValue != 0) Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
            return;
        }
        if ((!deferred && legacyValue != (uint)centralValue)
            || (deferred && legacyValue != 0 && legacyValue != (uint)centralValue))
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
    }

    private static long ResolveZip64Value(uint legacyValue, ulong? extendedValue, string message)
    {
        if (legacyValue != uint.MaxValue)
        {
            if (extendedValue is not null) Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
            return legacyValue;
        }
        if (extendedValue is null) return MissingZip64Value(message);
        return ToInt64(extendedValue.Value, message);
    }

    private static long MissingZip64Value(string message)
    {
        Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
        return 0;
    }

    private static uint MissingZip64DiskValue(string message)
    {
        Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
        return 0;
    }

    private static ZipExtraInfo ValidateExtra(
        ReadOnlySpan<byte> extra,
        ReadOnlySpan<byte> rawName,
        bool expandedRequired,
        bool compressedRequired,
        bool offsetRequired,
        bool diskRequired)
    {
        int offset = 0;
        HashSet<ushort>? ids = null;
        string? unicodePath = null;
        ulong? expanded = null;
        ulong? compressed = null;
        ulong? localOffset = null;
        uint? startDisk = null;
        while (offset < extra.Length)
        {
            if (extra.Length - offset < 4) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field is truncated.");
            ushort id = U16(extra, offset);
            ushort length = U16(extra, offset + 2);
            offset += 4;
            if (length > extra.Length - offset) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field length is invalid.");
            ids ??= new HashSet<ushort>();
            if (!ids.Add(id)) Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP extra field is duplicated.");
            if (id == Zip64ExtraId)
            {
                int requiredLength = (expandedRequired ? sizeof(ulong) : 0)
                    + (compressedRequired ? sizeof(ulong) : 0)
                    + (offsetRequired ? sizeof(ulong) : 0)
                    + (diskRequired ? sizeof(uint) : 0);
                if (length != requiredLength)
                    Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP64 extra field length is invalid.");
                int valueOffset = offset;
                if (expandedRequired) { expanded = U64(extra, valueOffset); valueOffset += sizeof(ulong); }
                if (compressedRequired) { compressed = U64(extra, valueOffset); valueOffset += sizeof(ulong); }
                if (offsetRequired) { localOffset = U64(extra, valueOffset); valueOffset += sizeof(ulong); }
                if (diskRequired) startDisk = U32(extra, valueOffset);
            }
            else if (id == 0x7075)
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
        return new(unicodePath, expanded, compressed, localOffset, startDisk);
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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(437).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "ZIP entry name is not valid UTF-8.", innerException: exception);
        }
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

    private static long ToInt64(ulong value, string message)
    {
        if (value > long.MaxValue)
        {
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, message);
            return 0;
        }
        return (long)value;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Reject(string code, string message) => throw new GamePackageIngestionException(code, message);

    private readonly record struct Zip64Locator(long RecordOffset);

    private readonly record struct ZipDirectoryMetadata(
        ulong EntryCount,
        ulong CentralDirectoryBytes,
        ulong CentralDirectoryOffset,
        long CentralDirectoryEnd);

    private readonly record struct ZipExtraInfo(
        string? UnicodePath,
        ulong? ExpandedBytes,
        ulong? CompressedBytes,
        ulong? LocalHeaderOffset,
        uint? StartDiskNumber);
}
