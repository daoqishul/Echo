Add-Type -AssemblyName System.Drawing

# 路径相对脚本所在目录解析（可移植：克隆到任何目录都能跑）。
# 素材：src\Ncrust.App\Assets\Logo256.png（换图标就替换这个文件，建议 ≥512×512 png）。
$repoRoot = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repoRoot "src\Ncrust.App\Assets"
$sourcePng = Join-Path $assets "Logo256.png"
$outIco = Join-Path $assets "app.ico"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$src = New-Object System.Drawing.Bitmap $sourcePng
$entries = New-Object System.Collections.ArrayList
$imageData = New-Object System.Collections.ArrayList

$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $src, (New-Object System.Drawing.Size $size, $size)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    [void]$imageData.Add($bytes)

    $w = if ($size -eq 256) { 0 } else { $size }
    $entry = New-Object byte[] 16
    $entry[0] = [byte]$w          # width
    $entry[1] = [byte]$w          # height
    $entry[2] = 0                 # colors
    $entry[3] = 0                 # reserved
    [BitConverter]::GetBytes([uint16]1).CopyTo($entry, 4)   # planes
    [BitConverter]::GetBytes([uint16]32).CopyTo($entry, 6)  # bpp
    [BitConverter]::GetBytes([uint32]$bytes.Length).CopyTo($entry, 8)
    [BitConverter]::GetBytes([uint32]$offset).CopyTo($entry, 12)
    [void]$entries.Add($entry)
    $offset += $bytes.Length
}

$fs = New-Object System.IO.FileStream $outIco, Create
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)   # reserved
$bw.Write([uint16]1)   # type = icon
$bw.Write([uint16]$sizes.Count)
foreach ($e in $entries) { $bw.Write($e) }
foreach ($d in $imageData) { $bw.Write($d) }
$bw.Close()
$fs.Dispose()
$src.Dispose()

# 托盘图标也重新生成（16/24/32/48，托盘随 DPI 缩放用）
$outTray = Join-Path $assets "tray.ico"
$src2 = New-Object System.Drawing.Bitmap $sourcePng
$sizes2 = @(16, 24, 32, 48)
$entries2 = New-Object System.Collections.ArrayList
$imageData2 = New-Object System.Collections.ArrayList
$offset2 = 6 + 16 * $sizes2.Count
foreach ($size in $sizes2) {
    $bmp = New-Object System.Drawing.Bitmap $src2, (New-Object System.Drawing.Size $size, $size)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
    [void]$imageData2.Add($bytes)
    $entry = New-Object byte[] 16
    $entry[0] = [byte]$size; $entry[1] = [byte]$size
    [BitConverter]::GetBytes([uint16]1).CopyTo($entry, 4)
    [BitConverter]::GetBytes([uint16]32).CopyTo($entry, 6)
    [BitConverter]::GetBytes([uint32]$bytes.Length).CopyTo($entry, 8)
    [BitConverter]::GetBytes([uint32]$offset2).CopyTo($entry, 12)
    [void]$entries2.Add($entry)
    $offset2 += $bytes.Length
}
$fs2 = New-Object System.IO.FileStream $outTray, Create
$bw2 = New-Object System.IO.BinaryWriter $fs2
$bw2.Write([uint16]0); $bw2.Write([uint16]1); $bw2.Write([uint16]$sizes2.Count)
foreach ($e in $entries2) { $bw2.Write($e) }
foreach ($d in $imageData2) { $bw2.Write($d) }
$bw2.Close(); $fs2.Dispose(); $src2.Dispose()

"app.ico: $((Get-Item $outIco).Length) bytes, $($sizes.Count) sizes"
"tray.ico: $((Get-Item $outTray).Length) bytes, $($sizes2.Count) sizes"
