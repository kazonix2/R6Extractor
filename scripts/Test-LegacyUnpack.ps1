param(
    [string] $BuildDirectory = "$PSScriptRoot/../DumpTool/bin/Release/net10.0"
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BuildDirectory 'Zstandard.Net.dll')
Add-Type -Path (Join-Path $BuildDirectory 'RainbowScimitar.dll')

function New-Envelope([byte] $Codec, [uint16] $Size, [byte[]] $Packed, [switch] $CorruptChecksum) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $sum = 0
    $weighted = 0
    foreach ($byte in $Packed) {
        $sum = ($sum + $byte) % 65521
        $weighted = ($weighted + $sum) % 65521
    }
    $checksum = [uint32]($weighted * 65536L + $sum)
    if ($CorruptChecksum) { $checksum = $checksum -bxor 1 }
    $writer.Write([uint64]0x1004fa9957fbaa33)
    $writer.Write([uint16]1)
    $writer.Write($Codec)
    $writer.Write([uint16]0x8000)
    $writer.Write([uint16]0x8000)
    $writer.Write([uint32]1)
    $writer.Write($Size)
    $writer.Write([uint16]$Packed.Length)
    $writer.Write($checksum)
    $writer.Write($Packed)
    return ,$stream.ToArray()
}

function Assert-Rejected([byte[]] $Envelope, [string] $Expected) {
    $reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($Envelope))
    try {
        [RainbowForge.Core.DepGraph]::Unpack($reader).Dispose()
        throw "Expected rejection: $Expected"
    }
    catch {
        if ($_.Exception.GetBaseException() -isnot [IO.InvalidDataException] -or
            $_.Exception.GetBaseException().Message -notmatch $Expected) { throw }
    }
    finally { $reader.Dispose() }
}

# Two literals, then 23 overlapping bytes from distance 1, then end of stream.
$packed = [byte[]]@(19, 1, 0, 53, 0, 0, 17, 0, 0)
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new((New-Envelope 0 25 $packed)))
$graph = [RainbowForge.Core.DepGraph]::Read($reader)
$reader.Dispose()
if ($graph.Version -ne 1 -or $graph.Structs.Count -ne 1 -or $graph.Structs[0].Flags -ne 0) {
    throw 'Unexpected version-1 graph'
}

$rawGraph = [byte[]]::new(25)
$rawGraph[0] = 1
$rawGraph[1] = 42
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($rawGraph))
$graph = [RainbowForge.Core.DepGraph]::Read($reader)
$reader.Dispose()
if ($graph.Version -ne 1 -or $graph.Structs.Count -ne 1 -or $graph.Structs[0].ParentUid -ne 42) {
    throw 'Unexpected raw version-1 graph'
}
Assert-Rejected ([byte[]]@(1, 2)) 'raw dependency graph payload layout'

