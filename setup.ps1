<#
.SYNOPSIS
    Đóng gói và cài UltraCompressor vào ngay trong thư mục dự án.

.DESCRIPTION
    Gọi `dotnet publish`, chép công cụ ngoài vào thư mục chạy, rồi tạo lối tắt trên
    màn hình chính (nếu có thể).

    Mặc định cài vào `<dự án>\app`. Cấu hình, phiên, nhật ký, tệp tạm và dữ liệu
    trình duyệt nằm ở `<dự án>\data`. KHÔNG còn ghi gì ra `%LOCALAPPDATA%` — bản cũ
    cài vào đó và trùng thư mục với nơi lưu dữ liệu, nên ổ C: phình lên 130 MB mà
    không ai biết.

    Nếu phát hiện thư mục cài cũ ở `%LOCALAPPDATA%\UltraCompressor`, script sẽ hỏi
    trước khi xoá (xoá được thì giải phóng ~130 MB).

.PARAMETER Source
    Thư mục chứa mã nguồn. Mặc định là thư mục cha của tệp này.

.PARAMETER InstallTo
    Nơi cài. Mặc định `<Source>\app`.

.PARAMETER SelfContained
    Đóng gói kèm runtime .NET (tệp lớn hơn nhiều, nhưng chạy được trên máy chưa cài .NET).

.PARAMETER RemoveLegacy
    Xoá thư mục cài cũ ở %LOCALAPPDATA% mà không hỏi.

.EXAMPLE
    .\setup.ps1
    .\setup.ps1 -FFmpeg 'D:\tools\ffmpeg.exe'
#>
[CmdletBinding()]
param(
  # Không đặt mặc định là $PSScriptRoot ở đây: khi chạy bằng
  # `powershell -File setup.ps1` thì $PSScriptRoot còn rỗng lúc đánh giá mặc định
  # nên Join-Path bên dưới báo "empty string". Tính sau ở dòng dưới.
  [string]$Source,
  [string]$InstallTo,
  [string]$FFmpeg,
  [string]$Gifsicle,
  [string]$Ghostscript,
  [switch]$SelfContained,
  [switch]$NoShortcut,
  [switch]$RemoveLegacy
)

$ErrorActionPreference = 'Stop'

# KHONG dung [string]::IsNullOrWhiteSpace o day. Ham do chi co tu .NET Core 2.0 tro len,
# con powershell.exe (Windows PowerShell 5.1) chay tren .NET Framework nen goi toi se
# vang loi "does not contain a method named 'NullOrWhiteSpace'".
function Test-Blank([string]$value) { [string]::IsNullOrEmpty($value) -or $value.Trim().Length -eq 0 }

if (Test-Blank $Source) {
  $Source = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
}

$project  = Join-Path $Source 'src\UltraCompressor.App\UltraCompressor.App.csproj'
$staging  = Join-Path $Source 'publish'
$dataDir  = Join-Path $Source 'data'
$legacyDir = Join-Path $env:LOCALAPPDATA 'UltraCompressor'

if (Test-Blank $InstallTo) {
  $InstallTo = Join-Path $Source 'app'
}

if (-not (Test-Path $project)) {
  throw ("Khong thay tep du an: {0}" -f $project)
}

Write-Host '== Dong goi (dotnet publish) ==' -ForegroundColor Cyan

$config = if ($SelfContained) { 'Release-SelfContained' } else { 'Release' }

& dotnet publish $project `
  -c $config `
  -o $staging `
  --nologo `
  -v minimal
if ($LASTEXITCODE -ne 0) { throw ("dotnet publish that bai (ma {0})" -f $LASTEXITCODE) }

Write-Host '== Chep cong cu ngoai ==' -ForegroundColor Cyan

# Chép từ thư mục tools của dự án, rồi tới thư mục publish, rồi tới bản đã cài — theo
# thứ tự đó để lần cài lại không vô tình dùng bản cũ.
$toolSources = @(
  (Join-Path $Source 'tools'),
  $staging,
  $InstallTo
) | Where-Object { $_ -and (Test-Path $_) }

