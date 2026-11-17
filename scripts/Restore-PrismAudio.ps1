param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Force -Path $Directory | Out-Null

function Install-AudioArchive($Url, $Executable, $Destination) {
    if (Test-Path (Join-Path $Destination $Executable)) { return }
    $temp = [IO.Directory]::CreateTempSubdirectory('Prism-audio-build-')
    try {
        $zip = Join-Path $temp.FullName 'audio.zip'
        $expanded = Join-Path $temp.FullName 'files'
        Invoke-WebRequest -Uri $Url -OutFile $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $expanded
        $binary = Get-ChildItem -LiteralPath $expanded -Recurse -Filter $Executable | Select-Object -First 1
        if (!$binary) { throw "The audio archive did not contain $Executable." }
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        if ($Executable -eq 'ffmpeg.exe') {
            Copy-Item -LiteralPath $binary.FullName -Destination $Destination
            Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object Name -Match '^LICENSE' |
                Select-Object -First 1 | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination 'FFmpeg-LICENSE.txt') }
        }
        else {
            Get-ChildItem -LiteralPath $binary.DirectoryName -File |
                Where-Object { $_.Name -ne $Executable -and $_.Extension -ne '.exe' } |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination }
            Copy-Item -LiteralPath $binary.FullName -Destination $Destination
        }
    }
    finally {
        $temp.Delete($true)
    }
}

Install-AudioArchive 'https://github.com/vgmstream/vgmstream/releases/download/r2168/vgmstream-win64.zip' 'vgmstream-cli.exe' (Join-Path $Directory 'vgmstream')
Install-AudioArchive 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip' 'ffmpeg.exe' $Directory
