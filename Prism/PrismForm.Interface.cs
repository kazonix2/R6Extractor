using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrightIdeasSoftware;
using OpenTK;
using OpenTK.Graphics;
using Prism.Controls;
using Prism.Extensions;
using Prism.Render;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using SkiaSharp.Views.Desktop;

namespace Prism
{
	public partial class PrismForm
	{
		private const string SETTINGS_FILENAME = "settings.json";

		private readonly ToolStripLabel _statusForgeInfo;

		private readonly ToolStripMenuItem _bOpenForge;
		private readonly ToolStripMenuItem _bGenerateFilelist;
		private readonly ToolStripMenuItem _bResetViewport;

		private readonly ToolStripMenuItem _bDumpAsBinHeader;
		private readonly ToolStripMenuItem _bDumpAsBinQuick;
		private readonly ToolStripMenuItem _bDumpAsBinAs;

		private readonly ToolStripMenuItem _bDumpAsDdsHeader;
		private readonly ToolStripMenuItem _bDumpAsDdsQuick;
		private readonly ToolStripMenuItem _bDumpAsDdsAs;

		private readonly ToolStripMenuItem _bDumpAsPngHeader;
		private readonly ToolStripMenuItem _bDumpAsPngQuick;
		private readonly ToolStripMenuItem _bDumpAsPngAs;

		private readonly ToolStripMenuItem _bDumpAsObjHeader;
		private readonly ToolStripMenuItem _bDumpAsObjQuick;
		private readonly ToolStripMenuItem _bDumpAsObjAs;

		private readonly TextBox _searchTextBox;
		private readonly AssetTreeView _assetList;

		private readonly MinimalSplitContainer _splitContainer;
		private readonly GLControl _glControl;
		private readonly SKControl _imageControl;
		private readonly TreeListView _infoControl;
		private readonly TextBox _errorInfoControl;

		private ToolStripMenuItem _bEditSettings;

		private PrismSettings _settings;

		public PrismForm(string initialForgePath = null)
		{
			SuspendLayout();

			AutoScaleMode = AutoScaleMode.Font;
			ClientSize = new Size(800, 450);
			Text = "Prism";
			BackColor = Color.FromArgb(24, 25, 28);
			ForeColor = Color.FromArgb(220, 224, 230);

			Controls.Add(_splitContainer = new MinimalSplitContainer
			{
				Dock = DockStyle.Fill,
				SplitterDistance = 450,
				Panel1 =
				{
					Controls =
					{
						(_assetList = new AssetTreeView { Dock = DockStyle.Fill }),
						(_searchTextBox = new TextBox
						{
							Dock = DockStyle.Top,
							PlaceholderText = "Search groups (keyword, ?type, #uid, separated by commas)",
							BackColor = Color.FromArgb(32, 33, 36),
							ForeColor = Color.FromArgb(220, 224, 230),
							BorderStyle = BorderStyle.FixedSingle
						})
					}
				}
			});

			Controls.Add(new MenuStrip
			{
				Dock = DockStyle.Top,
				Renderer = new FlatToolStripRenderer(),
				BackColor = Color.FromArgb(32, 33, 36),
				ForeColor = Color.FromArgb(220, 224, 230),

				Items =
				{
					new ToolStripDropDownButton
					{
						Text = "&File",
						DropDownItems =
						{
							(_bOpenForge = new ToolStripMenuItem("&Open Forge")
							{
								ShortcutKeys = Keys.Control | Keys.O
							}),
							new ToolStripSeparator(),
							(_bGenerateFilelist = new ToolStripMenuItem("&Generate Filelist")
							{
								ShortcutKeys = Keys.Control | Keys.I
							}),
							new ToolStripSeparator(),
							(_bEditSettings = new ToolStripMenuItem("&Settings")
							{
								ShortcutKeys = Keys.Control | Keys.Oemcomma,
								ShortcutKeyDisplayString = "Ctrl+,"
							}),
						}
					},
					new ToolStripDropDownButton
					{
						Text = "&Export",
						DropDownItems =
						{
							(_bDumpAsBinHeader = new ToolStripMenuItem("&Binary File (*.bin)")
							{
								DropDownItems =
								{
									(_bDumpAsBinQuick = new ToolStripMenuItem("&Quick Export")),
									(_bDumpAsBinAs = new ToolStripMenuItem("&Export as...")),
								}
							}),
							new ToolStripSeparator(),
							(_bDumpAsDdsHeader = new ToolStripMenuItem("&DirectDraw Surface (*.dds)")
							{
								DropDownItems =
								{
									(_bDumpAsDdsQuick = new ToolStripMenuItem("&Quick Export")),
									(_bDumpAsDdsAs = new ToolStripMenuItem("&Export as...")),
								}
							}),
							(_bDumpAsPngHeader = new ToolStripMenuItem("&PNG (*.png)")
							{
								DropDownItems =
								{
									(_bDumpAsPngQuick = new ToolStripMenuItem("&Quick Export")),
									(_bDumpAsPngAs = new ToolStripMenuItem("&Export as...")),
								}
							}),
							new ToolStripSeparator(),
							(_bDumpAsObjHeader = new ToolStripMenuItem("&Wavefront OBJ (*.obj)")
							{
								DropDownItems =
								{
									(_bDumpAsObjQuick = new ToolStripMenuItem("&.obj | Quick Export")),
									(_bDumpAsObjAs = new ToolStripMenuItem("&.obj | Export as..."))
								}
							})
						}
					},
					new ToolStripDropDownButton
					{
						Text = "&View",
						DropDownItems =
						{
							(_bResetViewport = new ToolStripMenuItem("&Reset 3D Viewport"))
						}
					}
				}
			});

			Controls.Add(new StatusStrip
			{
				Dock = DockStyle.Bottom,
				BackColor = Color.FromArgb(32, 33, 36),
				ForeColor = Color.FromArgb(220, 224, 230),
				Items =
				{
					(_statusForgeInfo = new ToolStripLabel("Ready") { ForeColor = Color.FromArgb(220, 224, 230) })
				}
			});

			_glControl = new GLControl(new GraphicsMode(new ColorFormat(8), 24, 8, 1))
			{
				Dock = DockStyle.Fill,
				VSync = true
			};

			_imageControl = new SKControl
			{
				Dock = DockStyle.Fill
			};

			_infoControl = new TreeListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				ShowGroups = false,
				FullRowSelect = true,
				BackColor = Color.FromArgb(24, 25, 28),
				ForeColor = Color.FromArgb(220, 224, 230)
			};