$tools = @{
  'ffmpeg.exe'    = $FFmpeg
  'gifsicle.exe'  = $Gifsicle
  'gswin64c.exe'  = $Ghostscript
}

foreach ($name in $tools.Keys) {
  $target = Join-Path $staging $name
  if (Test-Path $target) {
    Write-Host "  đã có      $name"
    continue
  }

  $explicit = $tools[$name]
  $found = $null
  if ($explicit) { $found = $explicit }
  else { $found = $toolSources | ForEach-Object { Join-Path $_ $name } | Where-Object { Test-Path $_ } | Select-Object -First 1 }

  if ($found) {
    Copy-Item $found $target -Force
    Write-Host ('  da chep    {0}  <- {1}' -f $name, $found) -ForegroundColor Green
  }
  else {
    $hint = switch ($name) {
      'gswin64c.exe' {
        # Tệp gswin64c.exe đi kèm bản đóng gói cũ là stub thiếu DLL, chạy không được.
        # Cần bộ cài đầy đủ.
        ' — cai Ghostscript day du tu https://ghostscript.com/releases/ roi chay lai setup.ps1'
      }
      default { ' — ung dung se bao loi khi can toi loai media do' }
    }
    Write-Host ('  THIEU      {0}{1}' -f $name, $hint) -ForegroundColor Yellow
  }
}

Write-Host '== Kiem tra kha nang chay tung cong cu ==' -ForegroundColor Cyan

# Chỉ kiểm tra phiên bản, không chạy công việc thật. Ứng dụng tự kiểm tra kỹ hơn
# (chạy thử một phép nhỏ) mỗi lần mở, nên ở đây chỉ bắt lỗi rõ ràng kiểu thiếu tệp.
$probes = @{
  'ffmpeg.exe'   = @('-version')
  'gifsicle.exe' = @('--version')
  'gswin64c.exe' = @('-version')
}

foreach ($name in $probes.Keys) {
  $path = Join-Path $staging $name
  if (-not (Test-Path $path)) { continue }

  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $path
  $psi.Arguments = $probes[$name] -join ' '
  $psi.RedirectStandardError = $true
  $psi.RedirectStandardOutput = $true
  $psi.UseShellExecute = $false

  $proc = [System.Diagnostics.Process]::Start($psi)
  $out = $proc.StandardOutput.ReadToEnd()
  $err = $proc.StandardError.ReadToEnd()
  $proc.WaitForExit(15000) | Out-Null

  $version = ($out -split "`n" | Select-Object -First 1).Trim()
  if ($version) {
    Write-Host ('  OK         {0} - {1}' -f $name, $version) -ForegroundColor Green
  }
  else {
    $detail = ($err -split "`n" | Select-Object -First 1).Trim()
    if ($detail) {
      Write-Host ('  HONG       {0} - {1}' -f $name, $detail) -ForegroundColor Yellow
    }
    else {
      Write-Host ('  KHONG DOC DUOC  {0}' -f $name) -ForegroundColor Yellow
    }
  }
}

Write-Host '== Cai dat vao thu muc du an ==' -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $InstallTo | Out-Null
Copy-Item (Join-Path $staging '*') $InstallTo -Recurse -Force

Write-Host ('  Da cai vao {0}' -f $InstallTo) -ForegroundColor Green

