using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BrightIdeasSoftware;
using Prism.Extensions;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Core;
using RainbowForge.Core.Container;

namespace Prism.Controls;

/// <summary>
/// .NET 10 port of the ObjectListView TreeListView asset browser from the
/// D:\RainbowForge Prism version. R5-specific type detection and separated archive
/// parsing are retained for Siege Forge v31-v33.
/// </summary>
public sealed class AssetTreeView : TreeListView
{
	private readonly ConcurrentDictionary<Entry, (ulong Magic, bool IsArchive)> _detectedTypes = new();
	private Dictionary<Entry, Forge> _owners = new();
	private readonly Dictionary<FlatArchiveEntry, Entry> _parents = new();

	internal Forge GetForge(object model) => model switch
	{
		Entry entry => _owners.GetValueOrDefault(entry),
		FlatArchiveEntry child when _parents.TryGetValue(child, out var parent) => _owners.GetValueOrDefault(parent),
		_ => null
	};

	internal Entry GetParent(FlatArchiveEntry entry) => _parents.GetValueOrDefault(entry);
	private Dictionary<ulong, ulong> _flatArchiveEntryMap = new();

	public AssetTreeView()
	{
		Dock = DockStyle.Fill;
		View = View.Details;
		ShowGroups = false;
		FullRowSelect = true;
		MultiSelect = true;
		UseFiltering = true;
		UseHotItem = false;
		UseHyperlinks = false;
		UseHotControls = false;
		HideSelection = false;
		BackColor = System.Drawing.Color.FromArgb(24, 25, 28);
		ForeColor = System.Drawing.Color.FromArgb(220, 224, 230);

		Columns.Add(new OLVColumn("Filename", null)
		{
			Width = 100,
			AspectGetter = rowObject => rowObject switch
			{
				Entry entry => DisplayFilename(entry.MetaData?.FileName, entry.Uid),
				FlatArchiveEntry archiveEntry => DisplayFilename(archiveEntry.MetaData?.FileName, archiveEntry.MetaData?.Uid ?? 0),
				_ => string.Empty
			}
		});

		Columns.Add(new OLVColumn("Type", null)
		{
			Width = 150,
			AspectGetter = rowObject => rowObject switch
			{
				Entry entry => (GetDetectedType(entry).Magic, entry.Uid),
				FlatArchiveEntry archiveEntry => ((ulong)(archiveEntry.MetaData?.FileType ?? 0), archiveEntry.MetaData?.Uid ?? 0),
				_ => (ulong.MaxValue, 0UL)
			},
			AspectToStringConverter = value =>
			{
				var (magic, uid) = ((ulong Magic, ulong Uid))value;
				return FormatType(magic);
			}
		});

		Columns.Add(new OLVColumn("Size", null)
		{
			Width = 70,
			AspectGetter = rowObject => rowObject switch
			{
				Entry entry => entry.Size,
				FlatArchiveEntry archiveEntry => (uint)Math.Max(0, archiveEntry.PayloadLength),
				_ => 0U
			},
			AspectToStringConverter = value => ((uint)value).ToFileSizeString()
		});

		Columns.Add(new OLVColumn("UID", null)
		{
			Width = 210,
			AspectGetter = rowObject => rowObject switch
			{
				Entry entry => entry.Uid,
				FlatArchiveEntry archiveEntry => archiveEntry.MetaData?.Uid,
				_ => null
			},
			AspectToStringConverter = value => value is ulong uid ? $"{uid:X16}" : string.Empty
		});

		Columns.Add(new OLVColumn("Forge", null)
		{
			Width = 250,
			AspectGetter = model => Path.GetFileName(GetForge(model)?.SourcePath)
		});

		CanExpandGetter = model => model is Entry entry && GetDetectedType(entry).IsArchive;
		ChildrenGetter = LoadChildren;
		CellRightClick += OnCellRightClick;
		Expanded += (_, _) => SyncVirtualListSize();
		Collapsed += (_, _) => SyncVirtualListSize();
	}

	public void SetObjects(IEnumerable collection, Forge forge, Dictionary<ulong, ulong> flatArchiveEntryMap)
	{
		SetForgeObjects(collection.Cast<Entry>().ToDictionary(entry => entry, _ => forge), flatArchiveEntryMap);
	}

	internal void SetForges(IEnumerable<Forge> forges) => SetForgeObjects(forges
		.SelectMany(forge => forge.Entries.Select(entry => (entry, forge)))
		.ToDictionary(pair => pair.entry, pair => pair.forge), null);

	private void SetForgeObjects(Dictionary<Entry, Forge> owners, Dictionary<ulong, ulong> flatArchiveEntryMap)
	{
		_owners = owners;
		_parents.Clear();
		_flatArchiveEntryMap = flatArchiveEntryMap ?? new Dictionary<ulong, ulong>();
		_detectedTypes.Clear();
		base.SetObjects(owners.Keys);
		RebuildAll(true);
		SyncVirtualListSize();
	}

	public void LoadForge(Forge forge)
	{
		SetObjects(forge.Entries, forge, new Dictionary<ulong, ulong>());
	}

	protected override void UpdateHotItem(System.Drawing.Point point)
	{
		// ObjectListView otherwise hit-tests native rows while replacing the tree model,
		// even when all of the features that use hot items are disabled.
		if (UseHotItem || UseHyperlinks || UseHotControls)
			base.UpdateHotItem(point);
	}