			_errorInfoControl = new TextBox
			{
				Multiline = true,
				Dock = DockStyle.Fill,
				ReadOnly = true,
				BackColor = Color.FromArgb(24, 25, 28),
				ForeColor = Color.FromArgb(255, 100, 100)
			};

			SetPreviewPanel(new Label
			{
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleCenter,
				Text = "Open a Forge to get started.",
				ForeColor = Color.FromArgb(160, 165, 175)
			});

			ResumeLayout(true);

			_bOpenForge.Click += (sender, args) =>
			{
				using var ofd = new OpenFileDialog
				{
					Filter = "Forge Files|*.forge"
				};

				if (ofd.ShowDialog() != DialogResult.OK)
					return;

				OpenForge(ofd.FileName);
			};

			_bGenerateFilelist.Click += (sender, args) =>
			{
				using var fbd = new FolderBrowserDialog
				{
					Description = "Selected the folder containing the forges."
				};

				if (fbd.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(fbd.SelectedPath))
					return;

				string[] forgeFiles = Directory.GetFiles(fbd.SelectedPath, "*.forge");

				if (forgeFiles.Length < 1)
					return;

				GenerateFileList(forgeFiles);
			};

			_bEditSettings.Click += (sender, args) =>
			{
				if (new SettingsForm(SETTINGS_FILENAME).ShowDialog(this) == DialogResult.OK)
					_settings = PrismSettings.Load(SETTINGS_FILENAME);
			};

			_bDumpAsBinQuick.Click += CreateDumpEventHandler(DumpSelectionAsBin);
			_bDumpAsBinAs.Click += CreateDumpEventHandler(DumpSelectionAsBin, true);

			_bDumpAsDdsQuick.Click += CreateDumpEventHandler(DumpSelectionAsDds);
			_bDumpAsDdsAs.Click += CreateDumpEventHandler(DumpSelectionAsDds, true);

			_bDumpAsPngQuick.Click += CreateDumpEventHandler(DumpSelectionAsPng);
			_bDumpAsPngAs.Click += CreateDumpEventHandler(DumpSelectionAsPng, true);

			_bDumpAsObjQuick.Click += CreateObjExportEventHandler();
			_bDumpAsObjAs.Click += CreateObjExportEventHandler(true);

			_bResetViewport.Click += (sender, args) => _renderer3d.ResetView();

			SetupRenderer();
			SetupAssetList();

			UpdateAbility(null);

			_settings = PrismSettings.Load(SETTINGS_FILENAME);
			if (!string.IsNullOrWhiteSpace(initialForgePath))
				Shown += (_, _) => OpenForge(initialForgePath);
		}

