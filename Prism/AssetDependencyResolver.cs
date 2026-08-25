using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RainbowForge;
using RainbowForge.Core;
using RainbowForge.Core.Container;

namespace Prism;

internal enum AssetBankKind
{
	Mesh,
	Texture
}

internal sealed class ResolvedForgeAsset
{
	private readonly Forge _forge;
	private readonly Entry _entry;

	public uint Version => _forge.Version;

	public ResolvedForgeAsset(Forge forge, Entry entry)
	{
		_forge = forge;
		_entry = entry;
	}

	public BinaryReader OpenStream()
	{
		if (_forge.GetContainer(_entry.Uid) is not ForgeAsset asset)
			throw new InvalidDataException($"Resolved UID {_entry.Uid:X16} is not a Forge asset.");
		return asset.GetDataStream(_forge);
	}
}

/// <summary>
/// Lazily indexes logical asset UIDs embedded in neighboring Siege bank payloads.
/// FAT UIDs in v31-v33 banks are not the logical mesh/texture UIDs, so looking at
/// Forge.Entry.Uid alone is insufficient.
/// </summary>
internal sealed class AssetDependencyResolver : IDisposable
{
	private readonly string _directory;
	private readonly object _sync = new();
	private readonly Dictionary<string, Forge> _openedBanks = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<ulong, ResolvedForgeAsset> _logicalAssets = new();
	private readonly HashSet<string> _scannedBanks = new(StringComparer.OrdinalIgnoreCase);

	public AssetDependencyResolver(string sourceForgePath)
	{
		_directory = Path.GetDirectoryName(sourceForgePath)
		             ?? throw new ArgumentException("The source Forge has no parent directory.", nameof(sourceForgePath));
	}

	public ResolvedForgeAsset Resolve(ulong uid, AssetBankKind kind, CancellationToken cancellationToken, Action<string> progress = null)
	{
		lock (_sync)
		{
			if (_logicalAssets.TryGetValue(uid, out var cached))
				return cached;

			foreach (var path in GetCandidateBanks(kind))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					ScanBank(path, cancellationToken, progress);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception error)
				{
					progress?.Invoke($"Skipped {Path.GetFileName(path)}: {error.Message}");
					continue;
				}
				if (_logicalAssets.TryGetValue(uid, out var resolved))
					return resolved;
			}
		}

		throw new FileNotFoundException($"Logical {kind} UID 0x{uid:X16} was not found in neighboring banks.");
	}

	private IEnumerable<string> GetCandidateBanks(AssetBankKind kind)
	{
		var paths = Directory.EnumerateFiles(_directory, "*.forge", SearchOption.TopDirectoryOnly)
			.Where(path => kind switch
			{
				AssetBankKind.Mesh => Path.GetFileName(path).EndsWith("_bnk_mesh.forge", StringComparison.OrdinalIgnoreCase),
				AssetBankKind.Texture => IsTextureBank(path),
				_ => false
			});

		return paths.OrderBy(GetBankPriority).ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsTextureBank(string path)
	{
		var name = Path.GetFileName(path);
		return !name.Contains("guitextures", StringComparison.OrdinalIgnoreCase)
		       && Enumerable.Range(0, 5).Any(layer => name.EndsWith($"_bnk_textures{layer}.forge", StringComparison.OrdinalIgnoreCase));
	}

	private static int GetBankPriority(string path)
	{
		var name = Path.GetFileName(path);
		if (name.Contains("merged_events_bnk", StringComparison.OrdinalIgnoreCase)) return 0;
		if (name.Contains("merged_set01_bnk", StringComparison.OrdinalIgnoreCase)) return 1;
		if (name.Contains("merged_set02_bnk", StringComparison.OrdinalIgnoreCase)) return 2;
		if (name.Contains("merged_ondemand_bnk", StringComparison.OrdinalIgnoreCase)) return 3;
		if (name.Contains("merged_playgo_bnk", StringComparison.OrdinalIgnoreCase)) return 4;
		return 10;
	}

	private void ScanBank(string path, CancellationToken cancellationToken, Action<string> progress)
	{
		if (_scannedBanks.Contains(path))
			return;

		progress?.Invoke($"Indexing {Path.GetFileName(path)}...");
		Forge forge = null;
		try
		{
			forge = Forge.GetForge(path);
			_openedBanks[path] = forge;
			var bankAssets = new Dictionary<ulong, ResolvedForgeAsset>();
			for (var i = 0; i < forge.Entries.Length; i++)
			{
				if ((i & 0x3F) == 0)
					cancellationToken.ThrowIfCancellationRequested();

				var entry = forge.Entries[i];
				bankAssets.TryAdd(entry.Uid, new ResolvedForgeAsset(forge, entry));
				try
				{
					if (forge.GetContainer(entry.Uid) is not ForgeAsset asset)
						continue;
					using var prefix = asset.GetDataPrefixStream(forge, 512);
					if (prefix.BaseStream.Length < 20)
						continue;
					var metadata = FileMetaData.Read(prefix, forge.Version);
					bankAssets.TryAdd(metadata.Uid, new ResolvedForgeAsset(forge, entry));
				}
				catch
				{
					// Descriptor/hash entries and malformed assets are not dependency payloads.
				}
			}
			foreach (var asset in bankAssets)
				_logicalAssets.TryAdd(asset.Key, asset.Value);
			_scannedBanks.Add(path);
		}
		catch
		{
			if (forge != null && !_scannedBanks.Contains(path))
			{
				_openedBanks.Remove(path);
				forge.Stream.Dispose();
			}
			throw;
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			foreach (var forge in _openedBanks.Values.Distinct())
				forge.Stream.Dispose();
			_openedBanks.Clear();
			_logicalAssets.Clear();
			_scannedBanks.Clear();
		}
	}
}