$member = [byte[]]::new(45)
[BitConverter]::GetBytes([uint32]0x12345678).CopyTo($member, 0)
[BitConverter]::GetBytes([uint32]32).CopyTo($member, 4)
[BitConverter]::GetBytes([uint32]1).CopyTo($member, 8)
$member[12] = [byte][char]'A'
[BitConverter]::GetBytes([uint64]42).CopyTo($member, 13)
$packedMember = [byte[]](@(39) + $member[0..21] + @(53, 0, 0, 17, 0, 0))
$metadata = [byte[]]::new(14)
[BitConverter]::GetBytes([uint16]1).CopyTo($metadata, 0)
[BitConverter]::GetBytes([uint64]42).CopyTo($metadata, 2)
[BitConverter]::GetBytes([uint32]$member.Length).CopyTo($metadata, 10)
$index = New-Envelope 1 $metadata.Length $metadata
$payload = New-Envelope 1 $member.Length $packedMember
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([Text.Encoding]::ASCII.GetBytes("scimitar`0"))
$writer.Write([uint32]29)
$stream.Position = 62
$writer.Write([uint32]1)
$writer.Write([uint64]74)
$writer.Write([int]1)
$writer.Write([int]0)
$writer.Write([long]122)
$writer.Write([long]-1)
$writer.Write([long]0)
$writer.Write([long]142)
$writer.Write([long]462)
$writer.Write([long]4096)
$writer.Write([uint64]42)
$writer.Write([uint32]($index.Length + $payload.Length))
$stream.Position = 142 + 44
$writer.Write([Text.Encoding]::ASCII.GetBytes("Vanilla_Allocation`0"))
$stream.Position = 4096
$writer.Write($index)
$writer.Write($payload)
$stream.Position = 0
$forge = [RainbowForge.Core.Forge]::Read([IO.BinaryReader]::new($stream))
if ($forge.Entries[0].MetaData.FileName -cne 'Vanilla_Allocation') {
    throw 'The v29 Forge table did not return its plain name'
}
$asset = $forge.GetContainer([uint64]42)
foreach ($case in @(
    @{ Reader = $asset.GetMetaDataStream($forge); Bytes = $metadata },
    @{ Reader = $asset.GetDataStream($forge); Bytes = $member },
    @{ Reader = $asset.GetDataPrefixStream($forge, 3); Bytes = $member[0..2] }
)) {
    if ([Convert]::ToHexString($case.Reader.BaseStream.ToArray()) -ne [Convert]::ToHexString($case.Bytes)) {
        throw 'Forge block decoded to unexpected bytes'
    }
    $case.Reader.Dispose()
}
$decoded = $asset.GetDataStream($forge)
$archive = [RainbowForge.Archive.FlatArchive]::Read($forge, $asset, $decoded)
$entry = $archive.Entries[0]
if ($archive.Entries.Count -ne 1 -or $entry.MetaData.Uid -ne 42 -or $entry.MetaData.FileName -cne 'A' -or
    $entry.MetaData.FileType -ne 0x12345678 -or $entry.HeaderLength -ne 21 -or $entry.PayloadLength -ne 45) {
    throw 'Legacy directory did not identify the named member'
}
$decoded.Dispose()
$forge.Stream.Dispose()

# Closed Alpha relocates the FAT header and can link to a second descriptor.
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([Text.Encoding]::ASCII.GetBytes("scimitar`0"))
$writer.Write([uint32]29)
$writer.Write([uint32]0x41a)
$stream.Position = 0x41a
$writer.Write([uint32]2)
$stream.Position = 0x41a + 32
$writer.Write([uint32]2)
$writer.Write([long]1094)
foreach ($table in @(
    @{ Position = 1094; Next = 1500; Offset = 4096; Uid = 42; Size = $index.Length + $payload.Length },
    @{ Position = 1500; Next = -1; Offset = 8192; Uid = 0xbf0; Size = 112 }
)) {
    $stream.Position = $table.Position
    $writer.Write([int]1)
    $writer.Write([int]0)
    $writer.Write([long]($table.Position + 48))
    $writer.Write([long]$table.Next)
    $writer.Write([long]0)
    $writer.Write([long]($table.Position + 68))
    $writer.Write([long]($table.Position + 388))
    $writer.Write([long]$table.Offset)
    $writer.Write([uint64]$table.Uid)
    $writer.Write([uint32]$table.Size)
}
$stream.Position = 4096
$writer.Write($index)
$writer.Write($payload)
$stream.Position = 8192
$writer.Write([uint32]5)
$writer.Write([uint64]123)
$writer.Write([uint32]0)
$writer.Write([uint32]1)
$writer.Write([Text.Encoding]::ASCII.GetBytes('Alpha_Map'))
$stream.SetLength(8192 + 112)
$stream.Position = 0
$forge = [RainbowForge.Core.Forge]::Read([IO.BinaryReader]::new($stream))
$toc = $forge.GetContainer([uint64]0xbf0)
if ($forge.Entries.Count -ne 2 -or $forge.Entries[0].Uid -ne 42 -or $toc.Name -cne 'Alpha_Map') {
    throw 'Closed Alpha allocation tables or fast-load record did not decode'
}
$reference = [byte[]]::new(61)
[BitConverter]::GetBytes([uint32]0x415D9568).CopyTo($reference, 0)
[BitConverter]::GetBytes([uint64]123).CopyTo($reference, 20)
[BitConverter]::GetBytes([uint32]1).CopyTo($reference, 40)
[BitConverter]::GetBytes([uint64]456).CopyTo($reference, 44)
$reference[60] = 3
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($reference))
$compiledUid = [uint64]0
$materials = [uint64[]]@()
if (![RainbowForge.RenderPipeline.Mesh]::TryReadLegacyReference($reader, $forge, [ref]$compiledUid, [ref]$materials) -or
    $compiledUid -ne 123 -or $materials[0] -ne 456) { throw 'Closed Alpha bank mesh reference did not decode' }
