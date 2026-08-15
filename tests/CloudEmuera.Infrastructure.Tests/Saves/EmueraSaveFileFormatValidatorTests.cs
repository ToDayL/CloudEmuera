using System.Buffers.Binary;
using System.Text;
using CloudEmuera.Application.Saves;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Saves;

namespace CloudEmuera.Infrastructure.Tests.Saves;

[Trait("Category", "SaveFormat")]
public sealed class EmueraSaveFileFormatValidatorTests
{
    [Fact]
    public async Task ValidTextAndBinarySavesProduceBoundedSha256Digests()
    {
        EmueraSaveFileFormatValidator validator = new(new InstanceCapacityOptions { MaxSaveFileBytes = 1024 });

        byte[] text = Encoding.UTF8.GetBytes("0\n0\n");
        SaveFormatValidationResult textResult = await validator.ValidateAsync(
            new MemoryStream(text), SessionSaveFileKind.Global, "global.sav", text.Length);
        Assert.Equal(text.Length, textResult.SizeBytes);
        Assert.StartsWith("sha256:", textResult.Digest, StringComparison.Ordinal);

        byte[] binary = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(binary, 0x0A1A0A0D41524589UL);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8), 1808);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12), 0);
        binary[16] = 0;
        SaveFormatValidationResult binaryResult = await validator.ValidateAsync(
            new MemoryStream(binary), SessionSaveFileKind.Normal, "save1.sav", binary.Length);
        Assert.Equal(binary.Length, binaryResult.SizeBytes);
        Assert.NotEqual(textResult.Digest, binaryResult.Digest);
    }

    [Fact]
    public async Task InvalidTextAndWrongBinaryFileTypeAreRejected()
    {
        EmueraSaveFileFormatValidator validator = new(new InstanceCapacityOptions { MaxSaveFileBytes = 1024 });

        SessionSaveException invalidText = await Assert.ThrowsAsync<SessionSaveException>(() => validator.ValidateAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("not a save\n")), SessionSaveFileKind.Normal, "save1.sav", 11));
        Assert.Equal(SaveErrorCodes.FormatInvalid, invalidText.Code);

        byte[] globalBinary = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(globalBinary, 0x0A1A0A0D41524589UL);
        BinaryPrimitives.WriteUInt32LittleEndian(globalBinary.AsSpan(8), 1808);
        globalBinary[16] = 0;
        SessionSaveException wrongKind = await Assert.ThrowsAsync<SessionSaveException>(() => validator.ValidateAsync(
            new MemoryStream(globalBinary), SessionSaveFileKind.Global, "global.sav", globalBinary.Length));
        Assert.Equal(SaveErrorCodes.FormatInvalid, wrongKind.Code);
    }

    [Fact]
    public async Task AuxiliaryPngIsHeaderCheckedAndSizeLimitIsEnforced()
    {
        EmueraSaveFileFormatValidator validator = new(new InstanceCapacityOptions { MaxSaveFileBytes = 64 });
        byte[] png = new byte[33];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 32);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 32);
        SaveFormatValidationResult result = await validator.ValidateAsync(
            new MemoryStream(png), SessionSaveFileKind.AuxiliaryImage, "img1.png", png.Length);
        Assert.Equal(png.Length, result.SizeBytes);

        byte[] oversized = new byte[65];
        SessionSaveException tooLarge = await Assert.ThrowsAsync<SessionSaveException>(() => validator.ValidateAsync(
            new MemoryStream(oversized), SessionSaveFileKind.AuxiliaryText, "txt1.txt", oversized.Length));
        Assert.Equal(SaveErrorCodes.FormatInvalid, tooLarge.Code);
    }
}
