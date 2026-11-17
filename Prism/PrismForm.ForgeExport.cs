using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using RainbowForge;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using RainbowForge.Dump;

namespace Prism;

partial class PrismForm
{
	private readonly ToolStripMenuItem _multiForgeExportMenu = new("Multiple &Forge Files");

	private void SetupMultiForgeExport()
	{
		_multiForgeExportMenu.DropDownItems.Add("Assets…", null, async (_, _) => await ExportForgesAsync(null));
		foreach (var format in new[] { "wav", "flac", "mp3" })
			_multiForgeExportMenu.DropDownItems.Add($"SoundMedia as {format.ToUpperInvariant()}…", null,
				async (_, _) => await ExportForgesAsync(format));
	}

	private async Task ExportForgesAsync(string audioFormat)
	{
		using var input = new OpenFileDialog { Filter = "Forge files (*.forge)|*.forge", Multiselect = true };
		if (input.ShowDialog(this) != DialogResult.OK)
			return;
		using var output = new FolderBrowserDialog { Description = "Choose the export destination." };
		if (output.ShowDialog(this) != DialogResult.OK)
			return;

		_multiForgeExportMenu.Enabled = false;
		try
		{
			var files = input.FileNames;
			var destination = output.SelectedPath;
			var progress = new Progress<string>(message =>
			{
				if (!IsDisposed)
					_statusForgeInfo.Text = message;
			});
			var result = await Task.Run(() => ExportForgeFilesAsync(files, destination, audioFormat, progress));
			if (!IsDisposed)
			{
				_statusForgeInfo.Text = "Forge export finished.";
				MessageBox.Show(this, result, "Export Forge Files");
			}
		}
		finally
		{
			if (!IsDisposed)
				_multiForgeExportMenu.Enabled = true;
		}
	}

	private static async Task<string> ExportForgeFilesAsync(string[] files, string destination, string audioFormat,
		IProgress<string> progress)
	{
		var exported = 0;
		var failed = 0;
		var errors = new System.Collections.Generic.List<string>();
		void RecordFailure(string name, Exception error)
		{
			failed++;
			if (errors.Count < 5)
				errors.Add($"{name}: {error.Message}");
		}

		foreach (var file in files)
		{
			var name = Path.GetFileName(file);
			progress.Report($"Exporting {name}...");
			try
			{
				var forge = Forge.GetForge(file);
				using var stream = forge.Stream;
				var folder = Path.Combine(destination, name);
				for (var suffix = 2; Directory.Exists(folder) || File.Exists(folder); suffix++)
					folder = Path.Combine(destination, $"{name} ({suffix})");
				Directory.CreateDirectory(folder);
				foreach (var entry in forge.Entries)
				{
					try
					{
						var container = forge.GetContainer(entry.Uid);
						if (container is not (ForgeAsset or RawRiffAsset))
							continue;
						if (audioFormat == null)
							DumpHelper.Dump(forge, entry, folder);
						else
						{
							if (MagicHelper.GetFiletype(AssetTypeDetector.DetectMagic(forge, entry)) != AssetType.Sound)
								continue;
							using var reader = container is RawRiffAsset riff ? riff.GetDataStream(forge) :
								((ForgeAsset)container).GetDataStream(forge);
							var audio = await SoundMediaAudio.ConvertAsync(SoundMediaAudio.ReadWem(reader), audioFormat);
							await WriteBatchAudioAsync(Path.Combine(folder, $"{entry.Uid:X16}.{audioFormat}"), audio);
						}
						exported++;
					}
					catch (Exception error)
					{
						RecordFailure($"{name} / {entry.Uid:X16}", error);
					}
				}
			}
			catch (Exception error)
			{
				RecordFailure(name, error);
			}
		}
		return $"Processed {exported:N0} asset entries. Failed: {failed:N0}."
			+ (errors.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors));
	}
}