$reader.Dispose()
$forge.Stream.Dispose()

Assert-Rejected (New-Envelope 0 25 $packed -CorruptChecksum) 'checksum mismatch'
$packed[4] = 8 # Distance 3 after only two bytes of history, with a valid checksum.
Assert-Rejected (New-Envelope 0 25 $packed) 'back-reference exceeds'

foreach ($name in @('Vanilla_Texture', '', 'April_Texture')) {
    $april = $name -eq 'April_Texture'
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $nameBytes = [Text.Encoding]::ASCII.GetBytes($name)
    $fileType = [uint32]0xD7B5C478L
    $surface = [byte[]]@(0, 248, 0, 0, 0, 0, 0, 0)
    $writer.Write($fileType)
    $writer.Write([uint32](125 + [int]$april)) # UID, texture body, surface, and footer.
    $writer.Write([uint32]$nameBytes.Length)
    $writer.Write($nameBytes)
    $writer.Write([uint64]42)
    foreach ($word in @($fileType, 0, 0, 0x13237FE9, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0xC30C4B3DL)) {
        $writer.Write([uint32]$word)
    }
    $writer.Write([uint32]$(if ($april) { $surface.Length + 4 } else { 260 }))
    $writer.Write([uint32]7)
    $writer.Write($surface)
    foreach ($word in @(4, 4, 1, 0, 0, 0, 1, 0)) { $writer.Write([uint32]$word) }
    if ($april) { $writer.Write([byte]1) }
    $writer.Write([uint32]7)
    $writer.Write([uint32]7)
    $writer.Write([byte]1)

    $stream.Position = 0
    $reader = [IO.BinaryReader]::new($stream)
    $texture = [RainbowForge.Image.Texture]::Read($reader, 29)
    if ($texture.MetaData.Uid -ne 42 -or $texture.MetaData.FileName -cne $name -or
        $texture.Width -ne 4 -or $texture.Height -ne 4 -or $texture.Mips -ne 1 -or
        [Convert]::ToHexString($texture.ReadSurfaceBytes($reader)) -ne [Convert]::ToHexString($surface)) {
        throw "Unexpected v29 texture metadata or surface for '$name'"
    }
    $reader.Dispose()
}
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
# A v29 mesh with two parts, one LOD, and a separate vertex base for the second part.
foreach ($word in @(0xABEB2DFBL, 0, 0, 0, 0, 0xABEB2DFBL, 0, 0, 0xFC9E1595L, 0)) {
    $writer.Write([uint32]$word)
}
foreach ($word in @(19, 2, 24, 144, 768, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 2, 0, 0, 0, 0, 0)) {
    $writer.Write([uint32]$word)
}
foreach ($vertex in 0..5) {
    $writer.Write([int16]$vertex)
    $writer.Write([int16]0)
    $writer.Write([int16]0)
    $writer.Write([int16]32767)
    $writer.Write([byte[]]::new(16))
}
foreach ($part in 0..1) {
    foreach ($index in @(0, 1, 2)) { $writer.Write([uint16]$index) }
    $writer.Write([byte[]]::new(378))
}
foreach ($part in 0..1) {
    foreach ($word in @(4, ($part * 3), 3, $part, 1, $part, 0, 0, 0)) { $writer.Write([uint32]$word) }
}
$writer.Write([byte[]]::new(56))
$meshBytes = $stream.ToArray()
$stream.Position = 0
$reader = [IO.BinaryReader]::new($stream)
$header = [RainbowForge.Model.MeshHeader]::Read($reader, 29)
$mesh = [RainbowForge.Model.CompiledMeshObject]::Read($reader, $header)
if ($header.NumLods -ne 1 -or $header.VertBlockOffset -ne 128 -or $header.ObjectBoundingBoxes.Length -ne 2 -or
    $mesh.Objects.Count -ne 2 -or $mesh.Objects[1].Length -ne 1 -or $mesh.Objects[1][0].C -ne 5 -or
    $mesh.Container.Vertices[5].X -ne 5) {
    throw 'Early v29 mesh header, vertices, or part indices did not decode'
}
$reader.Dispose()

