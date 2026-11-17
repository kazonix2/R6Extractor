using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;
using RainbowForge;

namespace Prism;

partial class PrismForm
{
	private readonly FlowLayoutPanel _audioPreview = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
	private readonly ToolStripMenuItem _audioBatchMenu = new("Selected &SoundMedia");
	private SoundPlayer _audioPlayer;
	private MemoryStream _audioPlaybackStream;
	private int _audioPreviewGeneration;

	private async Task ExportSelectedAudioAsync(string format)
	{

		var assets = _assetList.SelectedObjects.Cast<object>().Select(GetAssetStream)
			.Where(asset => asset != null && MagicHelper.GetFiletype(asset.MetaData.Magic) == AssetType.Sound)
			.ToArray();
		if (assets.Length == 0)
		{
			MessageBox.Show(this, "Select at least one SoundMedia entry.", "Export SoundMedia");
			return;
		}

		using var dialog = new FolderBrowserDialog
		{
			Description = $"Choose a folder for {assets.Length:N0} {format.ToUpperInvariant()} files.",
			UseDescriptionForTitle = true
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
			return;

		_audioBatchMenu.Enabled = false;
		var failures = new System.Collections.Generic.List<string>();
		var exported = 0;
		var skipped = 0;
		var processed = 0;
		try
		{
			foreach (var asset in assets)
			{
				if (IsDisposed)
					break;
				_statusForgeInfo.Text = $"Exporting SoundMedia {++processed:N0}/{assets.Length:N0}...";
				var path = Path.Combine(dialog.SelectedPath, $"{asset.MetaData.Uid:X16}.{format}");
				if (File.Exists(path))
				{
					skipped++;
					continue;
				}
				try
				{
					byte[] wem;
					using (var reader = asset.StreamProvider.Invoke())
						wem = SoundMediaAudio.ReadWem(reader);
					var audio = await SoundMediaAudio.ConvertAsync(wem, format);
					await WriteBatchAudioAsync(path, audio);
					exported++;
				}
				catch (Exception error)
				{
					failures.Add($"{asset.MetaData.Uid:X16}: {error.Message}");
				}
			}
			if (IsDisposed)
				return;
			var report = $"Exported {exported:N0} of {assets.Length:N0} files. Existing files skipped: {skipped:N0}. Failed: {failures.Count:N0}.";
			_statusForgeInfo.Text = report;
			if (failures.Count > 0)
				report += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(5));
			MessageBox.Show(this, report, "Export SoundMedia");
		}
		finally
		{
			if (!IsDisposed)
				_audioBatchMenu.Enabled = true;
		}
	}

	private static async Task WriteBatchAudioAsync(string path, byte[] audio)
	{
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporaryPath, audio);
			File.Move(temporaryPath, path);
		}
		finally
		{
			File.Delete(temporaryPath);
		}
	}

	private void StopAudioPreview()
	{
		_audioPlayer?.Stop();
		_audioPlayer?.Dispose();
		_audioPlayer = null;
		_audioPlaybackStream?.Dispose();
		_audioPlaybackStream = null;
	}

	private void PreviewSoundMedia(AssetStream asset)
	{
		ResetAudioPreview();
		var generation = _audioPreviewGeneration;
		var play = new Button { Text = "Play", AutoSize = true };
		var stop = new Button { Text = "Stop", AutoSize = true };
		var export = new Button { Text = "Export…", AutoSize = true };
		var status = new Label { AutoSize = true, Text = asset.MetaData.Filename };
		_audioPreview.Controls.AddRange([play, stop, export, status]);
		SetPreviewPanel(_audioPreview);

		byte[] source = null;
		byte[] decoded = null;
		var playRequest = 0;
		byte[] GetSource()
		{
			if (source != null)
				return source;
			using var stream = asset.StreamProvider.Invoke();
			return source = SoundMediaAudio.ReadWem(stream);
		}

		play.Click += async (_, _) =>
		{
			play.Enabled = false;
			var request = ++playRequest;
			try
			{
				decoded ??= await SoundMediaAudio.ConvertAsync(GetSource(), "wav");
				if (generation != _audioPreviewGeneration || request != playRequest)
					return;
				StopAudioPreview();
				_audioPlaybackStream = new MemoryStream(decoded, writable: false);
				_audioPlayer = new SoundPlayer(_audioPlaybackStream);
				_audioPlayer.Load();
				_audioPlayer.Play();
				status.Text = asset.MetaData.Filename;
			}
			catch (Exception error)
			{
				if (generation == _audioPreviewGeneration)
					status.Text = error.Message;
			}
			finally
			{
				if (generation == _audioPreviewGeneration)
					play.Enabled = true;
			}
		};

		stop.Click += (_, _) =>
		{
			playRequest++;
			StopAudioPreview();
		};
		export.Click += async (_, _) =>
		{
			using var dialog = new SaveFileDialog
			{
				Filter = "WAV audio (*.wav)|*.wav|FLAC audio (*.flac)|*.flac|MP3 audio (*.mp3)|*.mp3",
				FileName = $"{asset.MetaData.Uid:X16}",
				AddExtension = true
			};
			if (dialog.ShowDialog(this) != DialogResult.OK)
				return;
			var format = dialog.FilterIndex switch { 2 => "flac", 3 => "mp3", _ => "wav" };
			var path = dialog.FileName;
			if (!string.Equals(Path.GetExtension(path), "." + format, StringComparison.OrdinalIgnoreCase))
			{
				status.Text = $"Use the .{format} extension for this export format.";
				return;
			}
			export.Enabled = false;
			try
			{
				var data = format == "wav" && decoded != null ? decoded :
					await SoundMediaAudio.ConvertAsync(GetSource(), format);
				await File.WriteAllBytesAsync(path, data);
				if (generation == _audioPreviewGeneration)
					status.Text = $"Saved {Path.GetFileName(path)}";
			}
			catch (Exception error)
			{
				if (generation == _audioPreviewGeneration)
					status.Text = error.Message;
			}
			finally
			{
				if (generation == _audioPreviewGeneration)
					export.Enabled = true;
			}
		};
	}

	private void ResetAudioPreview()
	{
		_audioPreviewGeneration++;
		StopAudioPreview();
		while (_audioPreview.Controls.Count > 0)
			_audioPreview.Controls[0].Dispose();
	}
}
