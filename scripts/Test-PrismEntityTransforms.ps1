param(
    [string] $BuildDirectory = "$PSScriptRoot/../Prism/bin/Release/net10.0-windows"
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BuildDirectory 'OpenTK.dll')
$prism = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'Prism.dll'))
$reader = $prism.GetType('Prism.SceneLightingReader', $true).GetMethod(
    'TryReadEntityMatrix', [Reflection.BindingFlags]'NonPublic,Static')

foreach ($version in @(29, 33)) {
    $offset = if ($version -eq 29) { 4 } else { 5 }
    $bytes = [byte[]]::new($offset + 64)
    [BitConverter]::GetBytes([uint32]0x0984415E).CopyTo($bytes, 0)
    # 90-degree rotation followed by a non-zero translation.
    $matrix = [float[]]@(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 10, -20, 30, 1)
    [Buffer]::BlockCopy($matrix, 0, $bytes, $offset, 64)
    $arguments = [object[]]@($bytes, [uint32]$version, $null)
    if (!$reader.Invoke($null, $arguments)) { throw "Version $version rejected a valid entity matrix." }
    $result = $arguments[2]
    if ($result.M41 -ne 10 -or $result.M42 -ne -20 -or $result.M43 -ne 30 -or
        $result.M12 -ne 1 -or $result.M21 -ne -1) {
        throw "Version $version lost translation or transposed rotation."
    }

    [BitConverter]::GetBytes([float]::NaN).CopyTo($bytes, $offset + 48)
    if ($reader.Invoke($null, $arguments)) { throw "Version $version accepted a non-finite translation." }
    $arguments[0] = [byte[]]$bytes[0..($bytes.Length - 2)]
    if ($reader.Invoke($null, $arguments)) { throw "Version $version accepted a truncated matrix." }
}

