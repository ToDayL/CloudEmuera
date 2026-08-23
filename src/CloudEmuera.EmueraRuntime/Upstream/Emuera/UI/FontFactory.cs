using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace MinorShift.Emuera.UI;

internal class FontFactory
{

	static readonly Dictionary<(string fontname, int fontSize, FontStyle fontStyle), Font> fontDic = [];

	public static Font GetFont(string requestFontName, FontStyle style, int? requestedSize = null)
	{
		// CloudEmuera S04: Config.FontName is set only after the selected
		// image-owned face has been loaded into the private collection. Game
		// PRINT/HTML font requests are intentionally ignored, so measurement and
		// rendering cannot diverge or fall back to host-installed fonts.
		string fn = Config.FontName;
		if (string.IsNullOrEmpty(fn) || GlobalStatic.Pfc is null)
			return null;

		int fontSize = requestedSize ?? Config.FontSize;
		if (fontSize is < 1 or > 512)
			return null;
		var key = (fn, fontSize, style);
		if (fontDic.TryGetValue(key, out Font cached))
			return cached;

		FontFamily family = null;
		foreach (FontFamily candidate in GlobalStatic.Pfc.Families)
		{
			if (string.Equals(candidate.Name, fn, StringComparison.Ordinal))
			{
				family = candidate;
				break;
			}
		}

		if (family is null)
			return null;

		try
		{
			Font created = new(family, fontSize, style, GraphicsUnit.Pixel);
			fontDic.Add(key, created);
			return created;
		}
		catch
		{
			return null;
		}
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
