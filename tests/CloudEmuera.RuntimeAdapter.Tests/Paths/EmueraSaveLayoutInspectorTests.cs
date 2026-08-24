using System.Text;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "SessionRoot")]
public sealed class EmueraSaveLayoutInspectorTests
{
    [Theory]
    [InlineData("", RuntimeSaveLayout.Root)]
    [InlineData("Use sav folder:NO\n", RuntimeSaveLayout.Root)]
    [InlineData("Use sav folder:FALSE\n", RuntimeSaveLayout.Root)]
    [InlineData("Use sav folder:YES\n", RuntimeSaveLayout.SavDirectory)]
    [InlineData("Use sav folder:1\n", RuntimeSaveLayout.SavDirectory)]
    [InlineData("セーブデータをsavフォルダ内に作成する:後\n", RuntimeSaveLayout.Root)]
    [InlineData("セーブデータをsavフォルダ内に作成する:前\n", RuntimeSaveLayout.SavDirectory)]
    [InlineData("セーブデータをSAVフォルダ内に作成する:YES\n", RuntimeSaveLayout.SavDirectory)]
    [InlineData("在sav文件夹中创建存档:YES\n", RuntimeSaveLayout.SavDirectory)]
    public void InspectorUsesTheFixedUpstreamBooleanContract(string text, RuntimeSaveLayout expected)
    {
        Assert.Equal(expected, EmueraSaveLayoutInspector.Inspect(text));
    }

    [Fact]
    public void InspectorAcceptsUtf8BomAndShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        UTF8Encoding utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
        byte[] utf8Bytes = utf8Bom.GetPreamble()
            .Concat(utf8Bom.GetBytes("Use sav folder:YES\n"))
            .ToArray();
        Assert.Equal(RuntimeSaveLayout.SavDirectory, EmueraSaveLayoutInspector.Inspect(utf8Bytes));
        Assert.Equal(
            RuntimeSaveLayout.Root,
            EmueraSaveLayoutInspector.Inspect(shiftJis.GetBytes("Use sav folder:NO\n")));
    }

    [Theory]
    [InlineData("Use sav folder:YES\nUse sav folder:NO\n")]
    [InlineData("Use sav folder:maybe\n")]
    public void InspectorRejectsConflictingOrInvalidValues(string text)
    {
        Assert.Throws<RuntimeSaveLayoutInspectionException>(() =>
            EmueraSaveLayoutInspector.Inspect(text));
    }

    [Fact]
    public void InspectorBoundsConfigurationStreamBeforeAllocatingUnboundedData()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Use sav folder:NO\n" + new string('x', 1024 * 1024)));

        Assert.Throws<RuntimeSaveLayoutInspectionException>(() =>
            EmueraSaveLayoutInspector.Inspect(stream));
    }
}