# Revision 0 stores the same two faces without chunk padding.
$rawFaces = [byte[]]($meshBytes[0..271] + $meshBytes[272..277] + $meshBytes[656..661] + $meshBytes[1040..1111])
[BitConverter]::GetBytes([uint32]0).CopyTo($rawFaces, 44)
[BitConverter]::GetBytes([uint32]12).CopyTo($rawFaces, 56)
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($rawFaces))
$header = [RainbowForge.Model.MeshHeader]::Read($reader, 29)
$mesh = [RainbowForge.Model.CompiledMeshObject]::Read($reader, $header)
if ($mesh.Objects[1].Length -ne 1 -or $mesh.Objects[1][0].C -ne 5) { throw 'Legacy unpadded faces did not decode' }
$reader.Dispose()

# The April Mesh resource embeds the compiled block at byte 29 of its body.
[BitConverter]::GetBytes([uint32]($meshBytes.Length - 40)).CopyTo($meshBytes, 36)
$prefix = [byte[]]::new(29)
[BitConverter]::GetBytes([uint32]0x415D9568).CopyTo($prefix, 0)
$inline = [byte[]]($prefix + $meshBytes[32..($meshBytes.Length - 1)])
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($inline))
$metadata = [RainbowForge.FileMetaData]::CreateDetected('Embedded', 0, 0x415D9568, 42)
$header = $null
$compiledEnd = [long]0
if (![RainbowForge.Model.MeshHeader]::TryReadEmbedded($reader, 29, $metadata, [ref]$header, [ref]$compiledEnd) -or
    $compiledEnd -ne $inline.Length) { throw 'April Alpha embedded mesh header did not decode' }
$mesh = [RainbowForge.Model.CompiledMeshObject]::Read($reader, $header)
if ($mesh.Objects[1][0].C -ne 5) { throw 'April Alpha embedded mesh geometry did not decode' }
$reader.Dispose()

# A v34 allocation ends at the second envelope, including zero bytes in its payload.
function New-V34StoredBlock([byte[]] $Bytes) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $sum = 0
    $weighted = 0
    foreach ($byte in $Bytes) {
        $sum = ($sum + $byte) % 65521
        $weighted = ($weighted + $sum) % 65521
    }
    $writer.Write([uint64]0x1015fa9957fbaa37)
    $writer.Write([uint16]3)
    $writer.Write([byte]15)
    $writer.Write([uint16]0)
    $writer.Write([uint16]0x8004)
    $writer.Write([uint32]1)
    $writer.Write([uint32]$Bytes.Length)
    $writer.Write([uint32]$Bytes.Length)
    $writer.Write([uint32]($weighted * 65536L + $sum))
    $writer.Write($Bytes)
    return ,$stream.ToArray()
}

$page = 0x1000
$member = [byte[]]@(13, 0, 7, 0, 0)
$meta = [byte[]]::new(16)
[BitConverter]::GetBytes([uint32]1).CopyTo($meta, 0)
[BitConverter]::GetBytes([uint64]42).CopyTo($meta, 4)
[BitConverter]::GetBytes([uint32]$member.Length).CopyTo($meta, 12)
$allocation = [byte[]]((New-V34StoredBlock $meta) + (New-V34StoredBlock $member))
$fixture = [byte[]]::new($page * 3)
[Text.Encoding]::ASCII.GetBytes("scimitar`0").CopyTo($fixture, 0)
[BitConverter]::GetBytes([uint32]34).CopyTo($fixture, 9)
$allocation.CopyTo($fixture, $page)
$fixture[$page * 2 - 3] = 0xEF
$allocation.CopyTo($fixture, $page * 2)
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($fixture))
try {
    $forge = [RainbowForge.Core.Forge]::Read($reader, $null)
    if ($forge.Entries.Count -ne 2 -or $forge.Entries[0].Size -ne $allocation.Length -or
        $forge.Entries[1].Size -ne $allocation.Length) { throw 'v34 allocation boundaries did not match the envelopes' }
    $container = $forge.GetContainer([int]0)
    $payload = $container.GetDataStream($forge)
    try {
        if ([Convert]::ToHexString($payload.BaseStream.ToArray()) -ne [Convert]::ToHexString($member)) {
            throw 'v34 allocation lost payload bytes'
        }
    }
    finally { $payload.Dispose() }
}
finally { $reader.Dispose() }

