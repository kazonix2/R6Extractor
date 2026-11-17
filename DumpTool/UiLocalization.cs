using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RainbowForge;
using RainbowForge.Components;
using RainbowForge.Core;
using RainbowForge.Core.Container;

namespace DumpTool;

internal sealed class UiLocalization
{
	private const ulong EnglishUid = 0xD7AF9901;
	private readonly string[] _strings;
	private readonly Dictionary<string, int> _unique = new(StringComparer.Ordinal);

	private UiLocalization(string[] strings)
	{
		_strings = strings;
		for (var i = 0; i < strings.Length; i++)
		{
			if (strings[i].Length < 2)
				continue;
			if (!_unique.TryAdd(strings[i], i))
				_unique[strings[i]] = -1;
		}
	}

	public static UiLocalization Read(string game)
	{
		var archive = Path.Combine(game, "datapc64.forge");
		if (!File.Exists(archive))
			return null;
		var forge = Forge.GetForge(archive);
		using (forge.Stream)
		{
			if (!forge.Entries.Any(entry => entry.Uid == EnglishUid) || forge.GetContainer(EnglishUid) is not ForgeAsset asset)
				return null;
			using var stream = asset.GetDataStream(forge);
			FileMetaData.Read(stream, forge.Version);
			if (stream.ReadUInt32() != (uint)Magic.LocalizationPackage)
				return null;
			stream.ReadUInt32();
			if (stream.ReadUInt32() != 0 || stream.ReadUInt32() != 1)
				return null;
			stream.BaseStream.Seek(8, SeekOrigin.Current);
			return new UiLocalization(CompressedLocalizationData.Read(stream).Strings);
		}
	}

	public int? Find(string authoredText, bool isPcStartPrompt)
	{
		if (isPcStartPrompt && _strings.Length > 4562 && _strings[4562] == "PRESS ANY KEY" &&
		    _strings[4561].Contains("Rainbow Six® Siege", StringComparison.Ordinal))
			return 4562;
		return _unique.TryGetValue(authoredText, out var index) && index >= 0 ? index : null;
	}

	public void Write(string directory)
	{
		var json = JsonSerializer.Serialize(new { language = "en", uid = EnglishUid.ToString("X16"), strings = _strings });
		File.WriteAllText(Path.Combine(directory, "localization.js"), $"globalThis.siegeLocalization = {json};\n");
	}
}
