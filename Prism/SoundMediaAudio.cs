using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Prism;

internal static class SoundMediaAudio
{
	internal static byte[] ReadWem(BinaryReader reader)
	{
		var stream = reader.BaseStream;
		var bytes = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
		for (var offset = 0; offset <= bytes.Length - 12; offset++)
		{
			var header = bytes.AsSpan(offset);
			bool bigEndian = header[..4].SequenceEqual("RIFX"u8);
			if ((!bigEndian && !header[..4].SequenceEqual("RIFF"u8)) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
				continue;
			var size = bigEndian
				? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[4..])
				: System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
			if (size >= 4 && size + 8L <= header.Length)
				return header[..((int)size + 8)].ToArray();
		}
		throw new InvalidDataException("This SoundMedia entry contains no complete RIFF/WAVE payload.");
	}

	internal static async Task<byte[]> ConvertAsync(byte[] input, string format)
	{
		var directory = Directory.CreateTempSubdirectory("Prism-audio-");
		try
		{
			var wem = Path.Combine(directory.FullName, "source.wem");
			var wav = Path.Combine(directory.FullName, "decoded.wav");
			await File.WriteAllBytesAsync(wem, input).ConfigureAwait(false);
			await RunAsync(Path.Combine("vgmstream", "vgmstream-cli.exe"), "-i", "-o", wav, wem).ConfigureAwait(false);
			var output = wav;
			if (format != "wav")
			{
				output = Path.Combine(directory.FullName, "converted." + format);
				var codec = format switch
				{
					"flac" => "flac",
					"mp3" => "libmp3lame",
					_ => throw new ArgumentOutOfRangeException(nameof(format))
				};
				var arguments = new System.Collections.Generic.List<string>
				{
					"-nostdin", "-v", "error", "-i", wav, "-c:a", codec
				};
				if (format == "mp3")
					arguments.AddRange(["-ac", "2", "-q:a", "2"]);
				arguments.Add(output);
				await RunAsync("ffmpeg.exe", arguments.ToArray()).ConfigureAwait(false);
			}
			if (!File.Exists(output) || new FileInfo(output).Length == 0)
				throw new InvalidDataException("The audio converter produced no output.");
			return await File.ReadAllBytesAsync(output).ConfigureAwait(false);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	private static async Task RunAsync(string executable, params string[] arguments)
	{
		var path = Path.Combine(AppContext.BaseDirectory, "audio-tools", executable);
		if (!File.Exists(path))
			throw new FileNotFoundException("The Prism audio runtime is missing from this build.", path);
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardError = true, RedirectStandardOutput = true
		};
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);

		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the audio converter.");
		var error = process.StandardError.ReadToEndAsync();
		var output = process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync().ConfigureAwait(false);
		await Task.WhenAll(error, output).ConfigureAwait(false);
		if (process.ExitCode != 0)
			throw new InvalidDataException($"Audio conversion failed. {error.Result.Trim()} {output.Result.Trim()}".Trim());
	}
}
