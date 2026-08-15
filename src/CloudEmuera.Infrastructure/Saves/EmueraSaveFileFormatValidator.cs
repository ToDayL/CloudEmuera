using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CloudEmuera.Application.Saves;
using CloudEmuera.Infrastructure.Capacity;

namespace CloudEmuera.Infrastructure.Saves;

/// <summary>
/// Bounded, parser-free sniffing for the native save categories. It does not
/// reference the vendored runtime assembly and never decides Game compatibility.
/// </summary>
public sealed class EmueraSaveFileFormatValidator(InstanceCapacityOptions capacityOptions) : ISaveFileFormatValidator
{
    private const ulong BinaryHeader = 0x0A1A0A0D41524589UL;
    private const ulong CompressedBinaryHeader = 0x0A50495A41524589UL;
    private const uint BinaryVersion = 1808;
    private const uint MaximumDataCount = 1024;
    private const int MaximumTextPrefixBytes = 64 * 1024;
    private const int MaximumPngDimension = 32 * 1024;

    public Task<SaveFormatValidationResult> ValidateAsync(
        Stream content,
        SessionSaveFileKind kind,
        string fileName,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (sizeBytes <= 0 || sizeBytes > capacityOptions.MaxSaveFileBytes || !content.CanSeek)
            throw InvalidFormat();
        try
        {
            content.Position = 0;
            switch (kind)
            {
                case SessionSaveFileKind.Normal or SessionSaveFileKind.Global:
                    ValidateSave(content, kind, fileName, sizeBytes, cancellationToken);
                    break;
                case SessionSaveFileKind.AuxiliaryText:
                    ValidateText(content, sizeBytes, cancellationToken);
                    break;
                case SessionSaveFileKind.AuxiliaryImage:
                    ValidatePng(content, sizeBytes, cancellationToken);
                    break;
                default:
                    throw InvalidFormat();
            }

            content.Position = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long readTotal = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readTotal = checked(readTotal + read);
                if (readTotal > sizeBytes || readTotal > capacityOptions.MaxSaveFileBytes)
                    throw new SessionSaveException(SaveErrorCodes.FileTooLarge, "存档文件超过大小上限。", 413);
                hash.AppendData(buffer, 0, read);
            }
            if (readTotal != sizeBytes)
                throw InvalidFormat();
            return Task.FromResult(new SaveFormatValidationResult(
                readTotal,
                $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}"));
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException or InvalidDataException or ArgumentException or OverflowException)
        {
            throw new SessionSaveException(SaveErrorCodes.FormatInvalid, "存档文件格式无效。", 415, exception);
        }
    }

    private static void ValidateSave(Stream content, SessionSaveFileKind kind, string fileName, long sizeBytes, CancellationToken cancellationToken)
    {
        if (!fileName.EndsWith(".sav", StringComparison.Ordinal))
            throw InvalidFormat();
        Span<byte> header = stackalloc byte[16];
        if (ReadAtMost(content, header, cancellationToken) < header.Length)
        {
            content.Position = 0;
            ValidateText(content, sizeBytes, cancellationToken);
            return;
        }
        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(header);
        if (magic is not (BinaryHeader or CompressedBinaryHeader))
        {
            content.Position = 0;
            ValidateText(content, sizeBytes, cancellationToken);
            return;
        }
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint dataCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (version != BinaryVersion || dataCount > MaximumDataCount)
            throw InvalidFormat();
        long dataBytes = checked((long)dataCount * sizeof(uint));
        if (sizeBytes < 16 + dataBytes + 1)
            throw InvalidFormat();
        if (dataBytes != 0)
            content.Seek(dataBytes, SeekOrigin.Current);
        if (magic == CompressedBinaryHeader)
        {
            using GZipStream gzip = new(content, CompressionMode.Decompress, leaveOpen: true);
            int type = gzip.ReadByte();
            if (type < 0)
                throw InvalidFormat();
            ValidateFileType((byte)type, kind);
        }
        else
        {
            int type = content.ReadByte();
            if (type < 0)
                throw InvalidFormat();
            ValidateFileType((byte)type, kind);
        }
    }

    private static void ValidateFileType(byte type, SessionSaveFileKind kind)
    {
        byte expected = kind == SessionSaveFileKind.Global ? (byte)1 : (byte)0;
        if (type != expected)
            throw InvalidFormat();
    }

    private static void ValidateText(Stream content, long sizeBytes, CancellationToken cancellationToken)
    {
        int length = checked((int)Math.Min(sizeBytes, MaximumTextPrefixBytes));
        byte[] prefix = new byte[length];
        ReadExactly(content, prefix, cancellationToken);
        if (prefix.Any(static value => value == 0))
            throw InvalidFormat();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(prefix);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(prefix);
        }
        string[] fields = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string[] logical = fields.Where(static field => !string.IsNullOrWhiteSpace(field)).Take(2).ToArray();
        if (logical.Length < 2 ||
            !long.TryParse(logical[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _) ||
            !long.TryParse(logical[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw InvalidFormat();
    }

    private static void ValidatePng(Stream content, long sizeBytes, CancellationToken cancellationToken)
    {
        if (sizeBytes < 33)
            throw InvalidFormat();
        Span<byte> header = stackalloc byte[33];
        ReadExactly(content, header, cancellationToken);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            !header[12..16].SequenceEqual("IHDR"u8))
            throw InvalidFormat();
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0 || width > MaximumPngDimension || height > MaximumPngDimension)
            throw InvalidFormat();
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw InvalidFormat();
            offset += read;
        }
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                break;
            offset += read;
        }
        return offset;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        ReadExactly(stream, buffer.AsSpan(), cancellationToken);

    private static SessionSaveException InvalidFormat() =>
        new(SaveErrorCodes.FormatInvalid, "存档文件格式无效。", 415);
}
