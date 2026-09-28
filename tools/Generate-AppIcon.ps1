<#
.SYNOPSIS
    Sinh biểu tượng ứng dụng (app.ico) và logo cho giao diện web.

.DESCRIPTION
    Vẽ bằng GDI+ rồi xuống cỡ, không cần thư viện ngoài.

    Nguyên tắc thiết kế:
      - Hình vuông bo góc nền chuyển sắc xanh lam sang xanh mòng két, đúng màu nhấn
        của giao diện (--accent), để icon và sản phẩm là một.
      - Bên trong là mũi tên tụt xuống: nét thân là ba vạch ngang xếp chồng (dữ liệu
        nhiều), đầu mũi tên là một khối (kết quả đã nén). Đọc được ở cỡ 16px vì chỉ
        còn ba hình: bo góc, các vạch, mũi tên.
      - Vẽ ở cỡ lớn rồi mới thu nhỏ, vì GDI+ khử răng cạnh rất tệ ở cỡ nhỏ.

.EXAMPLE
    .\tools\Generate-AppIcon.ps1
    .\tools\Generate-AppIcon.ps1 -OutputDir 'D:\ noi khac'
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    # Tên biến cố ý khác case với $master bên dưới: PowerShell không phân biệt
    # hoa thường, nên tên gần giống nhau dễ khiến hai biến thành một.
    [int]$RenderSize = 1024
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# $PSScriptRoot chưa có giá trị lúc đánh giá mặc định của param, nên tính ở đây.
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutputDir = Join-Path $here '..\src\UltraCompressor.App'
}

# ------------------------------------------------------------------ bảng màu
$From = [System.Drawing.Color]::FromArgb(255, 0x4C, 0x8D, 0xFF)   # --accent
$To   = [System.Drawing.Color]::FromArgb(255, 0x1E, 0xC4, 0xE0)   # nhấn sáng hơn
$Ink  = [System.Drawing.Color]::White

function New-RoundedPath([System.Drawing.RectangleF]$r, [float]$radius) {
    # Bo goc bang da giac nhieu doan, KHONG dung AddArc + CloseFigure.
    # CloseFigure tao them vung chong lap giua hinh mau va cua duong, nen GDI+
    # khu chung mot cham nho o goc (thay vi mot hinh chu nhat bo dep).
    # Da giac don gian thi luon to dung dung quy tac nao.
    if ($radius -le 0) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddRectangle($r)
        return $path
    }

    $max = [single]([Math]::Min($r.Width, $r.Height) / 2)
    if ($radius -gt $max) { $radius = $max }

    $d = [single]$radius
    $left = [single]$r.X
    $top = [single]$r.Y
    $right = [single]($r.Right)
    $bottom = [single]($r.Bottom)

    # 16 doan cong moi goc 90 do: khi thu nho tu 1024 xuong 16 px van con
    # du cong, khong thay thanh goc.
    $seg = 16
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'

    function Add-ArcPoints($cx, $cy, $a0, $a1) {
        for ($i = 0; $i -le $seg; $i++) {
            $a = ($a0 + (($a1 - $a0) * $i / $seg)) * [Math]::PI / 180.0
            $pts.Add([System.Drawing.PointF]::new(
                [single]($cx + $d * [Math]::Cos($a)),
                [single]($cy + $d * [Math]::Sin($a))))
        }
    }

    # Vong theo chi kim dong ho: goc tren-trai, tren-phai, duoi-phai, duoi-trai.
    # GDI+ lay truc Y theo chi kim dong ho duong.
    Add-ArcPoints ($left + $d)  ($top + $d)    180 270
    Add-ArcPoints ($right - $d) ($top + $d)    270 360
    Add-ArcPoints ($right - $d) ($bottom - $d)   0  90
    Add-ArcPoints ($left + $d)  ($bottom - $d)  90 180

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon([System.Drawing.PointF[]]$pts.ToArray())
    $path.CloseFigure()
    return $path
}

