//新設したコンフィグ設定のロード、セーブ、公開を担当する。
using System.IO;
using System.Text.Json;


namespace MinorShift.Emuera.Runtime.Config.JSON;
static class JSONConfig
{
	public static JSONConfigData Data;

	const string _configFileName = "setting.json";
	// CloudEmuera modification: headless Sessions reuse the pinned process but
	// each Session has a different Program.ExeDir. Resolve this path per call so
	// JSON settings never remain bound to the first SessionRoot.
	static string ConfigFilePath => Program.ExeDir + _configFileName;

	public static void Load()
	{
		string configFilePath = ConfigFilePath;
		if (!File.Exists(configFilePath))
		{
			var defaultData = new JSONConfigData();
			var defaultJson = JsonSerializer.Serialize(defaultData);
			File.WriteAllText(configFilePath, defaultJson);
		}

		var json = File.ReadAllText(configFilePath);

		Data = JsonSerializer.Deserialize<JSONConfigData>(json);
	}

	public static void Save()
	{
		var json = JsonSerializer.Serialize(Data);
		File.WriteAllText(ConfigFilePath, json);
	}
}