$type = $prism.GetType('Prism.SceneLightingReader', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$groupReader = $type.GetMethod('ReadDestructionMeshGroup', $flags)
$findMeshes = $type.GetMethod('FindEntityMeshes', $flags)
$group = [byte[]]::new(28)
[BitConverter]::GetBytes([uint32]0xAF53B819L).CopyTo($group, 0)
[BitConverter]::GetBytes([uint32]2).CopyTo($group, 8)
[BitConverter]::GetBytes([uint64]0x123456789).CopyTo($group, 12)
[BitConverter]::GetBytes([uint64]0x123456788).CopyTo($group, 20)
$members = $groupReader.Invoke($null, [object[]]@(,$group))
if ($members.Length -ne 2) { throw 'Destruction mesh group members were lost.' }
$truncated = [byte[]]$group[0..26]
if ($groupReader.Invoke($null, [object[]]@(,$truncated)).Length -ne 0) {
    throw 'A truncated destruction mesh group was accepted.'
}
$groups = [Collections.Generic.Dictionary[uint64, uint64[]]]::new()
$groupUid = [uint64]0xabcdef0123
$groups.Add($groupUid, $members)
$meshes = [Collections.Generic.HashSet[uint64]]::new($members)
$entity = [byte[]]::new(95)
[BitConverter]::GetBytes($groupUid).CopyTo($entity, 73)
[BitConverter]::GetBytes($members[0]).CopyTo($entity, 83)
$found = $findMeshes.Invoke($null, [object[]]@($entity, [uint32]29, $meshes, $groups))
if ($found.Count -ne 2 -or !$found.Contains($members[1])) {
    throw 'Entity mesh links must include destruction groups and deduplicate direct links.'
}

$direct = [uint64]0x123456781
$nested = [uint64]0x123456782
[void]$meshes.Add($direct)
[void]$meshes.Add($nested)
$modern = [byte[]]::new(160)
[BitConverter]::GetBytes([uint32]0x3ADFCF6C).CopyTo($modern, 0)
[BitConverter]::GetBytes([uint32]0xC5EA802BL).CopyTo($modern, 74)
$modern[78] = 1
[BitConverter]::GetBytes($groupUid).CopyTo($modern, 80)
([byte[]]@(0xf8, 0xfb, 0, 0, 0, 0)).CopyTo($modern, 100)
[BitConverter]::GetBytes([uint32]0x89EBEBE5L).CopyTo($modern, 106)
[BitConverter]::GetBytes($direct).CopyTo($modern, 110)
[BitConverter]::GetBytes([uint32]0xEE3E2373L).CopyTo($modern, 126)
[BitConverter]::GetBytes($nested).CopyTo($modern, 130)
$found = $findMeshes.Invoke($null, [object[]]@($modern, [uint32]34, $meshes, $groups))
if ($found.Count -ne 3 -or !$found.Contains($direct) -or !$found.Contains($members[1]) -or $found.Contains($nested)) {
    throw 'Modern entity placements must use direct references and preserve destruction groups.'
}

$readNested = $type.GetMethod('ReadNestedDestructionPlacements', $flags)
$collection = [byte[]]::new(280)
[BitConverter]::GetBytes([uint32]0x745FDD6D).CopyTo($collection, 0)
[BitConverter]::GetBytes([uint32]0xC5EA802BL).CopyTo($collection, 72)
[BitConverter]::GetBytes($groupUid).CopyTo($collection, 78)
[BitConverter]::GetBytes([uint32]0x3ADFCF6C).CopyTo($collection, 104)
$matrix = [float[]]@(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 15, -9, 2, 1)
[Buffer]::BlockCopy($matrix, 0, $collection, 108, 64)
[BitConverter]::GetBytes([uint32]0xC5EA802BL).CopyTo($collection, 200)
$collection[204] = 1
[BitConverter]::GetBytes($groupUid).CopyTo($collection, 206)
$placed = @($readNested.Invoke($null, [object[]]@($collection, $groups)))
if ($placed.Length -ne 2 -or $placed[0].Item2.M41 -ne 15 -or $placed[0].Item2.M42 -ne -9) {
    throw 'Nested destruction geometry must use its own world transform.'
}

$readLight = $type.GetMethod('ReadModernLight', $flags)
$entity = [byte[]]::new(420)
$component = 140
([byte[]]@(0xf8, 0xfb, 0, 0, 0, 0)).CopyTo($entity, $component - 27)
[BitConverter]::GetBytes([uint32]0xCF25BE94L).CopyTo($entity, $component - 21)
([byte[]]@(0xf8, 0xfb, 0, 0, 0, 0)).CopyTo($entity, $component - 6)
[BitConverter]::GetBytes([uint32]0x0D6DBCA9).CopyTo($entity, $component)
[BitConverter]::GetBytes([single]1).CopyTo($entity, $component + 101)
[BitConverter]::GetBytes([single]0.5).CopyTo($entity, $component + 105)
[BitConverter]::GetBytes([single]0.25).CopyTo($entity, $component + 109)
[BitConverter]::GetBytes([single]120).CopyTo($entity, $component + 117)
[BitConverter]::GetBytes([single]5).CopyTo($entity, $component + 264)
[BitConverter]::GetBytes([single]0.4).CopyTo($entity, $component + 268)
[BitConverter]::GetBytes([single]0.8).CopyTo($entity, $component + 272)
$lightArgs = [object[]]@($entity, [uint64]123, [OpenTK.Vector3]::UnitX, [OpenTK.Vector3]::UnitZ)
$light = $readLight.Invoke($null, $lightArgs)
if ($light.Kind.ToString() -ne 'Spot' -or $light.Radius -ne 5 -or $light.Color.Y -ne 0.5 -or
    $light.Position.X -ne 1 -or $light.InnerConeCosine -le $light.OuterConeCosine) {
    throw 'Modern light placement, color, range or cone was lost.'
}
[BitConverter]::GetBytes([single]::NaN).CopyTo($entity, $component + 264)
if ($null -ne $readLight.Invoke($null, $lightArgs)) { throw 'A non-finite light range was accepted.' }

$readGrid = $type.GetMethod('ReadGiGrid', $flags)
$grid = [byte[]]::new(4 + 64 + 4 + 1024 + 4 + 8 + 48)
[BitConverter]::GetBytes([uint32]64).CopyTo($grid, 0)
[BitConverter]::GetBytes([uint32]1024).CopyTo($grid, 68)
[BitConverter]::GetBytes([uint32]8).CopyTo($grid, 1096)
foreach ($offset in 1136, 1140, 1144) {
    [BitConverter]::GetBytes([single]1).CopyTo($grid, $offset)
}
[BitConverter]::GetBytes([uint32]8).CopyTo($grid, 1148)
[BitConverter]::GetBytes([uint32]16).CopyTo($grid, 1152)
$stream = [IO.BinaryReader]::new([IO.MemoryStream]::new($grid))
try {
    $result = $readGrid.Invoke($null, [object[]]@($stream))
    if ($result.CellCount -ne 1 -or $result.SampleTiles.Length -ne 64) { throw 'GI tile was lost.' }
    $stream.BaseStream.Position = 0
    $grid[68] = 1
    if ($null -ne $readGrid.Invoke($null, [object[]]@($stream))) { throw 'Invalid GI section length was accepted.' }
}
finally {
    $stream.Dispose()
}

'Prism entity transforms, destruction groups, light components and GI layouts passed.'
