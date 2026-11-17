# Early-season filesystem unpacking

The C# Forge reader recognizes the early Siege v29 allocations with version-1
chunk envelopes. These store 16-bit sizes for chunks of up to 32 KiB. Both
observed codec IDs use LZO1X for compressed chunks, and equal packed and decoded
sizes indicate stored bytes. The reader checks the packed checksum and the
allocation bounds. It also reads the 16-bit member count and length table used
by these Forge groups. Dependency graphs preserve the leading version byte and
the 24-byte edge records. LZO decoding runs in managed code. [LZO1X format reference](https://www.kernel.org/doc/html/latest/staging/lzo.html)

The implementation selects the decoder from the archive header. Closed Alpha
builds 6836491 (April) and 7558694 (August) and Y1S0 Vanilla were tested directly.
Both Alpha builds use a relocated Forge allocation table. August stores its
dependency graphs without a compression wrapper. April stores compiled meshes
inside map Mesh resources, with textures in `DataPC64_Textures.forge`.
Y1S1 through Y2S2 builds using the supported layouts
are covered by the reader, with per-build validation pending on those installs.
The later Zstandard and Oodle paths remain available.

From the repository root, set `$install` to the Y1S0 directory and `$out` to an
existing directory outside the game installation. The supplied dump serves as
the SHA-256 fingerprint for this offline workflow.

```powershell
dotnet build RainbowForge.sln -c Release
$tool = '.\DumpTool\bin\Release\net10.0\DumpTool.exe'
$build = Join-Path $install 'RainbowSix_Dump.exe'
$house = Join-Path $install 'datapc64_pvp01_house_v2.forge'
$graph = Join-Path $install 'datapc64_pvp01_house_v2_set01.depgraphbin'
& $tool list $house
& $tool unpack $house (Join-Path $out 'house-group.bin') --build $build --uid 857538214
& $tool unpack $house (Join-Path $out 'house-index.bin') --build $build --uid 857538214 --metadata
& $tool unpack $graph (Join-Path $out 'house-edges.bin') --build $build
```

For Closed Alpha, use the supplied build's `RainbowSix_Dump.exe` as `$build` and
point `$install` at its Closed Alpha directory. Allocation `0x800` in
`DataPC64.forge` is an example for both builds. August also has
`datapc64_bootstrap.depgraphbin`.

Run `pwsh -File scripts/Test-LegacyUnpack.ps1` after the Release build to check
the reader against synthetic Forge and dependency graph inputs.
