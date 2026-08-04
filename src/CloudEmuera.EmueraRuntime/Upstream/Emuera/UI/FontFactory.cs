using MinorShift.Emuera.Runtime.Config;
using System.Collections.Generic;
using System.Drawing;

namespace MinorShift.Emuera.UI;

internal class FontFactory
{

	static readonly Dictionary<(string fontname, int fontSize, FontStyle fontStyle), Font> fontDic = [];

	public static Font GetFont(string requestFontName, FontStyle style)
	{
		/*
		string fontname = requestFontName;
		if (string.IsNullOrEmpty(requestFontName))
			fontname = Config.FontName;
		if (!fontDic.ContainsKey((fontname, Config.FontSize, style)))
		{
			var font = new Font(fontname, Config.FontSize, style, GraphicsUnit.Pixel);
			if (font == null)
			{
				return null;
			}
			else
			{
				fontDic.Add((fontname, Config.FontSize, style), font);
			}

		}
		#region EE_フォントファイル対応
		int fontsize = Config.FontSize;
		Font styledFont;
		foreach (FontFamily ff in GlobalStatic.Pfc.Families)
		{
			if (ff.Name == fontname)
			{
				styledFont = new Font(ff, fontsize, style, GraphicsUnit.Pixel);
				break;
			}
		}
		#endregion
		return fontDic[(fontname, Config.FontSize, style)];
		*/

		string fn = requestFontName;
		if (string.IsNullOrEmpty(requestFontName))
			fn = Config.FontName;
		if (!fontDic.ContainsKey((fn, Config.FontSize, style)))
		{
			var font = new Font(fn, Config.FontSize, style, GraphicsUnit.Pixel);
			if (font != null)
				fontDic.Add((fn, Config.FontSize, style), font);

		}
		Dictionary<FontStyle, Font> fontStyleDic = [];
		if (!fontStyleDic.ContainsKey(style))
		{
			int fontsize = Config.FontSize;
			Font styledFont;
			try
			{
				#region EE_フォントファイル対応
				foreach (FontFamily ff in GlobalStatic.Pfc.Families)
				{
					if (ff.Name == fn)
					{
						styledFont = new Font(ff, fontsize, style, GraphicsUnit.Pixel);
						goto foundfont;
					}
				}
				styledFont = new Font(fn, fontsize, style, GraphicsUnit.Pixel);
			}
			catch
			{
				return null;
			}
		foundfont:
			#endregion
			fontStyleDic.Add(style, styledFont);
		}
		return fontStyleDic[style];
	}

	public static void ClearFont()
	{
		foreach (var font in fontDic)
		{
			font.Value.Dispose();
		}
		fontDic.Clear();
	}
}
