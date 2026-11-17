param([string] $BuildDirectory = "$PSScriptRoot/../DumpTool/bin/Release/net10.0")

$ErrorActionPreference = 'Stop'
$tool = [Reflection.Assembly]::LoadFrom((Join-Path $BuildDirectory 'DumpTool.dll'))
$read = $tool.GetType('DumpTool.UefTimeline', $true).GetMethod('Read')

# One frame, one external bitmap, and an instance named icon at (10, -5) pixels.
$clip = [Convert]::FromHexString('5545460F4500000060003E80001F40001801008F3D0700080004003078313233000000008E06260300070012C8CE0069636F6E000B130100000053616D706C650040000000')
$result = $read.Invoke($null, [object[]]@(,$clip))
$placement = $result.Stage.Frames[0].Changes[0]
if ($result.Width -ne 2000 -or $result.Height -ne 1000 -or $result.FramesPerSecond -ne 24 -or
    $result.RootClass -ne 'Sample' -or $result.Symbols[[uint16]7].Resource -ne '0x123' -or
    $placement.Depth -ne 3 -or $placement.Character -ne 7 -or $placement.Name -ne 'icon' -or
    $placement.Matrix[4] -ne 10 -or $placement.Matrix[5] -ne -5) {
    throw 'UEF stage, symbol, or display placement decoded incorrectly.'
}

$truncated = [byte[]]$clip[0..($clip.Length - 2)]
try {
    $null = $read.Invoke($null, [object[]]@(,$truncated))
    throw 'Accepted a truncated UEF tag.'
}
catch {
    if ($_.Exception.GetBaseException() -isnot [IO.InvalidDataException]) { throw }
}

'UEF timeline fixture passed.'
