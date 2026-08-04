using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Shape;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.Runtime.Utils.EvilMask;

class ConsoleDivPart : AConsoleDisplayNode
{
	public ConsoleDivPart(MixedNum xPos, MixedNum yPos, MixedNum width, MixedNum height, int depth, int color, StyledBoxModel box, bool isRelative, ConsoleDisplayLine[] childs)
	{
		backgroundColor = color >= 0 ? Color.FromArgb((int)(color | 0xff000000)) : Color.Transparent;
		StringBuilder sb = new();
		width.num = Math.Abs(width.num);
		height.num = Math.Abs(height.num);
		sb.Append("<div");
		AddTagMixedNumArg(sb, "xpos", xPos);
		AddTagMixedNumArg(sb, "ypos", yPos);
		AddTagMixedNumArg(sb, "width", width);
		AddColorParam(sb, "color", backgroundColor);
		AddTagMixedNumArg(sb, "height", height);
		if (box != null)
		{
			AddTagMixedParam(sb, "margin", box.margin);
			MixedNum4ToInt4(box.margin, ref margin);
			AddTagMixedParam(sb, "padding", box.padding);
			MixedNum4ToInt4(box.padding, ref padding);
			AddTagMixedParam(sb, "border", box.border);
			MixedNum4ToInt4(box.border, ref border);
			AddTagMixedParam(sb, "radius", box.radius);
			MixedNum4ToInt4(box.radius, ref radius);
			if (box.color != null)
			{
				borderColors = new Color[4];
				for (int i = 0; i < 4; i++)
					borderColors[i] = box.color[i] >= 0 ? Color.FromArgb((int)(box.color[i] | 0xff000000)) : Color.Transparent;
				AddColorParam4(sb, "bcolor", borderColors);
			}
		}
		sb.Append(">");
		altHeadTag = sb.ToString();
		Text = string.Empty;
		xOffset = MixedNum.ToPixel(xPos, 0);
		#region EE_div各要素の修正
		if (margin != null) divXOffset += margin[Direction.Left];
		if (padding != null) divXOffset += padding[Direction.Left];
		if (border != null) divXOffset += border[Direction.Left];
		#endregion
		PointY = MixedNum.ToPixel(yPos, 0);

		#region EE_div各要素の修正
		if (margin != null) yOffset += margin[Direction.Top];
		if (padding != null) yOffset += padding[Direction.Top];
		if (border != null) yOffset += border[Direction.Top];
		#endregion

		this.width = MixedNum.ToPixel(width, 0);
		Height = MixedNum.ToPixel(height, 0);
		children = childs;
		Depth = depth;
		IsRelative = isRelative;
		
		ShiftChildrenX(PointX + xOffset + divXOffset);
	}
	int pointX;
	int xOffset;
	#region EE_div各要素の修正
	int divXOffset;
	int yOffset;
	#endregion
	int width;
	public override int PointX
	{
		get { return pointX; }
		set
		{
			var diff = value - pointX;
			pointX = value;
			#region EE_div各要素の修正
			//foreach (var child in children)
			//    child.ShiftPositionX(value + xOffset + divXOffset);
			ShiftChildrenX(diff);
			#endregion
		}
	}
	int PointY;
	int Height;
	int[] margin, padding, radius, border;
	Color[] borderColors;
	Color backgroundColor;
	string altHeadTag;
	readonly ConsoleDisplayLine[] children;
	public bool IsEscaped { get; set; }
	public override int Top { get { return PointY; } }
	public override int Bottom { get { return PointY + Height; } }
	public bool IsRelative { get; private set; }
	public ConsoleDisplayLine[] Children { get { return children; } }

