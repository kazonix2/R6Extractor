using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DumpTool;

// UEF keeps the SWF stage header and tag framing. Tags 246 and 251 refer to
// texture and font assets stored outside the clip.
internal static class UefTimeline
{
	internal sealed record Movie(int X, int Y, int Width, int Height, float FramesPerSecond, string RootClass, string Background,
		Timeline Stage, Dictionary<ushort, Timeline> Sprites, Dictionary<ushort, Symbol> Symbols,
		Dictionary<ushort, string> Classes);
	internal sealed record Timeline(ushort FrameCount, List<Frame> Frames);
	internal sealed record Frame(string Label, List<Placement> Changes);
	internal sealed record Placement(ushort Depth, bool Remove, bool Move, ushort? Character, string Name,
		double[] Matrix, ushort? ClipDepth, int[] ColorTransform, ushort? Ratio);
	internal sealed record Symbol(string Type, int Offset, int Length, string Resource, ushort? Width = null, ushort? Height = null, int Tag = 0);

	public static Movie Read(byte[] data)
	{
		if (data.Length < 13 || !data.AsSpan(0, 3).SequenceEqual("UEF"u8))
			throw new InvalidDataException("Invalid UEF clip header.");
		using var reader = new BinaryReader(new MemoryStream(data, writable: false));
		reader.BaseStream.Position = 8;
		var bits = new BitFields(reader);
		var width = bits.Read(5);
		var x = bits.Signed(width);
		var right = bits.Signed(width);
		var y = bits.Signed(width);
		var bottom = bits.Signed(width);
		var fps = reader.ReadUInt16() / 256f;
		var frames = reader.ReadUInt16();
		var sprites = new Dictionary<ushort, Timeline>();
		var symbols = new Dictionary<ushort, Symbol>();
		var classes = new Dictionary<ushort, string>();
		var background = "#000000";
		var stage = ReadTimeline(reader, reader.BaseStream.Length, frames, sprites, symbols, classes, 0, ref background);
		return new Movie(x, y, right - x, bottom - y, fps, classes.GetValueOrDefault((ushort)0), background, stage, sprites, symbols, classes);
	}

	private static Timeline ReadTimeline(BinaryReader reader, long limit, ushort declaredFrames, Dictionary<ushort, Timeline> sprites,
		Dictionary<ushort, Symbol> symbols, Dictionary<ushort, string> classes, int nesting, ref string background)
	{
		if (nesting > 64)
			throw new InvalidDataException("UEF sprite nesting is too deep.");
		var stream = reader.BaseStream;
		var frames = new List<Frame>();
		var changes = new List<Placement>();
		string label = null;
		while (stream.Position < limit)
		{
			if (limit - stream.Position < 2)
				throw new InvalidDataException("Truncated UEF tag.");
			var header = reader.ReadUInt16();
			var tag = header >> 6;
			var length = (uint)(header & 63);
			if (length == 63)
				length = reader.ReadUInt32();
			var start = stream.Position;
			var end = start + length;
			if (end > limit)
				throw new InvalidDataException("UEF tag extends beyond its timeline.");
			switch (tag)
			{
				case 0:
					return new Timeline(declaredFrames, frames);
				case 1:
					frames.Add(new Frame(label, changes));
					label = null;
					changes = new List<Placement>();
					break;
				case 9 when nesting == 0 && length == 3:
					background = "#" + Convert.ToHexString(reader.ReadBytes(3));
					break;
				case 26:
				case 70:
					changes.Add(ReadPlacement(reader, end, tag == 70));
					break;
				case 28:
					changes.Add(new Placement(reader.ReadUInt16(), true, false, null, null, null, null, null, null));
					break;
				case 39:
					var id = reader.ReadUInt16();
					var framesInSprite = reader.ReadUInt16();
					sprites[id] = ReadTimeline(reader, end, framesInSprite, sprites, symbols, classes, nesting + 1, ref background);
					break;
				case 43:
					label = ReadString(reader, end);
					break;
				case 76:
					var count = reader.ReadUInt16();
					for (var i = 0; i < count; i++)
						classes[reader.ReadUInt16()] = ReadString(reader, end);
					break;
				case 246:
					var bitmap = reader.ReadUInt16();
					var bitmapWidth = reader.ReadUInt16();
					var bitmapHeight = reader.ReadUInt16();
					symbols[bitmap] = new Symbol("Bitmap", (int)start, (int)length, ReadString(reader, end), bitmapWidth, bitmapHeight);
					break;
				case 251:
					symbols[reader.ReadUInt16()] = new Symbol("Font", (int)start, (int)length, ReadString(reader, end));
					break;
				case 2 or 22 or 32 or 83:
					symbols[reader.ReadUInt16()] = new Symbol("Shape", (int)start, (int)length, null, Tag: tag);
					break;
				case 37:
					symbols[reader.ReadUInt16()] = new Symbol("EditText", (int)start, (int)length, null);
					break;
				case 46:
					symbols[reader.ReadUInt16()] = new Symbol("MorphShape", (int)start, (int)length, null);
					break;
			}
			if (stream.Position > end)
				throw new InvalidDataException($"UEF tag {tag} at 0x{start:X} exceeds its {length}-byte length.");
			stream.Position = end;
		}
		throw new InvalidDataException("UEF timeline is missing an end tag.");
	}

