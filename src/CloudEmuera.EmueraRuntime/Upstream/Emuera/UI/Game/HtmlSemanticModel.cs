// CloudEmuera modification: the pinned HtmlManager exposes this small
// assembly-internal semantic seam to the headless translator. It deliberately
// contains no DOM, URL, Graphics, Font, WinForms, IPC, or RuntimeAdapter type.
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.UI.Game;

internal enum UpstreamHtmlParseMode
{
	DisplayLines,
	PrintBufferParts,
}

internal sealed class UpstreamHtmlParseOptions
{
	public UpstreamHtmlParseMode Mode { get; init; }
	public UpstreamHtmlParseBudget Budget { get; init; }
}

internal sealed class UpstreamHtmlParseBudget
{
	public UpstreamHtmlParseBudget(
		int maxInputUtf16Units,
		int maxTagCount,
		int maxDivDepth,
		int maxSegments,
		int maxParts,
		int maxTextUtf16Units)
	{
		MaxInputUtf16Units = RequirePositive(maxInputUtf16Units, nameof(maxInputUtf16Units));
		MaxTagCount = RequirePositive(maxTagCount, nameof(maxTagCount));
		MaxDivDepth = RequirePositive(maxDivDepth, nameof(maxDivDepth));
		MaxSegments = RequirePositive(maxSegments, nameof(maxSegments));
		MaxParts = RequirePositive(maxParts, nameof(maxParts));
		MaxTextUtf16Units = RequirePositive(maxTextUtf16Units, nameof(maxTextUtf16Units));
	}

	public int MaxInputUtf16Units { get; }
	public int MaxTagCount { get; }
	public int MaxDivDepth { get; }
	public int MaxSegments { get; }
	public int MaxParts { get; }
	public int MaxTextUtf16Units { get; }

	private int tagCount;
	private int segmentCount;
	private int partCount;
	private int textUtf16Units;

	public void ValidateInput(string value)
	{
		if (value == null)
			throw new ArgumentNullException(nameof(value));
		if (value.Length > MaxInputUtf16Units)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_INPUT_LIMIT");
	}

	public void ConsumeTag()
	{
		tagCount = checked(tagCount + 1);
		if (tagCount > MaxTagCount)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_TAG_LIMIT");
	}

	public void ConsumeSegment()
	{
		segmentCount = checked(segmentCount + 1);
		if (segmentCount > MaxSegments)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_OUTPUT_LIMIT");
	}

	public void ConsumePart()
	{
		partCount = checked(partCount + 1);
		if (partCount > MaxParts)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_OUTPUT_LIMIT");
	}

	public void ConsumeText(string value)
	{
		if (value == null)
			return;
		textUtf16Units = checked(textUtf16Units + value.Length);
		if (textUtf16Units > MaxTextUtf16Units)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_OUTPUT_LIMIT");
	}

	public void EnterDiv(int depth)
	{
		if (depth > MaxDivDepth)
			throw new UpstreamHtmlBudgetExceededException("EMUERA_HTML_DEPTH_LIMIT");
	}

	private static int RequirePositive(int value, string name) => value > 0
		? value
		: throw new ArgumentOutOfRangeException(name, value, "HTML parser budgets must be positive.");
}

internal sealed class UpstreamHtmlBudgetExceededException : Exception
{
	public UpstreamHtmlBudgetExceededException(string reasonCode)
		: base(reasonCode)
	{
		ReasonCode = reasonCode;
	}

	public string ReasonCode { get; }
}

internal sealed class UpstreamHtmlParseCapture
{
	public DisplayLineAlignment Alignment { get; set; }
	public bool NoWrap { get; set; }

	public void Set(DisplayLineAlignment alignment, bool noWrap)
	{
		Alignment = alignment;
		NoWrap = noWrap;
	}
}

internal enum UpstreamHtmlAlignment
{
	Left,
	Center,
	Right,
}

[Flags]
internal enum UpstreamHtmlFontStyle
{
	None = 0,
	Bold = 1,
	Italic = 2,
	Underline = 4,
	Strike = 8,
}

internal sealed class UpstreamHtmlTextStyle
{
	public int ForegroundRgb { get; init; }
	public int ButtonRgb { get; init; }
	public bool ColorChanged { get; init; }
	public UpstreamHtmlFontStyle FontStyle { get; init; }
	public string FontName { get; init; }
}

internal readonly struct UpstreamHtmlLength
{
	public UpstreamHtmlLength(int value, bool isPixels)
	{
		Value = value;
		IsPixels = isPixels;
	}

	public int Value { get; }
	public bool IsPixels { get; }
}

