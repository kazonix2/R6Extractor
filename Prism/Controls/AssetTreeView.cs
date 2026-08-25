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
	private readonly ConcurrentDictionary<ulong, (ulong Magic, bool IsArchive)> _detectedTypes = new();
	private Forge _openedForge;
	private Dictionary<ulong, ulong> _flatArchiveEntryMap = new();

	public AssetTreeView()
	{
		Dock = DockStyle.Fill;
		View = View.Details;
		ShowGroups = false;
		FullRowSelect = true;
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
				return FormatType(magic, uid);
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

		CanExpandGetter = model => model is Entry entry && GetDetectedType(entry).IsArchive;
		ChildrenGetter = LoadChildren;
		CellRightClick += OnCellRightClick;
		Expanded += (_, _) => SyncVirtualListSize();
		Collapsed += (_, _) => SyncVirtualListSize();
	}

	public void SetObjects(IEnumerable collection, Forge forge, Dictionary<ulong, ulong> flatArchiveEntryMap)
	{
		_openedForge = forge;
		_flatArchiveEntryMap = flatArchiveEntryMap ?? new Dictionary<ulong, ulong>();
		_detectedTypes.Clear();
		base.SetObjects(collection);
		RebuildAll(true);
		SyncVirtualListSize();
	}

	public void LoadForge(Forge forge)
	{
		SetObjects(forge.Entries, forge, new Dictionary<ulong, ulong>());
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
		if (_openedForge == null || model is not Entry entry || !GetDetectedType(entry).IsArchive)
			return null;

		if (_openedForge.GetContainer(entry.Uid) is not ForgeAsset forgeAsset)
			throw new InvalidDataException("Container is not an asset.");

		using var payload = forgeAsset.GetDataStream(_openedForge);
		var archive = FlatArchive.Read(_openedForge, forgeAsset, payload);
		foreach (var archiveEntry in archive.Entries)
		{
			if (archiveEntry.MetaData != null)
				_flatArchiveEntryMap[archiveEntry.MetaData.Uid] = entry.Uid;
		}
		return archive.Entries;
	}

	private (ulong Magic, bool IsArchive) GetDetectedType(Entry entry)
	{
		return _detectedTypes.GetOrAdd(entry.Uid, _ =>
		{
			if (_openedForge == null)
			{
				var metadataMagic = (ulong)(entry.MetaData?.FileType ?? 0);
				return (metadataMagic, MagicHelper.GetFiletype(metadataMagic) == AssetType.FlatArchive);
			}

			var magic = AssetTypeDetector.DetectMagic(_openedForge, entry, inspectPayload: true);
			var isArchive = AssetTypeDetector.DetectAssetType(_openedForge, entry, inspectPayload: true) == AssetType.FlatArchive;
			return (magic, isArchive);
		});
	}

	private string FormatType(ulong magic, ulong uid)
	{
		if (magic == ulong.MaxValue)
			return string.Empty;

		if (Enum.IsDefined(typeof(Magic), magic))
		{
			var known = (Magic)magic;
			if (known == Magic.Metadata && _openedForge != null && uid != 0)
			{
				try
				{
					return _openedForge.GetContainer(uid) switch
					{
						Hash => "[HASH]",
						Descriptor => "[DESCRIPTOR]",
						_ => known.ToString()
					};
				}
				catch
				{
				}
			}
			return known.ToString();
		}

		return $"{magic:X8}";
	}

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
				if (!FormatType(magic, uid).Contains(token[1..], StringComparison.OrdinalIgnoreCase))
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
