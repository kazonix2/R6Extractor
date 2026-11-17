using System;
using CommandLine;
using RainbowForge;
using RainbowForge.Core;

namespace DumpTool
{
	[Verb("list", HelpText = "List all of the UIDs in the given container")]
	internal class ListCommand
	{
		[Value(0, HelpText = "The forge file to reference")]
		public string ForgeFilename { get; set; }

		public static void Run(ListCommand args)
		{
			var forge = Forge.GetForge(args.ForgeFilename);

			foreach (var forgeEntry in forge.Entries)
			{
				// Forges v31+ have missing or zeroed metadata tables, so fall back to detecting the type
				var magic = forgeEntry.MetaData?.FileType is { } ft && ft != 0
					? MagicHelper.GetFiletype(ft)
					: AssetTypeDetector.DetectAssetType(forge, forgeEntry);
				Console.WriteLine($"{forgeEntry.Uid}: {magic}");
			}
		}
	}
}