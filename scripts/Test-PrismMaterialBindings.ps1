param([string] $BuildDirectory = "$PSScriptRoot/../Prism/bin/Release/net10.0-windows")

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BuildDirectory 'OpenTK.dll')
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'Prism.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$classify = $assembly.GetType('Prism.Render.SceneTextureData', $true).GetMethod('ClassifyAlpha', $flags)
foreach ($fixture in @(
    @{ Name = 'Opaque'; Pixels = [byte[]]@(0, 0, 0, 255) },
    @{ Name = 'Cutout'; Pixels = [byte[]]@(0, 0, 0, 255, 0, 0, 0, 0) },
    @{ Name = 'Blend'; Pixels = [byte[]]@(0, 0, 0, 128, 0, 0, 0, 255) }
)) {
    $mode = $classify.Invoke($null, [object[]]@(,$fixture.Pixels))
    if ($mode.ToString() -ne $fixture.Name) { throw "Incorrect alpha mode for $($fixture.Name)." }
}

$getOpacity = $assembly.GetType('Prism.SceneMaterialReader', $true).GetMethod('GetPreviewOpacity', $flags)
$textureConstructor = $classify.DeclaringType.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')[0]
foreach ($fixture in @(
    @{ Format = 3; Mode = 'Cutout'; Alpha = 0 },
    @{ Format = 5; Mode = 'Opaque'; Alpha = 96 },
    @{ Format = 16; Mode = 'Opaque'; Alpha = 0 }
)) {
    $texture = $textureConstructor.Invoke([object[]]@([uint64]1, 1, 1,
        [byte[]]@(0, 0, 0, $fixture.Alpha), [uint32]$fixture.Format))
    $mode = $getOpacity.Invoke($null, [object[]]@($texture))
    if ($mode.ToString() -ne $fixture.Mode) { throw "Unexpected opacity for texture format $($fixture.Format)." }
}

$bindingType = $assembly.GetType('Prism.SceneMaterialReader+Binding', $true)
$uv = [byte[]]::new(43)
[BitConverter]::GetBytes([single]2).CopyTo($uv, 4)
[BitConverter]::GetBytes([single]0.5).CopyTo($uv, 8)
$binding = [Activator]::CreateInstance($bindingType, [object[]]@($uv, [byte[]]::new(0), $null))
$scale = $binding.UvScale([uint32]29)
if ($scale.X -ne 2 -or $scale.Y -ne 0.5) { throw 'The legacy UV axes did not retain their independent scales.' }
if ($binding.UvScale([uint32]34) -ne [OpenTK.Vector2]::One) { throw 'Legacy UV offsets were applied to a modern material.' }
[BitConverter]::GetBytes([single]::NaN).CopyTo($uv, 8)
if ($binding.UvScale([uint32]29) -ne [OpenTK.Vector2]::One) { throw 'Invalid UV scale was accepted.' }

$resolveOpacity = $getOpacity.DeclaringType.GetMethod('ResolveOpacity', $flags)
$texture = $textureConstructor.Invoke([object[]]@([uint64]1, 1, 1, [byte[]]@(0, 0, 0, 96), [uint32]5))
$binding.AlphaTest = $true
if ($resolveOpacity.Invoke($null, [object[]]@($binding, $texture)).ToString() -ne 'Cutout') {
    throw 'The alpha-test shader lost coverage from a BC3 diffuse map.'
}
$binding.Glass = $true
if ($resolveOpacity.Invoke($null, [object[]]@($binding, $null)).ToString() -ne 'Blend') {
    throw 'Glass without a diffuse map must still blend.'
}

$readTint = $getOpacity.DeclaringType.GetMethod('ReadTint', $flags)
$tintBytes = [byte[]]::new(28)
foreach ($offset in 4, 12) {
    $color = [float[]]@(0.75, 0.25, 0.5, 1)
    [Buffer]::BlockCopy($color, 0, $tintBytes, $offset, 16)
    $tint = $readTint.Invoke($null, [object[]]@($tintBytes, $offset))
    if ($tint.X -ne 0.75 -or $tint.Y -ne 0.25 -or $tint.Z -ne 0.5) { throw 'Tint channels were lost.' }
}
[BitConverter]::GetBytes([single]::NaN).CopyTo($tintBytes, 12)
if ($readTint.Invoke($null, [object[]]@($tintBytes, 12)) -ne [OpenTK.Vector3]::One) {
    throw 'Invalid tint must use the neutral color.'
}

'Prism UV scales, alpha modes and tints passed.'
