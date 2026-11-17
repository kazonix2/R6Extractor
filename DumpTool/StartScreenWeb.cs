using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RainbowForge;

namespace DumpTool;

internal static class StartScreenWeb
{
	private sealed record Shape(string Path, string Fill, ushort? Bitmap, double[] Matrix);
	private sealed record EditText(double[] Bounds, ushort Font, double Size, string Text, int Alignment, string Color,
		int? LocalizationIndex = null);
	internal sealed record Glyph(float[] Uv, short X, short Y, short Width, short Height, short Advance);
	internal sealed record FireFont(string Atlas, short Ascent, Dictionary<char, Glyph> Glyphs);

	public static string[] Write(string output, string page, byte[] clip, Func<string, string> fileForUid,
		UiLocalization localization, Dictionary<string, FireFont> fontCache)
	{
		var movie = UefTimeline.Read(clip);
		var directory = Path.Combine(output, page);
		var isStartScreen = movie.RootClass == "R6_StartScreenMain";
		Directory.CreateDirectory(directory);
		var images = new Dictionary<ushort, string>();
		var text = new Dictionary<ushort, EditText>();
		var shapes = new Dictionary<ushort, Shape>();
		var fonts = new Dictionary<ushort, FireFont>();
		var unsupported = new List<string>();
		foreach (var (id, symbol) in movie.Symbols)
		{
			if (symbol.Type == "Bitmap")
			{
				if (fileForUid(symbol.Resource) is { } path)
					images[id] = "../" + path;
				else
					unsupported.Add($"Bitmap {id}: unresolved asset {symbol.Resource}.");
			}
			if (symbol.Type == "MorphShape")
				unsupported.Add($"MorphShape {id}: morph interpolation is unsupported.");
			try
			{
				if (symbol.Type == "Font")
				{
					if (!fontCache.TryGetValue(symbol.Resource, out var font))
					{
						var descriptor = fileForUid(symbol.Resource) ?? throw new InvalidDataException($"Missing Fire font {symbol.Resource}.");
						fontCache[symbol.Resource] = font = ReadFont(File.ReadAllBytes(Path.Combine(output, descriptor)), fileForUid);
					}
					fonts[id] = font;
				}
				if (symbol.Type is not ("Shape" or "EditText"))
					continue;
				using var reader = new BinaryReader(new MemoryStream(clip, symbol.Offset, symbol.Length, writable: false));
				reader.ReadUInt16();
				if (symbol.Type == "Shape")
					shapes[id] = ReadShape(reader, symbol.Tag);
				else
				{
					var field = ReadEditText(reader);
					text[id] = field with { LocalizationIndex = localization?.Find(field.Text, isStartScreen &&
						field.Text.StartsWith("PRESS ANY ", StringComparison.Ordinal)) };
				}
			}
			catch (Exception error) when (error is IOException or InvalidDataException)
			{
				unsupported.Add($"{symbol.Type} {id}: {error.Message}");
			}
		}
		var data = JsonSerializer.Serialize(new { movie, images, fonts, shapes, text, isStartScreen, unsupported },
			new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		File.WriteAllText(Path.Combine(directory, "data.js"), $"globalThis.startScreen = {data};\n");
		CopyTemplate("index.html", directory);
		return unsupported.ToArray();
	}

	public static void CopyTemplate(string file, string directory)
	{
		using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DumpTool.StartScreen.{file}");
		using var destination = File.Create(Path.Combine(directory, file));
		source.CopyTo(destination);
	}

	private static FireFont ReadFont(byte[] descriptor, Func<string, string> fileForUid)
	{
		using var reader = new BinaryReader(new MemoryStream(descriptor, writable: false));
		if (reader.ReadUInt32() != (uint)Magic.FireFontDescriptor || descriptor.Length < 52)
			throw new InvalidDataException("Invalid Fire font descriptor.");
		reader.BaseStream.Position = 48;
		var length = reader.ReadUInt32();
		var start = reader.BaseStream.Position;
		var end = start + length;
		if (end >= descriptor.Length)
			throw new InvalidDataException("Fire font metadata exceeds its resource.");
		reader.ReadUInt16();
		var flags = reader.ReadByte();
		if ((flags & 0x8C) != 0x84)
			throw new InvalidDataException("Unsupported Fire font table flags.");
		reader.ReadByte();
		var nameLength = reader.ReadByte();
		reader.BaseStream.Seek(nameLength, SeekOrigin.Current);
		var count = reader.ReadUInt16();
		var offsets = reader.BaseStream.Position;
		reader.BaseStream.Seek(count * 2L, SeekOrigin.Current);
		var codes = offsets + reader.ReadUInt16();
		if (codes + count * 4L + 6 > end)
			throw new InvalidDataException("Fire font code or metric table exceeds its metadata.");
		reader.BaseStream.Position = codes;
		var codePoints = new ushort[count];
		for (var i = 0; i < count; i++)
			codePoints[i] = reader.ReadUInt16();
		var ascent = reader.ReadInt16();
		reader.ReadInt16();
		reader.ReadInt16();
		var advances = new Dictionary<ushort, short>(count);
		foreach (var code in codePoints)
			advances[code] = reader.ReadInt16();

		reader.BaseStream.Position = end;
		var atlasUid = UefTimeline.ReadString(reader, descriptor.Length);
		var atlas = fileForUid(atlasUid) ?? throw new InvalidDataException($"Missing Fire font atlas {atlasUid}.");
		if (count * 27L > descriptor.Length - reader.BaseStream.Position)
			throw new InvalidDataException("Fire font atlas glyph table is truncated.");
		var glyphs = new Dictionary<char, Glyph>(count);
		for (var i = 0; i < count; i++)
		{
			var code = reader.ReadUInt16();
			if (reader.ReadByte() != 0)
				throw new InvalidDataException("Unsupported Fire font atlas page.");
			var uv = new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
			glyphs[(char)code] = new Glyph(uv, reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(),
				advances.GetValueOrDefault(code));
		}
		return new FireFont("../" + atlas, ascent, glyphs);
	}

	private static EditText ReadEditText(BinaryReader reader)
	{
		var bounds = ReadRect(reader);
		var flags = (reader.ReadByte() << 8) | reader.ReadByte();
		var end = reader.BaseStream.Length;
		var font = (flags & 0x0100) != 0 ? reader.ReadUInt16() : (ushort)0;
		if ((flags & 0x0080) != 0)
			UefTimeline.ReadString(reader, end);
		var size = font != 0 ? reader.ReadUInt16() / 20d : 0;
		var color = (flags & 0x0400) != 0 ? "#" + Convert.ToHexString(reader.ReadBytes(4)) : "#FFFFFF";
		if ((flags & 0x0200) != 0)
			reader.ReadUInt16();
		var alignment = 0;
		if ((flags & 0x0020) != 0)
		{
			alignment = reader.ReadByte();
			reader.ReadBytes(8);
		}
		UefTimeline.ReadString(reader, end);
		var text = (flags & 0x8000) != 0 ? UefTimeline.ReadString(reader, end) : string.Empty;
		return new EditText(bounds, font, size, WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", string.Empty)), alignment, color);
	}

	private static double[] ReadRect(BinaryReader reader)
	{
		var bits = new UefTimeline.BitFields(reader);
		var width = bits.Read(5);
		return [bits.Signed(width) / 20d, bits.Signed(width) / 20d, bits.Signed(width) / 20d, bits.Signed(width) / 20d];
	}

	private static Shape ReadShape(BinaryReader reader, int tag)
	{
		ReadRect(reader);
		if (tag == 83)
		{
			ReadRect(reader);
			reader.ReadByte();
		}
		if (reader.ReadByte() != 1)
			throw new InvalidDataException("Only single-fill shapes are supported.");
		var style = reader.ReadByte();
		var fill = style == 0 ? "#" + Convert.ToHexString(reader.ReadBytes(tag is 32 or 83 ? 4 : 3)) : null;
		ushort? bitmap = style is >= 0x40 and <= 0x43 ? reader.ReadUInt16() : null;
		var matrix = bitmap.HasValue ? UefTimeline.ReadMatrix(reader) : null;
		if ((fill == null && !bitmap.HasValue) || reader.ReadByte() != 0)
			throw new InvalidDataException("Unsupported shape style.");

		var bits = new UefTimeline.BitFields(reader);
		var fillBits = bits.Read(4);
		var lineBits = bits.Read(4);
		var x = 0;
		var y = 0;
		var path = new StringBuilder();
		while (true)
		{
			if (bits.Read(1) == 0)
			{
				var flags = bits.Read(5);
				if (flags == 0)
					break;
				if ((flags & 1) != 0)
				{
					var count = bits.Read(5);
					x = bits.Signed(count);
					y = bits.Signed(count);
					path.Append(CultureInfo.InvariantCulture, $"M{x / 20d} {y / 20d} ");
				}
				if ((flags & 2) != 0) bits.Read(fillBits);
				if ((flags & 4) != 0) bits.Read(fillBits);
				if ((flags & 8) != 0) bits.Read(lineBits);
				if ((flags & 16) != 0) throw new InvalidDataException("Mid-shape style changes are unsupported.");
			}
			else
			{
				var straight = bits.Read(1) != 0;
				var count = bits.Read(4) + 2;
				if (straight)
				{
					var both = bits.Read(1) != 0;
					var vertical = !both && bits.Read(1) != 0;
					if (both || !vertical) x += bits.Signed(count);
					if (both || vertical) y += bits.Signed(count);
					path.Append(CultureInfo.InvariantCulture, $"L{x / 20d} {y / 20d} ");
				}
				else
				{
					var controlX = x + bits.Signed(count);
					var controlY = y + bits.Signed(count);
					x = controlX + bits.Signed(count);
					y = controlY + bits.Signed(count);
					path.Append(CultureInfo.InvariantCulture, $"Q{controlX / 20d} {controlY / 20d} {x / 20d} {y / 20d} ");
				}
			}
		}
		return new Shape(path.ToString(), fill, bitmap, matrix);
	}
}
