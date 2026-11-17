# Y11S2 filesystem unpacking

The C# Forge reader recognizes the verified Y11S2 v34 allocation table. It
decodes allocation UIDs, offsets and lengths from the table and validates the
layout and ranges before using them. The two control records are excluded from
asset listings. For other v34 layouts the reader retains the existing page-scan
fallback and its synthetic IDs. Use `list` to get the IDs recognized in a given
file. Allocation UIDs identify archive groups and can differ from the UIDs of
individual resources within them.

The unpacker handles stored chunks, legacy Zstandard chunks, current Oodle
chunks and raw RIFF allocations. For the recognized Y11S2 envelopes, it checks
the packed checksum and bounds before decoding. A dependency graph unpacks to
its version byte followed by 24-byte little-endian parent UID, child UID and
flags rows. The raw Forge output preserves the serialized payload or resource
directory bytes.

## Run DumpTool

Build the C# solution with .NET 10. Run the following from the repository root
after setting `$install` to the locked Y11S2 installation and `$out` to an
existing directory outside the game installation and repository. Set
`OODLE2_8_PATH` to your local x64 `oo2core_8_win64.dll` for Oodle-compressed
chunks.

```powershell
dotnet build RainbowForge.sln -c Release
$tool = '.\DumpTool\bin\Release\net10.0\DumpTool.exe'
$build = Join-Path $install 'RainbowSix_dump.exe'
$house = Join-Path $install 'datapc64_pvp01_house_v3.forge'
$graph = Join-Path $install 'datapc64_pvp01_house_v3.depgraphbin'
& $tool list $house
& $tool unpack $house (Join-Path $out 'house-root.bin') --build $build --uid 0x37625c3900
& $tool unpack $house (Join-Path $out 'house-index.bin') --build $build --uid 0x37625c3900 --metadata
& $tool unpack $graph (Join-Path $out 'house-edges.bin') --build $build
```

`unpack` verifies the supplied dump by SHA-256, opens inputs read-only and
publishes to a new file outside the verified game directory. This is an offline
file operation. Keep samples out of BattleEye-protected installations and
sessions. The command treats the executable as fingerprint input only.

## Local sample archives

The local artifact snapshot is under
`E:\R6ExtractorArtifacts\Y11S2_SystemOverride`. `SHA256SUMS` records each file
and its hash. The original exports are under
`E:\RainbowSIX\ThrowbackLauncher\LevelSuiteExports\Y11S2_SystemOverride`.
The snapshot contains eight sample folders and keeps the archive files and
their generated bank ownership and dependency reports outside Git.

The table records how the original standalone producer generated these fixtures.
Its README in the source installation provides the setup and donor inputs.
Use an existing external output parent and a fresh child directory for each
producer invocation.

| Sample folder | Original producer command and input |
| --- | --- |
| `evn27_power_events_experiment` | `--house-event` with stock House v3 |
| `house_v2_y11s2_experiment` | `--house-legacy` with Blood Orchid House v2 |
| `pvp_test_placement_experiment` | `--bake-house` with `HousePlacementDemo.r6scene` |
| `pvp_test_house_bundle` | `--bake-house-bundle` with `HousePlacementDemo.r6scene` |
| `pvp_test_yaw_experiment` | `--bake-house-bundle` with `HouseYawDemo.r6scene` |
| `themepark_container_port_experiment` | `--port-themepark` with original Blood Orchid Theme Park and a local Oodle encoder |
| `themepark_native_graph_experiment` | `--port-themepark-native-graph` with the same donor and encoder |
| `playlist_catalog_university` | `--stage-playlist-world` with the stock Y11S2 University Forge and a local Oodle encoder |

The original Theme Park samples retain legacy serialized resource bodies. The
playlist sample demonstrates a staged world reference. Runtime compatibility,
map registration and playable loading remain unverified.