		private EventHandler CreateDumpEventHandler(Action<string, object> action, bool saveAs = false)
		{
			return (_, _) =>
			{
				var outputPath = _settings.QuickExportLocation;

				if (saveAs)
				{
					var folderBrowserDialog = new FolderBrowserDialog();
					if (folderBrowserDialog.ShowDialog() != DialogResult.OK)
						return;

					outputPath = folderBrowserDialog.SelectedPath;
				}

				Directory.CreateDirectory(outputPath);

				foreach (var selectedObject in _assetList.SelectedObjects)
				{
					action(outputPath, selectedObject);
				}
			};
		}

		private EventHandler CreateObjExportEventHandler(bool saveAs = false)
		{
			return async (_, _) =>
			{
				var outputPath = _settings.QuickExportLocation;
				if (saveAs)
				{
					using var dialog = new FolderBrowserDialog();
					if (dialog.ShowDialog() != DialogResult.OK)
						return;
					outputPath = dialog.SelectedPath;
				}

				Directory.CreateDirectory(outputPath);
				foreach (var selectedObject in _assetList.SelectedObjects.Cast<object>().ToArray())
				{
					try
					{
						var exportedPath = await ExportSelectionAsObjAsync(outputPath, selectedObject);
						if (File.Exists(exportedPath))
							MessageBox.Show(this, $"Complete map exported to:\n{exportedPath}", "OBJ Export",
								MessageBoxButtons.OK, MessageBoxIcon.Information);
					}
					catch (OperationCanceledException)
					{
						_statusForgeInfo.Text = "OBJ export cancelled.";
					}
					catch (Exception exception)
					{
						_errorInfoControl.Text = exception.ToString();
						SetPreviewPanel(_errorInfoControl);
					}
				}
			};
		}

		private void SetPreviewPanel(Control control)
		{
			using (_splitContainer.Panel2.SuspendPainting())
			{
				_splitContainer.Panel2.Controls.Clear();
				_splitContainer.Panel2.Controls.Add(control);
			}
		}

		private void SetupRenderer()
		{
			_renderer2d = new SurfaceRenderer(_imageControl);
			_imageControl.MouseMove += (sender, args) => _renderer2d.OnMouseMove(args.Location, (args.Button & MouseButtons.Left) != 0);
			_imageControl.MouseWheel += (sender, args) => _renderer2d.OnMouseWheel(args.Location, args.Delta);

			_imageControl.PaintSurface += (sender, args) => { _renderer2d.Render(args); };

			_renderer3d = new ModelRenderer(new GlControlContext(_glControl), () => _settings);
			_glControl.MouseDown += (sender, args) => _renderer3d.OnMouseDown(args.Location);
			_glControl.MouseMove += (sender, args) => _renderer3d.OnMouseMove(args.Location, (args.Button & MouseButtons.Left) != 0, (args.Button & MouseButtons.Right) != 0);
			_glControl.MouseWheel += (sender, args) => _renderer3d.OnMouseWheel(args.Delta);

			_glControl.Paint += (sender, args) =>
			{
				_glControl.MakeCurrent();
				_renderer3d.Render();
				_glControl.SwapBuffers();
			};

			_infoControl.Columns.Add(new OLVColumn("Key", nameof(TreeListViewEntry.Key))
			{
				Width = 200
			});
			_infoControl.Columns.Add(new OLVColumn("Value", nameof(TreeListViewEntry.Value))
			{
				FillsFreeSpace = true
			});
			_infoControl.CanExpandGetter = model => model is TreeListViewEntry tlve && tlve.Children.Length > 0;
			_infoControl.ChildrenGetter = model => model is TreeListViewEntry tlve ? tlve.Children : null;
		}

		private void SetupAssetList()
		{
			_assetList.SelectionChanged += OnAssetListOnSelectionChanged;

			_searchTextBox.TextChanged += (sender, args) =>
			{
				_assetList.ApplyFilter(_searchTextBox.Text);
			};
		}

		private bool DoesEntryMatchFilter(object entry, string filter)
		{
			var searchGroups = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var groupMatches = new List<bool>();

			var meta = entry is Entry forgeEntry ? GetDetectedAssetMetaData(forgeEntry) : GetAssetMetaData(entry);

			foreach (var t in searchGroups)
			{
				if (string.IsNullOrWhiteSpace(t))
				{
					groupMatches.Add(true);
					continue;
				}

				if (t.StartsWith('#') && ulong.TryParse(t[1..], NumberStyles.HexNumber, Thread.CurrentThread.CurrentCulture, out var filterUid) &&
				    filterUid == meta.Uid)
				{
					groupMatches.Add(true);
					continue;
				}

				if (t.StartsWith('?'))
				{
					groupMatches.Add(((Magic)meta.Magic).ToString().Contains(t[1..], StringComparison.OrdinalIgnoreCase));
					continue;
				}

				groupMatches.Add(meta.Filename.Contains(t, StringComparison.OrdinalIgnoreCase));
			}

			return groupMatches.All(x => x);
		}

