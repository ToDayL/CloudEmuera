using System;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

public static class EncodingHandler
{
	public static Encoding UTF8Encoding = new UTF8Encoding(false, true);
	public static Encoding shiftjisEncoding = GetEncoding(932);
	public static Encoding UTF8BOMEncoding = new UTF8Encoding(true, true);


	public static Encoding DetectEncoding(string filePath)
	{
		try
		{
			using var file = File.Open(filePath, FileMode.Open);
			Span<byte> bom = stackalloc byte[3];
			_ = file.Read(bom);
			file.Close();
			if (bom.SequenceEqual<byte>([0xEF, 0xBB, 0xBF]))
			{
				return UTF8BOMEncoding;
			}
			//read using UTF8
			using var sr = new StreamReader(filePath, UTF8Encoding);
			//Peek for detecting any BOM encoding
			sr.Peek();
			//If any BOM was detected durig Peek(), sr.CurrentEncoding won't be the same 
			//as the encoding we passed as a argument
			if (!UTF8Encoding.Equals(sr.CurrentEncoding))
			{
				return sr.CurrentEncoding;
			}
			//Here the used encoding is still UTF8, if it detects any 
			//invalid byte, it means its not UTF8 in which case we assume its SHIFT-JIS
			sr.ReadToEnd();
			sr.Dispose();
			return UTF8Encoding;
		}
		catch
		{
			return shiftjisEncoding;
		}
	}

	public static Encoding DetectEncoding(Stream stream)
	{
		var pos = stream.Position;
		try
		{
			using var sr = new StreamReader(stream, UTF8Encoding, true, -1, true);
			sr.Peek();
			if (!UTF8Encoding.Equals(sr.CurrentEncoding))
			{
				stream.Seek(pos, SeekOrigin.Begin);
				return sr.CurrentEncoding;
			}
			sr.ReadToEnd();
			sr.Dispose();
			stream.Seek(pos, SeekOrigin.Begin);
			return UTF8Encoding;
		}
		catch
		{
			stream.Seek(pos, SeekOrigin.Begin);
			return shiftjisEncoding;
		}
	}
	public static Encoding GetEncoding(int codePage)
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
	}
}