	private static Placement ReadPlacement(BinaryReader reader, long end, bool version3)
	{
		var flags = reader.ReadByte();
		var extra = version3 ? reader.ReadByte() : 0;
		var depth = reader.ReadUInt16();
		// Fire uses the second flag byte's 0x10 bit without the optional class
		// string that the equivalent Flash PlaceObject3 tag would insert.
		if ((extra & 8) != 0)
			ReadString(reader, end);
		ushort? character = (flags & 2) != 0 ? reader.ReadUInt16() : null;
		var matrix = (flags & 4) != 0 ? ReadMatrix(reader) : null;
		var color = (flags & 8) != 0 ? ReadColorTransform(reader) : null;
		ushort? ratio = (flags & 16) != 0 ? reader.ReadUInt16() : null;
		var name = (flags & 32) != 0 ? ReadString(reader, end) : null;
		ushort? clipDepth = (flags & 64) != 0 ? reader.ReadUInt16() : null;
		return new Placement(depth, false, (flags & 1) != 0, character, name, matrix, clipDepth, color, ratio);
	}

	internal static double[] ReadMatrix(BinaryReader reader)
	{
		var bits = new BitFields(reader);
		var sx = 1d;
		var sy = 1d;
		var skewX = 0d;
		var skewY = 0d;
		if (bits.Read(1) != 0)
		{
			var count = bits.Read(5);
			sx = bits.Signed(count) / 65536d;
			sy = bits.Signed(count) / 65536d;
		}
		if (bits.Read(1) != 0)
		{
			var count = bits.Read(5);
			skewY = bits.Signed(count) / 65536d;
			skewX = bits.Signed(count) / 65536d;
		}
		var translationBits = bits.Read(5);
		return [sx, skewY, skewX, sy, bits.Signed(translationBits) / 20d, bits.Signed(translationBits) / 20d];
	}

	private static int[] ReadColorTransform(BinaryReader reader)
	{
		var bits = new BitFields(reader);
		var additive = bits.Read(1) != 0;
		var multiply = bits.Read(1) != 0;
		var width = bits.Read(4);
		int[] values = [256, 256, 256, 256, 0, 0, 0, 0];
		for (var i = 0; multiply && i < 4; i++)
			values[i] = bits.Signed(width);
		for (var i = 4; additive && i < 8; i++)
			values[i] = bits.Signed(width);
		return values;
	}

	internal static string ReadString(BinaryReader reader, long end)
	{
		var start = reader.BaseStream.Position;
		while (reader.BaseStream.Position < end)
		{
			if (reader.ReadByte() != 0)
				continue;
			var length = (int)(reader.BaseStream.Position - start - 1);
			reader.BaseStream.Position = start;
			var value = Encoding.UTF8.GetString(reader.ReadBytes(length));
			reader.ReadByte();
			return value;
		}
		throw new InvalidDataException("Unterminated UEF string.");
	}

	internal sealed class BitFields(BinaryReader reader)
	{
		private int _remaining;
		private int _value;

		public int Read(int count)
		{
			var value = 0;
			for (var i = 0; i < count; i++)
			{
				if (_remaining == 0)
				{
					_value = reader.ReadByte();
					_remaining = 8;
				}
				value = (value << 1) | ((_value >> --_remaining) & 1);
			}
			return value;
		}

		public int Signed(int count)
		{
			var value = Read(count);
			return count == 0 ? 0 : (value << (32 - count)) >> (32 - count);
		}
	}
}