		private async void OnAssetListOnSelectionChanged(object sender, EventArgs args)
		{
			if (_assetList.SelectedObjects.Count < 1) return;
			var selectedEntry = _assetList.SelectedObjects[0]; // TODO: find a way to get the last selected index to preview the latest selected asset
			var stream = GetAssetStream(selectedEntry);
			if (stream == null)
				return;

			_previewCancellation?.Cancel();
			UpdateAbility(stream);

			if (IsWorldScene(stream))
			{
				var cancellation = _previewCancellation = new CancellationTokenSource();
				List<TexturedSceneMesh> scene = null;
				var renderedMeshes = 0;
				var loadedLights = 0;
				try
				{
					SetPreviewPanel(_glControl);
					using (_glControl.SuspendPainting())
					{
						_glControl.MakeCurrent();
						_renderer3d.BeginTexturedScene();
					}
					_statusForgeInfo.Text = $"Resolving {(Magic)stream.MetaData.Magic} mesh dependencies...";
					scene = await Task.Run(() => BuildWorldScene(stream, cancellation.Token,
						message => OnUiThread(() => _statusForgeInfo.Text = message),
						(meshChunk, lightChunk) => OnUiThreadSync(() =>
						{
							if (cancellation.IsCancellationRequested)
								return;
							using (_glControl.SuspendPainting())
							{
								_glControl.MakeCurrent();
								_renderer3d.AppendTexturedScene(meshChunk);
								_renderer3d.AppendDynamicLights(lightChunk);
							}
							renderedMeshes += meshChunk.Count;
							loadedLights += lightChunk.Count;
							_statusForgeInfo.Text = $"Streaming scene: {renderedMeshes:N0} mesh set(s), {loadedLights:N0} dynamic light(s)...";
						})), cancellation.Token);
					if (cancellation.IsCancellationRequested)
						return;

					_statusForgeInfo.Text = "Loading baked GI coefficients...";
					var bakedGi = await Task.Run(() => SceneLightingReader.TryReadBakedGi(_openedForge.SourcePath, cancellation.Token), cancellation.Token);
					if (cancellation.IsCancellationRequested)
						return;
					using (_glControl.SuspendPainting())
					{
						_glControl.MakeCurrent();
						_renderer3d.SetBakedLighting(bakedGi);
					}
					var giStatus = bakedGi.SampleCount > 0
						? $"GI approximation: {bakedGi.SampleCount:N0} coefficient samples"
						: "GI bank unavailable; using neutral ambient";
					_statusForgeInfo.Text = $"Rendered {scene.Count:N0} textured mesh set(s), {loadedLights:N0} dynamic light(s). {giStatus}.";
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception e)
				{
					if (!cancellation.IsCancellationRequested)
					{
						_errorInfoControl.Text = e.ToString();
						SetPreviewPanel(_errorInfoControl);
					}
				}
				return;
			}

			try
			{
				PreviewAsset(stream);
			}
			catch (Exception e)
			{
				_errorInfoControl.Text = e.ToString();
				SetPreviewPanel(_errorInfoControl);
			}
		}

		private void UpdateAbility(AssetStream assetStream)
		{
			AssetType type;
			Magic magic;

			if (assetStream == null)
			{
				type = AssetType.Unknown;
				magic = 0;
			}
			else
			{
				type = MagicHelper.GetFiletype(assetStream.MetaData.Magic);
				magic = (Magic)assetStream.MetaData.Magic;
			}

			_bDumpAsBinHeader.Enabled = assetStream != null;
			_bDumpAsDdsHeader.Enabled = _bDumpAsPngHeader.Enabled = type == AssetType.Texture;
			_bDumpAsObjHeader.Enabled = type == AssetType.Mesh || MagicHelper.Equals(Magic.Mesh, magic)
			                           || IsWorldScene(assetStream);
		}

		private void OnUiThread(Action action)
		{
			if (!InvokeRequired)
				action();
			else
				BeginInvoke(action);
		}

		private void OnUiThreadSync(Action action)
		{
			if (!InvokeRequired)
				action();
			else
				Invoke(action);
		}
	}
}
