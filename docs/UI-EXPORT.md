# Export Siege UI resources for HTML, CSS, and JavaScript

Build DumpTool from the repository root and pass the game directory and a new
output directory outside the installation.

```powershell
dotnet build DumpTool/DumpTool.csproj -c Release
$tool = '.\DumpTool\bin\Release\net10.0\DumpTool.exe'
& $tool rip-ui 'E:\RainbowSIX\ThrowbackLauncher\downloads\Y2S3_BloodOrchid' 'E:\R6ExtractorArtifacts\BloodOrchid-Web'
& $tool rip-ui "E:\SteamLibrary\steamapps\common\Tom Clancy's Rainbow Six Siege" 'E:\R6ExtractorArtifacts\Y11S3-Web'
```

Y11S3 compressed chunks use the existing Oodle loader. Put
`oo2core_8_win64.dll` next to DumpTool or set `OODLE2_8_PATH` to its path.
The game executable is never launched. The separate `unpack --build` command
accepts the verified Blood Orchid and Y11S3 dump fingerprints.

Open `index.html` in a browser to search the extracted assets and follow links.
`ui.json` lists resource UIDs, allocation groups, image dimensions, references,
missing images, and decoding diagnostics. `ui-data.js` exposes the same data to
HTML opened from disk. The extractor writes PNG when the Windows texture
decoder supports the pixel format and retains DDS otherwise.

The Blood Orchid export also writes `start-screen/index.html`. Open it directly
in a browser and keep it inside the output directory so it can load the original
textures. This page renders the PC start screen with native SVG and JavaScript.
It plays the 30 fps display list, including the intro, cloud fades, and grass
loop, and draws the original Scout font atlas. Pressing a key or clicking the
prompt dispatches `siege-start` on the document for the host page to handle.
Add `?frame=299` to hold a specific zero-based frame for inspection.

All exported Blood Orchid clips have a browser page linked from the resource
browser. Their pages share `localization.js`, generated from the shipped English
UI bank in `datapc64.forge` (UID `D7AF9901`). The PC start prompt uses the verified
bank row. Other text fields use a bank row when their authored text uniquely
matches it. Authored defaults remain available for unmatched fields.

The browser approximates Fire's distance-field font shader. The start screen
emits a browser event in place of the game transition. Other clips play their
authored timelines. Some library clips have an empty stage and are instantiated
by the game. Runtime ActionScript, masks, morphs, and complex fill styles
remain unsupported. See `screenDiagnostics` in `ui.json` for per-symbol gaps.

For a ported screen, link `fonts.css` and include `ui-data.js` before `ui.js`.
Use a PNG resource UID, image wrapper UID, or texture-map UID in the markup.
Copy the UID from the resource browser. Keep the scripts and images in the same
relative locations as the export.

```html
<link rel="stylesheet" href="fonts.css">
<script defer src="ui-data.js"></script>
<script defer src="ui.js"></script>
<img data-ui-image="0000000001CA0BDE" alt="Menu icon">
```

Use `font-family: 'Siege-<16-digit UID>'` for exported Y11S3 TTF and OTF fonts.
Y2S3 UEF clips also produce `layouts/<UID>.json` with the original stage bounds
in twips, frame rate, root class, sprite timelines, placement depths, matrices
in CSS order, and external bitmap and font references. Shape and text symbols
identify their payload spans in the original clip.

Y2S3 Fire font descriptors, UEF clips, and Y11S3 Phoenix records are preserved
as binary source assets. Other Y2S3 screens still need their game-driven behavior
ported. Y11S3 still needs its Phoenix scene and timeline decoder. Names that
remain encoded are shown by UID.
