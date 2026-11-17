using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using CommandLine;
using RainbowForge.Core;
using RainbowForge.Core.Container;

namespace DumpTool
{
	[Verb("unpack", HelpText = "Unpack a Forge allocation or dependency graph into a new binary file.")]
	internal class UnpackCommand
	{
		private const string ClosedAlphaSha256 = "E897685F61507C73CE3C6A5BE94FD9E0A1330A70A59EC56F8364AACAB614581E";
		private const string AprilAlphaSha256 = "26C99592896226AB1F96F267B984A4E781196AA57B0E516406296747E72CA845";
		private const string VanillaSha256 = "E14B01739BC068453FC0E1F854F17233CADFBC6AAF1BA1F3FB7B9CC0E2A785BB";
		private const string SystemOverrideSha256 = "69298FACDC1EECEAE11D6F35EF5849DB1D975CBD8AAC24478AD594E2AD31365F";
		private const string BloodOrchidSha256 = "DC9DEC754A7523D57FAEF3AF2692A08BA7395AEB088CBD1A6ECF2FB0AADED323";
		private const string Y11S3Sha256 = "28FB5D305AEB073FAF95AD95FEC1688E1FC6AA76755764CBE994C7D58623CFE2";

		[Value(0, Required = true, HelpText = "Input .forge or .depgraphbin file.")]
		public string Input { get; set; }

		[Value(1, Required = true, HelpText = "New output file outside the game installation.")]
		public string Output { get; set; }

		[Option("build", Required = true, HelpText = "Path to a supported RainbowSix dump for SHA-256 verification.")]
		public string Build { get; set; }

		[Option("uid", HelpText = "Forge allocation UID, in decimal or hexadecimal with a 0x prefix.")]
		public string Uid { get; set; }

		[Option("metadata", HelpText = "Unpack the Forge allocation's metadata block instead of its payload.")]
		public bool Metadata { get; set; }

		public static void Run(UnpackCommand args)
		{
			try
			{
				using (var build = File.OpenRead(args.Build))
					if (Convert.ToHexString(SHA256.HashData(build)) is not (AprilAlphaSha256 or ClosedAlphaSha256 or VanillaSha256 or SystemOverrideSha256 or BloodOrchidSha256 or Y11S3Sha256))
						throw new InvalidDataException("Build fingerprint does not match a supported RainbowSix dump.");

				var output = Path.GetFullPath(args.Output);
				var parent = ResolveDirectory(Path.GetDirectoryName(output));
				var buildFile = new FileInfo(args.Build);
				var install = ResolveDirectory(Path.GetDirectoryName((buildFile.ResolveLinkTarget(true) ?? buildFile).FullName));
				var relative = Path.GetRelativePath(install, parent);
				if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					throw new IOException("Unpack outside the game installation.");

				output = Path.Combine(parent, Path.GetFileName(output));
				using var decoded = OpenPayload(args);
				var staging = Path.Combine(parent, $".r6-unpack-{Guid.NewGuid():N}.tmp");
				var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				try
				{
					using (file)
						decoded.CopyTo(file);
					File.Move(staging, output);
				}
				finally
				{
					File.Delete(staging);
				}
			}
			catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
			                              KeyNotFoundException or FormatException or OverflowException or NotImplementedException or
			                              DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				Console.Error.WriteLine($"Unpack failed: {error.Message}");
				Environment.ExitCode = 1;
			}
		}

		private static Stream OpenPayload(UnpackCommand args)
		{
			switch (Path.GetExtension(args.Input).ToLowerInvariant())
			{
				case ".depgraphbin" when args.Uid == null && !args.Metadata:
					using (var input = new BinaryReader(File.OpenRead(args.Input)))
						return DepGraph.Unpack(input);
				case ".forge" when args.Uid != null:
					var uid = args.Uid.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
						? ulong.Parse(args.Uid.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
						: ulong.Parse(args.Uid, CultureInfo.InvariantCulture);
					var forge = Forge.GetForge(args.Input);
					using (forge.Stream)
						return forge.GetContainer(uid) switch
						{
							ForgeAsset asset when !args.Metadata => asset.GetDataStream(forge).BaseStream,
							ForgeAsset asset when asset.HasMeta => asset.GetMetaDataStream(forge).BaseStream,
							RawRiffAsset riff when !args.Metadata => riff.GetDataStream(forge).BaseStream,
							_ => throw new InvalidDataException("The allocation does not have the requested block.")
						};
				default:
					throw new ArgumentException("Use --uid with a .forge file, or supply a .depgraphbin without Forge options.");
			}
		}

		private static string ResolveDirectory(string path)
		{
			var directory = new DirectoryInfo(path);
			if (!directory.Exists)
				throw new DirectoryNotFoundException(path);
			if (directory.Parent == null)
				return directory.FullName;
			var resolved = new DirectoryInfo(Path.Combine(ResolveDirectory(directory.Parent.FullName), directory.Name));
			return (resolved.ResolveLinkTarget(true) ?? resolved).FullName;
		}
	}
}
