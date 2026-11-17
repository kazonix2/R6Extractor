using System;
using System.Buffers.Binary;
using System.IO;

namespace RainbowForge.Compression
{
	// Original LZO1X stream format: https://www.kernel.org/doc/html/latest/staging/lzo.html
	internal static class Lzo1x
	{
		internal static byte[] Decompress(ReadOnlySpan<byte> input, int length)
		{
			var output = new byte[length];
			var decoder = new Decoder(input, output);
			decoder.Decode();
			return output;
		}

		private ref struct Decoder(ReadOnlySpan<byte> input, Span<byte> output)
		{
			private ReadOnlySpan<byte> _input = input;
			private readonly Span<byte> _output = output;
			private int _written;

			internal void Decode()
			{
				// The next short opcode depends on the previous literal count, capped at four.
				var literalState = 0;
				if (!_input.IsEmpty && _input[0] > 17)
				{
					var length = ReadByte() - 17;
					CopyLiterals(length);
					literalState = Math.Min(length, 4);
				}

				while (true)
				{
					var opcode = ReadByte();
					int count, distance, followingLiterals;
					if (opcode < 16)
					{
						if (literalState == 0)
						{
							CopyLiterals(ReadLength(opcode, 15) + 3);
							literalState = 4;
							continue;
						}
						count = literalState == 4 ? 3 : 2;
						distance = (literalState == 4 ? 2049 : 1) + (opcode >> 2) + (ReadByte() << 2);
						followingLiterals = opcode & 3;
					}
					else if (opcode < 64)
					{
						var farMatch = opcode < 32;
						count = ReadLength(opcode, farMatch ? 7 : 31) + 2;
						var operand = ReadOffset();
						distance = operand >> 2;
						if (farMatch)
						{
							distance += (opcode & 8) << 11;
							if (distance == 0)
							{
							if (count != 3 || _written != _output.Length || !_input.IsEmpty)
									throw new InvalidDataException("LZO chunk ended at an unexpected length.");
								return;
							}
							distance += 16384;
						}
						else
							distance++;
						followingLiterals = operand & 3;
					}
					else
					{
						count = (opcode >> 5) + 1;
						distance = 1 + ((opcode >> 2) & 7) + (ReadByte() << 3);
						followingLiterals = opcode & 3;
					}

					if (distance > _written || count > _output.Length - _written)
						throw new InvalidDataException("LZO back-reference exceeds the chunk bounds.");
					// Matches may overlap, so each byte is available to the next iteration.
					for (var end = _written + count; _written < end; _written++)
						_output[_written] = _output[_written - distance];
					CopyLiterals(followingLiterals);
					literalState = followingLiterals;
				}
			}

			private byte ReadByte()
			{
				if (_input.IsEmpty)
					throw new InvalidDataException("Truncated LZO instruction.");
				var value = _input[0];
				_input = _input[1..];
				return value;
			}

			private ushort ReadOffset()
			{
				if (_input.Length < 2)
					throw new InvalidDataException("Truncated LZO match offset.");
				var value = BinaryPrimitives.ReadUInt16LittleEndian(_input);
				_input = _input[2..];
				return value;
			}

			private int ReadLength(byte opcode, int mask)
			{
				var length = opcode & mask;
				if (length != 0)
					return length;
				length = mask;
				byte next;
				while ((next = ReadByte()) == 0)
					length += 255;
				return length + next;
			}

			private void CopyLiterals(int count)
			{
				if (count > _input.Length || count > _output.Length - _written)
					throw new InvalidDataException("LZO literal run exceeds the chunk bounds.");
				_input[..count].CopyTo(_output[_written..]);
				_input = _input[count..];
				_written += count;
			}
		}
	}
}