	public void SelectFirstNode()
	{
		if (VirtualListDataSource?.GetObjectCount() > 0)
			SelectedIndex = 0;
	}

	public void ApplyFilter(string filter)
	{
		SelectedIndex = -1;
		ModelFilter = string.IsNullOrWhiteSpace(filter)
			? null
			: new ModelFilter(model => MatchesFilter(model, filter));
		SyncVirtualListSize();
	}

	private void SyncVirtualListSize()
	{
		// ObjectListView 2.9.2 shadows this WinForms property. Its legacy setter
		// relies on framework internals that are null on .NET 10, so update the
		// native virtual row count through ListView after its data source changes.
		if (VirtualListDataSource != null)
			((ListView)this).VirtualListSize = VirtualListDataSource.GetObjectCount();
	}

	private IEnumerable LoadChildren(object model)
	{
		if (model is not Entry entry || GetForge(entry) is not { } forge || !GetDetectedType(entry).IsArchive)
			return null;

		if (forge.GetContainer(entry.Uid) is not ForgeAsset forgeAsset)
			throw new InvalidDataException("Container is not an asset.");

		using var payload = forgeAsset.GetDataStream(forge);
		var archive = FlatArchive.Read(forge, forgeAsset, payload);
		foreach (var archiveEntry in archive.Entries)
		{
			_parents[archiveEntry] = entry;
			if (archiveEntry.MetaData != null)
				_flatArchiveEntryMap[archiveEntry.MetaData.Uid] = entry.Uid;
		}
		return archive.Entries;
	}

	private (ulong Magic, bool IsArchive) GetDetectedType(Entry entry)
	{
		return _detectedTypes.GetOrAdd(entry, _ =>
		{
			if (GetForge(entry) is not { } forge)
			{
				var metadataMagic = (ulong)(entry.MetaData?.FileType ?? 0);
				return (metadataMagic, MagicHelper.GetFiletype(metadataMagic) == AssetType.FlatArchive);
			}

			var magic = AssetTypeDetector.DetectMagic(forge, entry, inspectPayload: true);
			var isArchive = AssetTypeDetector.DetectAssetType(forge, entry, inspectPayload: true) == AssetType.FlatArchive;
			return (magic, isArchive);
		});
	}

	private static string FormatType(ulong magic) => magic == ulong.MaxValue ? string.Empty :
		Enum.IsDefined(typeof(Magic), magic) ? ((Magic)magic).ToString() : $"{magic:X8}";

	private bool MatchesFilter(object model, string filter)
	{
		var (filename, uid, magic) = model switch
		{
			Entry entry => (DisplayFilename(entry.MetaData?.FileName, entry.Uid), entry.Uid, GetDetectedType(entry).Magic),
			FlatArchiveEntry archiveEntry =>
				(DisplayFilename(archiveEntry.MetaData?.FileName, archiveEntry.MetaData?.Uid ?? 0),
				 archiveEntry.MetaData?.Uid ?? 0,
				 (ulong)(archiveEntry.MetaData?.FileType ?? 0)),
			_ => (string.Empty, 0UL, ulong.MaxValue)
		};

		foreach (var token in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (token.StartsWith('#') && ulong.TryParse(token[1..], NumberStyles.HexNumber, Thread.CurrentThread.CurrentCulture, out var filterUid))
			{
				if (filterUid != uid)
					return false;
			}
			else if (token.StartsWith('?'))
			{
				if (!FormatType(magic).Contains(token[1..], StringComparison.OrdinalIgnoreCase))
					return false;
			}
			else if (!filename.Contains(token, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	private void OnCellRightClick(object sender, CellRightClickEventArgs args)
	{
		var selected = SelectedObjects.Cast<object>().Select(GetDisplayMetadata).Where(meta => meta.HasValue).Select(meta => meta.Value).ToList();
		if (selected.Count == 0)
			return;

		var names = string.Join(Environment.NewLine, selected.Select(meta => meta.Filename));
		var uids = string.Join(Environment.NewLine, selected.Select(meta => $"0x{meta.Uid:X16}"));
		var magics = string.Join(Environment.NewLine, selected.Select(meta => $"0x{meta.Magic:X8}"));
		var copyName = new ToolStripMenuItem("&Copy Name");
		copyName.Click += (_, _) => Clipboard.SetText(names);
		var copyUid = new ToolStripMenuItem("&Copy UID");
		copyUid.Click += (_, _) => Clipboard.SetText(uids);
		var copyFiletype = new ToolStripMenuItem("&Copy Filetype");
		copyFiletype.Click += (_, _) => Clipboard.SetText(magics);
		args.MenuStrip = new ContextMenuStrip
		{
			Items = { copyName, copyUid, copyFiletype }
		};
	}

	private (string Filename, ulong Uid, ulong Magic)? GetDisplayMetadata(object model)
	{
		return model switch
		{
			Entry entry => (DisplayFilename(entry.MetaData?.FileName, entry.Uid), entry.Uid, GetDetectedType(entry).Magic),
			FlatArchiveEntry archiveEntry =>
				(DisplayFilename(archiveEntry.MetaData?.FileName, archiveEntry.MetaData?.Uid ?? 0),
				 archiveEntry.MetaData?.Uid ?? 0,
				 archiveEntry.MetaData?.FileType ?? 0),
			_ => null
		};
	}

	private static string DisplayFilename(string filename, ulong uid) =>
		string.IsNullOrWhiteSpace(filename) ? uid.ToString("X16") : filename;
}