internal enum UpstreamHtmlButtonValueKind
{
	None,
	String,
	Integer,
}

internal abstract class UpstreamHtmlPart
{
}

internal sealed class UpstreamHtmlTextPart : UpstreamHtmlPart
{
	public UpstreamHtmlTextPart(string text, UpstreamHtmlTextStyle style)
	{
		Text = text ?? throw new ArgumentNullException(nameof(text));
		Style = style ?? throw new ArgumentNullException(nameof(style));
	}

	public string Text { get; }
	public UpstreamHtmlTextStyle Style { get; }
}

internal sealed class UpstreamHtmlImagePart : UpstreamHtmlPart
{
	public UpstreamHtmlImagePart(
		string source,
		string buttonSource,
		string mappingSource,
		UpstreamHtmlLength? height,
		UpstreamHtmlLength? width,
		UpstreamHtmlLength? yPosition)
	{
		Source = source ?? string.Empty;
		ButtonSource = buttonSource;
		MappingSource = mappingSource;
		Height = height;
		Width = width;
		YPosition = yPosition;
	}

	public string Source { get; }
	public string ButtonSource { get; }
	public string MappingSource { get; }
	public UpstreamHtmlLength? Height { get; }
	public UpstreamHtmlLength? Width { get; }
	public UpstreamHtmlLength? YPosition { get; }
}

internal sealed class UpstreamHtmlShapePart : UpstreamHtmlPart
{
	public UpstreamHtmlShapePart(
		string type,
		IReadOnlyList<UpstreamHtmlLength> parameters,
		int foregroundRgb,
		int buttonRgb,
		string errorText)
	{
		Type = type ?? string.Empty;
		Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
		ForegroundRgb = foregroundRgb;
		ButtonRgb = buttonRgb;
		ErrorText = errorText;
	}

	public string Type { get; }
	public IReadOnlyList<UpstreamHtmlLength> Parameters { get; }
	public int ForegroundRgb { get; }
	public int ButtonRgb { get; }
	public string ErrorText { get; }
}

internal sealed class UpstreamHtmlBoxModel
{
	public UpstreamHtmlLength[] Margin { get; init; }
	public UpstreamHtmlLength[] Padding { get; init; }
	public UpstreamHtmlLength[] Border { get; init; }
	public UpstreamHtmlLength[] Radius { get; init; }
	public int[] BorderColorsRgb { get; init; }
}

internal sealed class UpstreamHtmlDivPart : UpstreamHtmlPart
{
	public UpstreamHtmlLength? X { get; init; }
	public UpstreamHtmlLength? Y { get; init; }
	public UpstreamHtmlLength Width { get; init; }
	public UpstreamHtmlLength Height { get; init; }
	public int Depth { get; init; }
	public int BackgroundRgb { get; init; }
	public bool IsRelative { get; init; }
	public UpstreamHtmlBoxModel Box { get; init; }
	public UpstreamHtmlSequence Children { get; init; }
}

internal sealed class UpstreamHtmlSegment
{
	public IReadOnlyList<UpstreamHtmlPart> Parts { get; init; }
	public bool IsInteractive { get; init; }
	public UpstreamHtmlButtonValueKind ValueKind { get; init; }
	public string Value { get; init; }
	public string Title { get; init; }
	public int? PositionX { get; init; }
}

internal sealed class UpstreamHtmlSequenceItem
{
	public UpstreamHtmlSegment Segment { get; init; }
	public bool IsBreak => Segment == null;

	public static UpstreamHtmlSequenceItem Break() => new();
	public static UpstreamHtmlSequenceItem FromSegment(UpstreamHtmlSegment segment) => new() { Segment = segment };
}

internal sealed class UpstreamHtmlSequence
{
	public UpstreamHtmlSequence(IReadOnlyList<UpstreamHtmlSequenceItem> items)
	{
		Items = items ?? throw new ArgumentNullException(nameof(items));
	}

	public IReadOnlyList<UpstreamHtmlSequenceItem> Items { get; }
}

internal sealed class UpstreamHtmlFragment
{
	public UpstreamHtmlFragment(
		UpstreamHtmlAlignment alignment,
		bool noWrap,
		UpstreamHtmlSequence sequence)
	{
		Alignment = alignment;
		NoWrap = noWrap;
		Sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
	}

	public UpstreamHtmlAlignment Alignment { get; }
	public bool NoWrap { get; }
	public UpstreamHtmlSequence Sequence { get; }
}
