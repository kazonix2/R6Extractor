using RainbowForge.Core;
using RainbowForge.Core.Container;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RainbowForge
{
	/// <summary>
	/// Resolves the real payload magic for Siege Forge versions whose FAT metadata is
	/// missing, generic, or misleading. Results are cached per open Forge.
	/// </summary>
	public static class AssetTypeDetector
	{
		private const int HeaderProbeBytes = 128 * 1024;
		private static readonly ConditionalWeakTable<Forge, ConcurrentDictionary<ulong, ulong>> Cache = new();
		private static readonly ConditionalWeakTable<Forge, ConcurrentDictionary<ulong, bool>> ArchiveCache = new();

		public static ulong DetectMagic(Forge forge, Entry entry, bool inspectPayload = true)
		{
			if (forge == null) throw new ArgumentNullException(nameof(forge));
			if (entry == null) throw new ArgumentNullException(nameof(entry));
			var metadataMagic = (ulong)(entry.MetaData?.FileType ?? 0);
			if (forge.Version < 31 || forge.Version > 33)
				return metadataMagic;

			var cache = Cache.GetOrCreateValue(forge);
			if (cache.TryGetValue(entry.Uid, out var cached)) return cached;

			var hinted = DetectBankHint(forge.SourcePath);
			if (hinted != 0 && IsOrdinaryAsset(forge, entry))
				return cache.GetOrAdd(entry.Uid, hinted);

			if (IsUsableMetadataMagic(metadataMagic))
				return cache.GetOrAdd(entry.Uid, metadataMagic);

			// A shallow lookup is intentionally inconclusive. Do not cache it: selection,
			// preview, and export may later perform the payload-aware lookup and resolve
			// the real v31-v33 type.
			if (!inspectPayload)
				return metadataMagic;

			try
			{
				if (forge.GetContainer(entry.Uid) is ForgeAsset asset)
				{
					using var stream = asset.GetDataPrefixStream(forge, HeaderProbeBytes);
					if (stream.BaseStream.Length >= 20)
					{
						var payloadMetadata = FileMetaData.Read(stream, forge.Version);
						if (payloadMetadata.FileType != 0)
							return cache.GetOrAdd(entry.Uid, payloadMetadata.FileType);
					}
				}
			}
			catch
			{
				// Callers still receive the original FAT value when a payload is not an asset.
			}

			return cache.GetOrAdd(entry.Uid, metadataMagic);
		}

		public static AssetType DetectAssetType(Forge forge, Entry entry, bool inspectPayload = true) =>
			IsSeparatedArchive(forge, entry) ? AssetType.FlatArchive : MagicHelper.GetFiletype(DetectMagic(forge, entry, inspectPayload));

		public static void RegisterDetection(Forge forge, ulong uid, ulong magic, bool isSeparatedArchive)
		{
			if (forge == null) throw new ArgumentNullException(nameof(forge));
			Cache.GetOrCreateValue(forge)[uid] = magic;
			ArchiveCache.GetOrCreateValue(forge)[uid] = isSeparatedArchive;
		}

		public static void RegisterDetections(
			Forge forge,
			IEnumerable<(ulong Uid, ulong Magic, bool IsSeparatedArchive)> detections)
		{
			if (forge == null) throw new ArgumentNullException(nameof(forge));
			if (detections == null) throw new ArgumentNullException(nameof(detections));
			var magicCache = Cache.GetOrCreateValue(forge);
			var archiveCache = ArchiveCache.GetOrCreateValue(forge);
			foreach (var detection in detections)
			{
				magicCache[detection.Uid] = detection.Magic;
				archiveCache[detection.Uid] = detection.IsSeparatedArchive;
			}
		}

		private static bool IsSeparatedArchive(Forge forge, Entry entry)
		{
			if (forge.Version is < 31 or > 33 || DetectBankHint(forge.SourcePath) != 0) return false;
			var metadataMagic = (ulong)(entry.MetaData?.FileType ?? 0);
			if (IsUsableMetadataMagic(metadataMagic)) return false;
			var cache = ArchiveCache.GetOrCreateValue(forge);
			if (cache.TryGetValue(entry.Uid, out var cached)) return cached;
			try
			{
				if (forge.GetContainer(entry.Uid) is not ForgeAsset { HasMeta: true } asset) return cache.GetOrAdd(entry.Uid, false);
				using var metadata = asset.GetMetaDataStream(forge);
				if (metadata.BaseStream.Length < 4) return cache.GetOrAdd(entry.Uid, false);
				var count = metadata.ReadInt32();
				return cache.GetOrAdd(entry.Uid, count > 0 && 4L + count * 12L <= metadata.BaseStream.Length);
			}
			catch { return cache.GetOrAdd(entry.Uid, false); }
		}

		private static bool IsUsableMetadataMagic(ulong magic) =>
			magic != 0 && (Enum.IsDefined(typeof(Magic), magic) || MagicHelper.GetFiletype(magic) != AssetType.Unknown);

		private static bool IsOrdinaryAsset(Forge forge, Entry entry)
		{
			try { return forge.GetContainer(entry.Uid) is ForgeAsset; }
			catch { return false; }
		}

		private static ulong DetectBankHint(string sourcePath)
		{
			if (string.IsNullOrWhiteSpace(sourcePath)) return 0;
			var name = Path.GetFileNameWithoutExtension(sourcePath);
			if (name.EndsWith("_bnk_mesh", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledMeshObject;
			if (name.EndsWith("_bnk_meshshape", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledMeshShapeDataObject;
			if (name.EndsWith("_bnk_textures0", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledLowResolutionTextureMap;
			if (name.EndsWith("_bnk_textures1", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledMediumResolutionTextureMap;
			if (name.EndsWith("_bnk_textures2", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledHighResolutionTextureMap;
			if (name.EndsWith("_bnk_textures3", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledUltraResolutionTextureMap;
			if (name.EndsWith("_bnk_textures4", StringComparison.OrdinalIgnoreCase)) return (ulong)Magic.CompiledFutureResolutionTextureMap;
			return 0;
		}
	}
}