	public override bool CanDivide => false;
	public ConsoleButtonString TestChildHitbox(int pointX, int pointY, int relPointY)
	{
		ConsoleButtonString pointing = null;
		#region EE_div各要素の修正
		var rect = new Rectangle(PointX + xOffset, relPointY + PointY + yOffset, width, Height);
		#endregion
		if (!rect.Contains(pointX, pointY)) return null;
		relPointY = rect.Y;
		foreach (var line in children)
		{
			for (int b = 0; b < line.Buttons.Length; b++)
			{
				ConsoleButtonString button = line.Buttons[line.Buttons.Length - b - 1];
				if (button == null || button.StrArray == null)
					continue;
				if (button.PointX <= pointX && button.PointX + button.Width >= pointX)
				{
					//if (relPointY >= 0 && relPointY <= Config.Config.FontSize)
					//{
					//	pointing = button;
					//	if(pointing.IsButton)
					//		goto breakfor;
					//}
					foreach (AConsoleDisplayNode part in button.StrArray)
					{
						if (part == null)
							continue;
						if (part.PointX <= pointX && part.PointX + part.Width >= pointX
							&& relPointY + part.Top <= pointY && relPointY + part.Bottom >= pointY)
						{
							pointing = button;
							if (pointing.IsButton)
								return pointing;
						}
					}
				}
			}
			relPointY += Config.Config.LineHeight;
		}
		return pointing;
	}
	public override void DrawTo(Graphics graph, int pointY, bool isSelecting, bool isBackLog, bool isFocus, TextDrawingMode mode, bool isButton = false)
	{
		if (GlobalStatic.EMediator.Console.Window == null) return;
		var rect = IsRelative ? new Rectangle(PointX + xOffset, pointY + PointY, width + 2, Height)
			: new Rectangle(xOffset, GlobalStatic.EMediator.Console.Window.MainPicBox.Height - PointY - Height, width + 2, Height); // 何故か+2pxが必要，なぞ

		if (margin != null)
			rect = new Rectangle(rect.X + margin[Direction.Left], rect.Y + margin[Direction.Top],
				 rect.Width - margin[Direction.Left] - margin[Direction.Right], rect.Height - margin[Direction.Top] - margin[Direction.Bottom]);
		graph.SetClip(rect, CombineMode.Replace);

		var pxMode = graph.PixelOffsetMode;
		graph.PixelOffsetMode = PixelOffsetMode.HighQuality; // ここを高品質にしておく、全体的高品質してもいいかな？
		BoxBorder.DrawBorder(graph, rect, border, radius, borderColors, backgroundColor);
		graph.PixelOffsetMode = pxMode;

		if (border != null)
			rect = new Rectangle(rect.X + border[Direction.Left], rect.Y + border[Direction.Top],
				 rect.Width - border[Direction.Left] - border[Direction.Right], rect.Height - border[Direction.Top] - border[Direction.Bottom]);

		if (padding != null)
			rect = new Rectangle(rect.X + padding[Direction.Left], rect.Y + padding[Direction.Top],
				 rect.Width - padding[Direction.Left] - padding[Direction.Right], rect.Height - padding[Direction.Top] - padding[Direction.Bottom]);

		graph.SetClip(rect, CombineMode.Replace);

		pointY = rect.Y;
		foreach (var child in children)
		{
			child.DrawTo(graph, pointY, isBackLog, true, mode);
			pointY += Config.Config.LineHeight;
		}
		graph.ResetClip();
	}

    private void ShiftChildrenX(int diff)
	{
		foreach (var child in children)
			child.ShiftPositionX(diff);
	}

	public override void SetWidth(StringMeasure sm, float subPixel)
	{
	}

	public override string ToString()
	{
		var sb = new StringBuilder();
		sb.Append(altHeadTag);
		foreach (var line in children)
		{
			line.BuildString(sb);
			sb.Append("\r\n");
		}
		sb.Append("</div>");
		return sb.ToString();
	}
	public override StringBuilder BuildString(StringBuilder sb)
	{
		sb.Append(altHeadTag);
		foreach (var line in children)
		{
			line.BuildString(sb);
			sb.Append("\r\n");
		}
		sb.Append("</div>");
		return sb;
	}
}