# ------------------------------------------------------------------ vẽ logo chính
function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $s = [single]$size
        $box = [System.Drawing.RectangleF]::new(0, 0, $s, $s)

        # Nền bo góc. Bo 18% thay vì 22%: ở 16px nếu bo quá nhiều sẽ thành hình
        # tròn và mất cảm giác "ô vuông ứng dụng".
        $bg = New-RoundedPath $box ($s * 0.18)
        $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(0, 0),
            [System.Drawing.PointF]::new($s, $s),
            $From, $To)
        $g.FillPath($brush, $bg)
        $brush.Dispose()
        $bg.Dispose()

        $inkBrush = [System.Drawing.SolidBrush]::new($Ink)

        # Mũi tên tụt xuống, vẽ bằng một đa giác gồm thân và đầu.
        # Mọi toạ độ dưới đây là tỉ lệ 0..1 rồi nhân với cạnh, nên tỉ lệ giữ
        # nguyên ở mọi cỡ xuất.
        $cx      = [single]0.5
        $stemW   = [single]0.17
        $headW   = [single]0.60
        $stemTop = [single]0.175
        $shoulder = [single]0.405
        $tip     = [single]0.620
        $pt = @(
            [System.Drawing.PointF]::new($cx - $stemW / 2, $stemTop),
            [System.Drawing.PointF]::new($cx + $stemW / 2, $stemTop),
            [System.Drawing.PointF]::new($cx + $stemW / 2, $shoulder),
            [System.Drawing.PointF]::new($cx + $headW / 2, $shoulder),
            [System.Drawing.PointF]::new($cx, $tip),
            [System.Drawing.PointF]::new($cx - $headW / 2, $shoulder),
            [System.Drawing.PointF]::new($cx - $stemW / 2, $shoulder)
        ) | ForEach-Object {
            [System.Drawing.PointF]::new([single]($_.X * $s), [single]($_.Y * $s))
        }
        $arrow = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $arrow.AddPolygon([System.Drawing.PointF[]]$pt)
        $arrow.CloseFigure()
        $g.FillPath($inkBrush, $arrow)
        $arrow.Dispose()

        # Vạch nền: file sau khi nén còn một vạch gọn thay vì nhiều vạch dữ liệu.
        # Đặt xuống 0.76 và mũi tên kết ở 0.62 để chừa hở ~0.06 cạnh. Ở 16px
        # khoảng hở này mới được 1 px, trước đó vạch dính vào mũi tên.
        $outW = [single]($s * 0.30)
        $outH = [single]($s * 0.075)
        $out = [System.Drawing.RectangleF]::new(
            [single](($s - $outW) / 2), [single]($s * 0.760), $outW, $outH)
        $outPath = New-RoundedPath $out ($outH * 0.42)
        $g.FillPath($inkBrush, $outPath)
        $outPath.Dispose()

        $inkBrush.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

# ------------------------------------------------------------------ xuất kích thước
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$master = New-Logo $RenderSize
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("uc-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$pngs = @{}
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($master, 0, 0, $size, $size)
    }
    finally { $g.Dispose() }

    $png = Join-Path $tmp "icon-$size.png"
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs[$size] = $png
}

# Logo lớn cho giao diện web
$webPng = Join-Path $tmp 'logo-512.png'
$big = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gb = [System.Drawing.Graphics]::FromImage($big)
try {
    $gb.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gb.DrawImage($master, 0, 0, 512, 512)
} finally { $gb.Dispose() }
$big.Save($webPng, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
$master.Dispose()

# ------------------------------------------------------------------ đóng gói ICO
# Định dạng ICO: 6 byte đầu, rồi 16 byte mỗi mục, rồi dữ liệu ảnh. Windows 7 trở
# lên nhận ảnh nén PNG trong ICO nên không cần tự dựng bitmap DIB.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0)          # reserved
$w.Write([uint16]1)          # 1 = icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
$entries = @()
foreach ($size in $sizes) {
    $data = [System.IO.File]::ReadAllBytes($pngs[$size])
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]0)         # bảng màu
    $w.Write([byte]0)         # reserved
    $w.Write([uint16]1)       # planes
    $w.Write([uint16]32)      # bits per pixel
    $w.Write([uint32]$data.Length)
    $w.Write([uint32]$offset)
    $offset += $data.Length
    $entries += , $data
}
foreach ($data in $entries) { $w.Write([byte[]]$data) }
$w.Flush()

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$icoPath = Join-Path $OutputDir 'app.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
$w.Dispose()
$ico.Dispose()

# Favicon cho trang web
$wwwroot = Join-Path ([System.IO.Path]::GetFullPath($OutputDir)) 'wwwroot'
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item $webPng (Join-Path $wwwroot 'logo.png') -Force
Copy-Item $pngs[32] (Join-Path $wwwroot 'favicon.ico') -Force

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ("Da tao {0}" -f $icoPath) -ForegroundColor Green
Write-Host ("  {0:N1} KB, {1} kich thuoc: {2}" -f ((Get-Item $icoPath).Length/1KB), $sizes.Count, ($sizes -join ', '))
Write-Host ("  {0}" -f (Join-Path $wwwroot 'logo.png'))
Write-Host ("  {0}" -f (Join-Path $wwwroot 'favicon.ico'))
