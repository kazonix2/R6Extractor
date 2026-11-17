using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommandLine;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using RainbowForge.Image;

namespace DumpTool;

[Verb("rip-ui", HelpText = "Export Siege UI resources and an offline HTML/CSS/JS asset kit.")]
internal sealed class RipUiCommand
{
	[Value(0, Required = true, HelpText = "Game directory containing datapc64_ui_playgo.forge.")]
	public string GameDirectory { get; set; }

	[Value(1, Required = true, HelpText = "New output directory for the web asset kit.")]
	public string OutputDirectory { get; set; }

	public static void Run(RipUiCommand args)
	{
		try
		{
			var input = Path.GetFullPath(args.GameDirectory);
			var output = Path.GetFullPath(args.OutputDirectory);
			var relative = Path.GetRelativePath(input, output);
			if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				throw new IOException("Choose an output directory outside the game installation.");
			if (Directory.Exists(output) || File.Exists(output))
				throw new IOException("Choose a new output directory.");
			new UiExport(output).Write(input);
		}
		catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
		                             or DllNotFoundException or BadImageFormatException or NotImplementedException)
		{
			Console.Error.WriteLine($"UI export failed: {error.Message}");
			Environment.ExitCode = 1;
		}
	}

	private sealed record Resource(string Uid, string Type, string Name, string File, string Format, string[] References,
		int? Width = null, int? Height = null, string Layout = null, string Screen = null, string[] ScreenDiagnostics = null);

	private sealed record Group(string Allocation, string Offset, string[] Members);

	private sealed class UiExport(string output)
	{
		private static readonly Regex TextUid = new(@"(?<![0-9A-Za-z])0x[0-9A-Fa-f]{1,16}(?![0-9A-Za-z])", RegexOptions.CultureInvariant);
		private readonly Dictionary<ulong, Resource> _resources = new();
		private readonly HashSet<ulong> _imageUids = new();
		private readonly List<Group> _groups = new();
		private readonly List<string> _diagnostics = new();

		public void Write(string game)
		{
			var forge = Forge.GetForge(Path.Combine(game, "datapc64_ui_playgo.forge"));
			using (forge.Stream)
			{
				if (forge.Version is not (29 or 34))
					throw new InvalidDataException($"Unsupported UI Forge version {forge.Version}.");
				foreach (var entry in forge.Entries)
					ReadGroup(forge, entry);
			}
			if (_resources.Count == 0)
				throw new InvalidDataException(_diagnostics.FirstOrDefault() ?? "No UI resources were found.");

			ReadImages(game, forge.Version);
			if (forge.Version == 29)
				WriteScreens(game);
			foreach (var (uid, resource) in _resources.ToArray())
			{
				if (resource.Format == "UEF" || resource.Format.StartsWith("PHX", StringComparison.Ordinal))
				{
					var content = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(output, resource.File)));
					var references = TextUid.Matches(content).Select(match => match.Value[2..].ToUpperInvariant().PadLeft(16, '0'))
						.Where(reference => reference != resource.Uid && _resources.ContainsKey(Convert.ToUInt64(reference, 16))).Distinct().ToArray();
					_resources[uid] = resource with { References = references };
				}
			}
			var missingImages = _imageUids.Where(uid => !_resources.ContainsKey(uid)).Order().Select(FormatUid).ToArray();
			var manifest = JsonSerializer.Serialize(new
			{
				forgeVersion = forge.Version,
				resources = _resources.Values,
				groups = _groups,
				missingImages,
				diagnostics = _diagnostics
			}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
			File.WriteAllText(Path.Combine(output, "ui.json"), manifest);
			File.WriteAllText(Path.Combine(output, "ui-data.js"), $"globalThis.siegeUi = {manifest};\n");
			File.WriteAllLines(Path.Combine(output, "fonts.css"), _resources.Values
				.Where(resource => resource.Format is "TrueType" or "OpenType")
				.Select(resource => $"@font-face {{ font-family: 'Siege-{resource.Uid}'; src: url('{resource.File}'); }}"));
			foreach (var file in new[] { "index.html", "ui.css", "ui.js" })
			{
				using var template = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DumpTool.UiWeb.{file}");
				using var destination = File.Create(Path.Combine(output, file));
				template.CopyTo(destination);
			}
			Console.WriteLine($"Exported {_resources.Count:N0} UI resources and {_resources.Values.Count(resource => resource.Format == "PNG"):N0} PNGs. {missingImages.Length:N0} texture references unresolved. {_diagnostics.Count:N0} diagnostics.");
		}

		private void WriteScreens(string game)
		{
			var clips = _resources.Where(pair => pair.Value.Format == "UEF").ToArray();
			if (clips.Length == 0)
				return;
			UiLocalization localization = null;
			try
			{
				localization = UiLocalization.Read(game);
				localization?.Write(output);
			}
			catch (Exception error) when (error is IOException or InvalidDataException)
			{
				_diagnostics.Add($"Localization: {error.Message}");
			}
			if (localization == null)
			{
				_diagnostics.Add("English UI bank unavailable. Screen text uses authored defaults.");
				File.WriteAllText(Path.Combine(output, "localization.js"), "globalThis.siegeLocalization = { strings: [] };\n");
			}
			StartScreenWeb.CopyTemplate("screen.css", output);
			StartScreenWeb.CopyTemplate("screen.js", output);
			var fonts = new Dictionary<string, StartScreenWeb.FireFont>(StringComparer.OrdinalIgnoreCase);
			foreach (var (uid, resource) in clips)
			{
				var page = resource.Name == "R6_StartScreenMain" ? "start-screen" : $"screen-{resource.Uid}";
				try
				{
					var diagnostics = StartScreenWeb.Write(output, page, File.ReadAllBytes(Path.Combine(output, resource.File)),
						ResolveStartScreenAsset, localization, fonts);
					_resources[uid] = resource with { Screen = $"{page}/index.html", ScreenDiagnostics = diagnostics };
				}
				catch (Exception error) when (error is IOException or InvalidDataException)
				{
					_diagnostics.Add($"Screen {resource.Uid}: {error.Message}");
				}
			}
		}

		private void ReadGroup(Forge forge, Entry entry)
		{
			try
			{
				if (forge.GetContainer(entry.Uid) is not ForgeAsset { HasMeta: true } asset)
					return;
				using var payload = asset.GetDataStream(forge);
				var archive = FlatArchive.Read(forge, asset, payload);
				foreach (var member in archive.Entries)
				{
					if (_resources.ContainsKey(member.MetaData.Uid))
						continue;
					payload.BaseStream.Position = member.PayloadOffset + member.HeaderLength;
					var bytes = payload.ReadBytes(member.PayloadLength - member.HeaderLength);
					ReadResource(member.MetaData, bytes, forge.Version);
				}
				_groups.Add(new Group(FormatUid(entry.Uid), $"0x{entry.Offset:X}", archive.Entries.Select(member => FormatUid(member.MetaData.Uid)).ToArray()));
			}
			catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or NotImplementedException)
			{
				_diagnostics.Add($"{Path.GetFileName(forge.SourcePath)} at 0x{entry.Offset:X}: {error.Message}");
			}
		}

		private void ReadResource(FileMetaData metadata, byte[] bytes, uint version)
		{
			var uid = FormatUid(metadata.Uid);
			var type = (Magic)metadata.FileType;
			var format = "Binary";
			var extension = "bin";
			var name = metadata.FileName;
			string layout = null;
			ReadOnlySpan<byte> content = bytes;
			ulong[] references = [];
			if (version == 29 && type == Magic.FireData && bytes.Length >= 12 && bytes.AsSpan(8, 3).SequenceEqual("UEF"u8))
			{
				content = ReadSection(bytes, 4, 8);
				(format, extension) = ("UEF", "uef");
				try
				{
					var timeline = UefTimeline.Read(content.ToArray());
					name = string.IsNullOrWhiteSpace(timeline.RootClass) ? name : timeline.RootClass;
					layout = WriteFile("layouts", uid, "json", JsonSerializer.SerializeToUtf8Bytes(timeline,
						new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
				}
				catch (Exception error) when (error is IOException or InvalidDataException)
				{
					_diagnostics.Add($"UEF timeline {uid}: {error.Message}");
				}
			}
			else if (version == 34 && bytes.Length >= 49 && bytes.AsSpan(44, 3).SequenceEqual("PHX"u8) &&
			         BitConverter.ToUInt32(bytes, 12) == 0x18808763)
			{
				content = ReadSection(bytes, 40, 44);
				(format, extension) = (Encoding.ASCII.GetString(bytes, 44, 5), "phx");
			}
			else if (type == Magic.PhoenixSfntFont && bytes.Length >= 20 &&
			         (bytes.AsSpan(8, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }) || bytes.AsSpan(8, 4).SequenceEqual("OTTO"u8)))
			{
				content = ReadSection(bytes, 4, 8);
				(format, extension) = bytes[8] == 0 ? ("TrueType", "ttf") : ("OpenType", "otf");
			}
			else if ((version == 29 && type == Magic.TextureMapSpec && bytes.Length >= 16) ||
			         (version == 34 && type == Magic.PhoenixImage && bytes.Length >= 19))
			{
				references = [BitConverter.ToUInt64(bytes, version == 29 ? 4 : 11)];
			}
			else if (version == 29 && type == Magic.TextureMap && bytes.Length >= 116 && bytes[^1] == 1)
			{
				// Blood Orchid ships one bank image per UI map. The other slots are reserved.
				references = ReadImageUids(bytes, bytes.Length - 41, 1);
			}
			else if (version == 34 && type == Magic.PhoenixTexture && bytes.Length >= 185 && BitConverter.ToUInt32(bytes, 57) == 0x6F697D77)
			{
				references = ReadImageUids(bytes, 105, 10);
			}

			var file = WriteFile("resources", uid, extension, content);
			var typeName = Enum.IsDefined(type) ? type.ToString() : $"{metadata.FileType:X8}";
			_resources.Add(metadata.Uid, new Resource(uid, typeName, name, file, format,
				references.Where(reference => reference != 0).Select(FormatUid).ToArray(), Layout: layout));
		}

		private ulong[] ReadImageUids(byte[] bytes, int start, int count)
		{
			var uids = new HashSet<ulong>();
			for (var i = 0; i < count; i++)
			{
				var uid = BitConverter.ToUInt64(bytes, start + i * sizeof(ulong));
				if (uid != 0)
					uids.Add(uid);
			}
			_imageUids.UnionWith(uids);
			return uids.ToArray();
		}

		private string ResolveStartScreenAsset(string reference)
		{
			if (!reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
			    !ulong.TryParse(reference.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var uid))
				return null;
			for (var depth = 0; depth < 4 && _resources.TryGetValue(uid, out var resource); depth++)
			{
				if (resource.Format == "PNG" || resource.Type == nameof(Magic.FireFontDescriptor))
					return resource.File;
				if (resource.References.Length == 0)
					break;
				uid = Convert.ToUInt64(resource.References[0], 16);
			}
			return null;
		}

		private static ReadOnlySpan<byte> ReadSection(byte[] bytes, int lengthOffset, int start)
		{
			var length = BitConverter.ToUInt32(bytes, lengthOffset);
			if (length > bytes.Length - start)
				throw new InvalidDataException("UI section exceeds its resource.");
			return bytes.AsSpan(start, (int)length);
		}

		private void ReadImages(string game, uint version)
		{
			var pattern = version == 29 ? "*_textures[0-4]" : "*_guitextures[01]";
			var bankSuffix = new Regex("^" + pattern.Replace("*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			foreach (var file in Directory.EnumerateFiles(game, "*.forge").Where(path => bankSuffix.IsMatch(Path.GetFileNameWithoutExtension(path))).Order())
			{
				try
				{
					var bank = Forge.GetForge(file);
					using (bank.Stream)
					{
						foreach (var entry in bank.Entries)
						{
							if (version == 29 && (!_imageUids.Contains(entry.Uid) || _resources.ContainsKey(entry.Uid)))
								continue;
							ReadImage(bank, entry);
						}
					}
				}
				catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or NotImplementedException)
				{
					_diagnostics.Add($"{Path.GetFileName(file)}: {error.Message}");
				}
			}
		}

		private void ReadImage(Forge bank, Entry entry)
		{
			try
			{
				if (bank.GetContainer(entry.Uid) is not ForgeAsset asset)
					return;
				if (bank.Version >= 34)
				{
					if (!asset.HasMeta)
						return;
					using var metadata = asset.GetMetaDataStream(bank);
					if (metadata.BaseStream.Length < 12 || metadata.ReadUInt32() != 1)
						return;
					var logicalUid = metadata.ReadUInt64();
					if (!_imageUids.Contains(logicalUid) || _resources.ContainsKey(logicalUid))
						return;
				}
				using var stream = asset.GetDataStream(bank);
				var texture = Texture.Read(stream, bank.Version);
				var uid = texture.MetaData.Uid;
				if (!_imageUids.Contains(uid) || _resources.ContainsKey(uid))
					return;
				if (texture.Width <= 0 || texture.Height <= 0 || texture.Width > 16384 || texture.Height > 16384 ||
				    texture.Mips is 0 or > 16 || !DdsHelper.TextureFormats.ContainsKey(texture.TexFormat))
					throw new InvalidDataException($"Texture {FormatUid(uid)} has unsupported dimensions or format.");
				using var dds = DdsHelper.GetDdsStream(texture, texture.ReadSurfaceBytes(stream));
				var format = "DDS";
				var id = FormatUid(uid);
				string file = null;
				if (OperatingSystem.IsWindows())
				{
					try
					{
						using var image = Pfim.Pfim.FromStream(dds);
						using var png = EncodePng(image, texture.TexFormat == 7);
						file = WriteFile("images", id, "png", png.GetBuffer().AsSpan(0, (int)png.Length));
						format = "PNG";
					}
					catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException or ExternalException)
					{
						_diagnostics.Add($"Texture {id} saved as DDS: {error.Message}");
					}
				}
				file ??= WriteFile("images", id, "dds", dds.GetBuffer().AsSpan(0, (int)dds.Length));
				_resources.Add(uid, new Resource(id, "Image", texture.MetaData.FileName, file, format, [], texture.Width, texture.Height));
			}
			catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or NotImplementedException)
			{
				_diagnostics.Add($"{Path.GetFileName(bank.SourcePath)} at 0x{entry.Offset:X}: {error.Message}");
			}
		}

		[SupportedOSPlatform("windows")]
		private static MemoryStream EncodePng(Pfim.IImage image, bool alphaOnly)
		{
			var pixels = image.Data;
			var stride = image.Stride;
			var pixelFormat = image.Format switch
			{
				Pfim.ImageFormat.Rgba32 or Pfim.ImageFormat.Rgb8 => PixelFormat.Format32bppArgb,
				Pfim.ImageFormat.Rgb24 => PixelFormat.Format24bppRgb,
				_ => throw new NotSupportedException($"Unsupported PNG source format {image.Format}.")
			};
			if (image.Format == Pfim.ImageFormat.Rgb8)
			{
				stride = checked(image.Width * 4);
				pixels = new byte[checked(stride * image.Height)];
				for (var y = 0; y < image.Height; y++)
				for (var x = 0; x < image.Width; x++)
				{
					var value = image.Data[y * image.Stride + x];
					var index = y * stride + x * 4;
					pixels[index] = pixels[index + 1] = pixels[index + 2] = alphaOnly ? (byte)255 : value;
					pixels[index + 3] = alphaOnly ? value : (byte)255;
				}
			}
			var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
			try
			{
				using var bitmap = new Bitmap(image.Width, image.Height, stride, pixelFormat, handle.AddrOfPinnedObject());
				var png = new MemoryStream();
				bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
				return png;
			}
			finally { handle.Free(); }
		}

		private string WriteFile(string directory, string name, string extension, ReadOnlySpan<byte> bytes)
		{
			Directory.CreateDirectory(Path.Combine(output, directory));
			var relative = $"{directory}/{name}.{extension}";
			using var file = File.Create(Path.Combine(output, relative));
			file.Write(bytes);
			return relative;
		}

		private static string FormatUid(ulong uid) => uid.ToString("X16");
	}
}
