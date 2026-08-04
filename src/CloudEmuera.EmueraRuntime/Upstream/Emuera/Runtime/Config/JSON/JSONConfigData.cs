using System.Text.Json.Serialization;

namespace MinorShift.Emuera.Runtime.Config.JSON;
//JSONの定義
sealed class JSONConfigData
{
	//ボタンにカーソルを合わせたときに背景色を変更するか
	[JsonPropertyName("UseButtonFocusBackgroundColor")]
	public bool UseButtonFocusBackgroundColor { get; set; }

	[JsonPropertyName("UseNewRandom")]
	public bool UseNewRandom { get; set; }

	public bool UseScopedVariableInstruction { get; set; }
}