if (-not $NoShortcut) {
  # Tìm tệp thực thi thật trong thư mục cài, đừng đoán tên: nếu AssemblyName đổi thì
  # lối tắt sẽ trỏ tới tệp không tồn tại và Windows báo "Missing Shortcut".
  $exe = Get-ChildItem $InstallTo -Filter '*.exe' -File |
    Where-Object { $_.Name -notmatch '^(ffmpeg|ffplay|ffprobe|gifsicle|gswin64c)' } |
    Sort-Object Length -Descending |
    Select-Object -First 1

  if (-not $exe) {
    Write-Warning 'Khong tim thay tep thuc thi de tao loi tat.'
  }
  else {
    $icon = Join-Path $InstallTo 'app.ico'
    $hasIcon = Test-Path $icon

    # Hàm tạo lối tắt dùng chung cho cả Desktop và Start Menu, để hai chỗ không lệch nhau
    # về icon hoặc thư mục làm việc.
    function New-UcShortcut([string]$path) {
      $sh = New-Object -ComObject WScript.Shell
      $sc = $sh.CreateShortcut($path)
      $sc.TargetPath = $exe.FullName
      $sc.WorkingDirectory = $InstallTo
      $sc.Description = 'Nen media hang loat theo muc tieu chinh'

      # Chỉ gán IconLocation khi file thật sự tồn tại. Gán một đường dẫn không có thì
      # Windows hiện biểu tượng trắng trống — tệ hơn là bỏ trống để nó lấy từ chính tệp
      # thực thi, vốn đã nhúng sẵn icon.
      if ($hasIcon) {
        $sc.IconLocation = "$icon,0"
      }

      $sc.Save()
    }

    $desktop = [Environment]::GetFolderPath('Desktop')

    if (Test-Path $desktop) {
      $lnk = Join-Path $desktop 'UltraCompressor.lnk'
      New-UcShortcut $lnk
      Write-Host ('  Da tao loi tat: {0}' -f $exe.Name) -ForegroundColor Green
    }

    # Start Menu: tạo trong thư mục Programs của người dùng, không cần quyền admin và
    # không đụng tới shortcut dùng chung cho mọi máy trong hệ thống.
    $programs = [Environment]::GetFolderPath('Programs')

    if (Test-Path $programs) {
      $menuDir = Join-Path $programs 'UltraCompressor'

      if (-not (Test-Path $menuDir)) {
        New-Item -ItemType Directory -Path $menuDir -Force | Out-Null
      }

      New-UcShortcut (Join-Path $menuDir 'UltraCompressor.lnk')
      Write-Host '  Da tao loi tat trong Start Menu.' -ForegroundColor Green
    }
  }
}

Write-Host '== Don thu muc cai cu o o dia C: ==' -ForegroundColor Cyan

# Bản cài cũ nằm ở %LOCALAPPDATA%\UltraCompressor, trùng đúng chỗ lưu dữ liệu nên nó
# phình lên ~130 MB (ffmpeg.exe 95 MB + profile WebView2 32 MB). Dữ liệu người dùng
# cũ nằm trong đó nên KHÔNG xoá khi chưa hỏi.
$doRemove = $RemoveLegacy
if (-not $doRemove -and (Test-Path $legacyDir)) {
  $answer = Read-Host ("Tim thay ban cai cu tai {0}. Xoa de giai phong dung luong? [y/N]" -f $legacyDir)
  $doRemove = ($answer -eq 'y' -or $answer -eq 'Y')
}

if ((Test-Path $legacyDir) -and $doRemove) {
  # Chỉ xoá khi nó KHÔNG phải nơi cài hiện tại — chống xoá nhầm chính thư mục đang chạy.
  $legacyFull = (Resolve-Path $legacyDir).Path
  $installFull = (Resolve-Path $InstallTo).Path
  if ($legacyFull -ne $installFull) {
    Remove-Item $legacyFull -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $legacyFull) {
      Write-Host ('  KHONG xoa duoc {0} — thu muc dang bi mo khoa.' -f $legacyFull) -ForegroundColor Yellow
    }
    else {
      Write-Host ('  Da xoa {0}' -f $legacyFull) -ForegroundColor Green
    }
  }
  else {
    Write-Host '  Bo qua: thu muc cai cu trung thu muc cai moi.' -ForegroundColor Yellow
  }
}
elseif (Test-Path $legacyDir) {
  $bytes = (Get-ChildItem $legacyDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum
  $mb = [math]::Round($bytes / 1MB, 1)
  Write-Host ('  Giu lai {0} ({1} MB). Chay lai voi -RemoveLegacy de xoa.' -f $legacyDir, $mb) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Xong. Chay app\UltraCompressor.exe de bat dau.' -ForegroundColor Cyan
Write-Host ('Cau hinh, phien, nhat ky nam o: {0}' -f $dataDir)