# The Blood Orchid material prefix is variable. The UV markers delimit the layers.
$material = [byte[]]::new(660)
[BitConverter]::GetBytes([uint32]0x85C817C3L).CopyTo($material, 0)
foreach ($offset in @(226, 254, 282, 310, 393, 421, 449, 588, 616)) {
    [BitConverter]::GetBytes([uint32]0x7D08460D).CopyTo($material, $offset)
    [BitConverter]::GetBytes([uint64]$offset).CopyTo($material, ($offset + 4))
}
foreach ($offset in @(338, 477)) { [BitConverter]::GetBytes([uint32]0xC52E2125L).CopyTo($material, $offset) }
foreach ($offset in @(532, 560)) { [BitConverter]::GetBytes([uint32]0x7799A6D5).CopyTo($material, $offset) }
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($material))
try {
    $parsed = [RainbowForge.RenderPipeline.Material]::Read($reader, 29)
    if ($parsed.BaseTextureMapSpecs.Length -ne 4 -or $parsed.SecondaryTextureMapSpecs[0].TextureMapSpecUid -ne 393 -or
        $parsed.TertiaryTextureMapSpecs[1].TextureMapSpecUid -ne 616 -or $parsed.BaseUvTransform.Length -ne 43) {
        throw 'Blood Orchid material selector or UV blocks decoded incorrectly.'
    }
}
finally { $reader.Dispose() }

$textureMap = [byte[]]::new(157)
[BitConverter]::GetBytes([uint32]0xA2B7E917L).CopyTo($textureMap, 0)
[BitConverter]::GetBytes([uint64]123).CopyTo($textureMap, 8)
[BitConverter]::GetBytes([uint64]456).CopyTo($textureMap, 116)
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($textureMap))
try {
    $parsedMap = [RainbowForge.RenderPipeline.TextureMap]::Read($reader, 29)
    if ($parsedMap.TexUidMipSet1[0] -ne 123 -or $parsedMap.TexUidMipSet2[0] -ne 456) {
        throw 'Blood Orchid texture map references decoded incorrectly.'
    }
}
finally { $reader.Dispose() }

$textureMap = [byte[]]::new(174)
[BitConverter]::GetBytes([uint32]0xA2B7E917L).CopyTo($textureMap, 0)
[BitConverter]::GetBytes([uint32]4).CopyTo($textureMap, 108)
[Text.Encoding]::ASCII.GetBytes('/tex').CopyTo($textureMap, 112)
[BitConverter]::GetBytes([uint64]123).CopyTo($textureMap, 133)
[BitConverter]::GetBytes([uint64]456).CopyTo($textureMap, 165)
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($textureMap))
try {
    $parsedMap = [RainbowForge.RenderPipeline.TextureMap]::Read($reader, 29)
    if ($parsedMap.TexUidMipSet1.Length -ne 0 -or $parsedMap.TexUidMipSet2[0] -ne 123 -or
        $parsedMap.TexUidMipSet2[4] -ne 456) { throw 'August texture map mip links were lost.' }
}
finally { $reader.Dispose() }

$textureMap = [byte[]]::new(127)
[BitConverter]::GetBytes([uint32]0xA2B7E917L).CopyTo($textureMap, 0)
[BitConverter]::GetBytes([uint32]0x13237FE9).CopyTo($textureMap, 21)
[BitConverter]::GetBytes([uint32]2).CopyTo($textureMap, 25)
[BitConverter]::GetBytes([uint32]0xC30C4B3DL).CopyTo($textureMap, 65)
[BitConverter]::GetBytes([uint32]12).CopyTo($textureMap, 69)
$textureMap[77] = 253
foreach ($offset in 85, 89) { [BitConverter]::GetBytes([int]4).CopyTo($textureMap, $offset) }
foreach ($offset in 93, 109) { [BitConverter]::GetBytes([uint32]1).CopyTo($textureMap, $offset) }
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($textureMap))
try {
    $inline = $null
    if (![RainbowForge.Image.Texture]::TryReadAprilInline($reader, [ref]$inline) -or $inline.Width -ne 4 -or
        $inline.TexFormat -ne 2 -or $inline.ReadSurfaceBytes($reader)[0] -ne 253) {
        throw 'April inline texture mip data was lost.'
    }
}
finally { $reader.Dispose() }

'Legacy Forge tables, graphs, malformed input, textures, meshes, materials, and v34 bounds passed.'
