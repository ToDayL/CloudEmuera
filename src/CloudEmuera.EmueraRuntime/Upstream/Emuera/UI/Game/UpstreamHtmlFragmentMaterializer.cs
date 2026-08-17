// CloudEmuera modification: direct semantic projection of the pinned
// HtmlManager output. No AltText/ToString round-trip is used here.
using System;
using System.Collections.Generic;
using System.Drawing;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.UI.Game;

internal static class UpstreamHtmlFragmentMaterializer
{
	public static UpstreamHtmlFragment FromButtons(
		IReadOnlyList<ConsoleButtonString> buttons,
		UpstreamHtmlParseCapture capture,
		UpstreamHtmlParseBudget budget)
	{
		var items = new List<UpstreamHtmlSequenceItem>(buttons.Count);
		foreach (ConsoleButtonString button in buttons)
		{
			if (button == null)
			{
				items.Add(UpstreamHtmlSequenceItem.Break());
				continue;
			}

			items.Add(UpstreamHtmlSequenceItem.FromSegment(ToSegment(button, budget)));
		}

		UpstreamHtmlAlignment alignment = capture.Alignment switch
		{
			DisplayLineAlignment.CENTER => UpstreamHtmlAlignment.Center,
			DisplayLineAlignment.RIGHT => UpstreamHtmlAlignment.Right,
			_ => UpstreamHtmlAlignment.Left,
		};
		return new UpstreamHtmlFragment(alignment, capture.NoWrap, new UpstreamHtmlSequence(items));
	}

	private static UpstreamHtmlSegment ToSegment(ConsoleButtonString button, UpstreamHtmlParseBudget budget)
	{
		var parts = new List<UpstreamHtmlPart>(button.StrArray.Length);
		foreach (AConsoleDisplayNode node in button.StrArray)
		{
			parts.Add(ToPart(node, budget));
		}

		return new UpstreamHtmlSegment
		{
			Parts = parts.AsReadOnly(),
			IsInteractive = button.IsButton,
			ValueKind = button.IsButton
				? button.IsInteger ? UpstreamHtmlButtonValueKind.Integer : UpstreamHtmlButtonValueKind.String
				: UpstreamHtmlButtonValueKind.None,
			Value = button.IsButton ? button.Inputs ?? string.Empty : string.Empty,
			Title = button.Title,
			// RelativePointX is the exact raw <button pos='...'> value. PointX
			// is a measured pixel position and is intentionally not used.
			PositionX = button.PointXisLocked ? button.RelativePointX : null,
		};
	}

	private static UpstreamHtmlPart ToPart(AConsoleDisplayNode node, UpstreamHtmlParseBudget budget)
	{
		return node switch
		{
			ConsoleStyledString text => new UpstreamHtmlTextPart(text.Text ?? string.Empty, ToTextStyle(text.StringStyle)),
			ConsoleImagePart image => new UpstreamHtmlImagePart(
				image.ResourceName,
				image.ButtonResourceName,
				image.MappingGraphName,
				ToLength(image.RawHeight),
				ToLength(image.RawWidth),
				ToLength(image.RawYPosition)),
			ConsoleShapePart shape => ToShape(shape),
			ConsoleDivPart div => ToDiv(div, budget),
			_ => throw new InvalidOperationException("The pinned HtmlManager produced an unknown display part.")
		};
	}

	private static UpstreamHtmlTextStyle ToTextStyle(StringStyle style) => new()
	{
		ForegroundRgb = ToRgb(style.Color),
		ButtonRgb = ToRgb(style.ButtonColor),
		ColorChanged = style.ColorChanged,
		FontStyle = ToFontStyle(style.FontStyle),
		FontName = style.Fontname,
	};

	private static UpstreamHtmlFontStyle ToFontStyle(FontStyle style)
	{
		UpstreamHtmlFontStyle result = UpstreamHtmlFontStyle.None;
		if ((style & FontStyle.Bold) != 0)
			result |= UpstreamHtmlFontStyle.Bold;
		if ((style & FontStyle.Italic) != 0)
			result |= UpstreamHtmlFontStyle.Italic;
		if ((style & FontStyle.Underline) != 0)
			result |= UpstreamHtmlFontStyle.Underline;
		if ((style & FontStyle.Strikeout) != 0)
			result |= UpstreamHtmlFontStyle.Strike;
		return result;
	}

	private static UpstreamHtmlShapePart ToShape(ConsoleShapePart shape)
	{
		var parameters = new UpstreamHtmlLength[shape.SemanticParameters?.Length ?? 0];
		for (int index = 0; index < parameters.Length; index++)
			parameters[index] = ToLength(shape.SemanticParameters[index]).Value;
		return new UpstreamHtmlShapePart(
			shape.SemanticType,
			parameters,
			ToRgb(shape.SemanticColor),
			ToRgb(shape.SemanticButtonColor),
			shape is ConsoleErrorShapePart ? shape.Text : null);
	}

	private static UpstreamHtmlDivPart ToDiv(ConsoleDivPart div, UpstreamHtmlParseBudget budget)
	{
		if (div.RawWidth == null || div.RawHeight == null)
			throw new InvalidOperationException("The pinned HtmlManager produced a div without dimensions.");

		return new UpstreamHtmlDivPart
		{
			X = ToLength(div.RawX),
			Y = ToLength(div.RawY),
			Width = ToLength(div.RawWidth).Value,
			Height = ToLength(div.RawHeight).Value,
			Depth = div.Depth,
			BackgroundRgb = div.RawColor,
			IsRelative = div.IsRelative,
			Box = ToBox(div.RawBox),
			Children = ToSequence(div.Children, budget),
		};
	}

	private static UpstreamHtmlSequence ToSequence(ConsoleDisplayLine[] lines, UpstreamHtmlParseBudget budget)
	{
		var items = new List<UpstreamHtmlSequenceItem>();
		for (int lineIndex = 0; lineIndex < (lines?.Length ?? 0); lineIndex++)
		{
			if (lineIndex > 0)
				items.Add(UpstreamHtmlSequenceItem.Break());
			ConsoleButtonString[] buttons = lines[lineIndex].Buttons ?? Array.Empty<ConsoleButtonString>();
			foreach (ConsoleButtonString button in buttons)
			{
				if (button != null)
					items.Add(UpstreamHtmlSequenceItem.FromSegment(ToSegment(button, budget)));
			}
		}
		return new UpstreamHtmlSequence(items);
	}

	private static UpstreamHtmlBoxModel ToBox(StyledBoxModel box)
	{
		if (box == null)
			return null;
		return new UpstreamHtmlBoxModel
		{
			Margin = ToLengths(box.margin),
			Padding = ToLengths(box.padding),
			Border = ToLengths(box.border),
			Radius = ToLengths(box.radius),
			BorderColorsRgb = ToColors(box.color),
		};
	}

	private static UpstreamHtmlLength[] ToLengths(MixedNum[] values)
	{
		if (values == null)
			return null;
		var result = new UpstreamHtmlLength[values.Length];
		for (int index = 0; index < values.Length; index++)
			result[index] = ToLength(values[index]).Value;
		return result;
	}

	private static int[] ToColors(int[] values)
	{
		if (values == null)
			return null;
		var result = new int[values.Length];
		Array.Copy(values, result, values.Length);
		return result;
	}

	private static UpstreamHtmlLength? ToLength(MixedNum value) => value == null
		? null
		: new UpstreamHtmlLength(value.num, value.isPx);

	private static int ToRgb(Color color) => color.IsEmpty ? 0 : (color.R << 16) | (color.G << 8) | color.B;
}
