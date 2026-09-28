<#
.SYNOPSIS
    Đóng gói và cài UltraCompressor lên máy.

.DESCRIPTION
    Gọi `dotnet publish`, chép các công cụ ngoài vào thư mục chạy, rồi tạo lối tắt
    trên màn hình chính (nếu có thể).

    Công cụ ngoài mặc định lấy từ thư mục `tools` cạnh mã nguồn. Nếu không có, dùng
    -FFmpeg/-Gifsicle/-Ghostscript để chỉ định.

.PARAMETER Source
    Thư mục chứa mã nguồn. Mặc định là thư mục cha của tệp này.

.PARAMETER InstallTo
    Nơi cài. Mặc định %LOCALAPPDATA%\UltraCompressor.

.PARAMETER SelfContained
    Đóng gói kèm runtime .NET (tệp lớn hơn nhiều, nhưng chạy được trên máy chưa cài .NET).

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
  [string]$InstallTo = (Join-Path $env:LOCALAPPDATA 'UltraCompressor'),
  [string]$FFmpeg,
  [string]$Gifsicle,
  [string]$Ghostscript,
  [switch]$SelfContained,
  [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Source)) {
  $Source = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
}
$project = Join-Path $Source 'src\UltraCompressor.App\UltraCompressor.App.csproj'
$staging = Join-Path $Source 'publish'

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

$toolDir = Join-Path $Source 'tools'
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
  $candidate = if ($explicit) { $explicit } else { Join-Path $toolDir $name }

  if ($candidate -and (Test-Path $candidate)) {
    Copy-Item $candidate $target -Force
    Write-Host ('  da chep    {0}' -f $name) -ForegroundColor Green
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
# (chạy thử một phép nhỏ) mỗi khi mở, nên ở đây chỉ bắt lỗi rõ ràng kiểu thiếu tệp.
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

Write-Host '== Cai dat ==' -ForegroundColor Cyan

# Phiên và nhật ký nằm ở %LOCALAPPDATA%\UltraCompressor (tách khỏi thư mục cài) nên
# nâng cấp không làm mất dữ liệu người dùng.
if (Test-Path $InstallTo) {
  Get-ChildItem $InstallTo -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin @('config.json', 'session.json') } |
    Remove-Item -Force -ErrorAction SilentlyContinue
  Copy-Item (Join-Path $staging '*') $InstallTo -Recurse -Force
}
else {
  New-Item -ItemType Directory -Force -Path $InstallTo | Out-Null
  Copy-Item (Join-Path $staging '*') $InstallTo -Recurse -Force
}

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
    $desktop = [Environment]::GetFolderPath('Desktop')

    # Xoá loi tat cu hon neu tro toi tep da khong con ton tai.
    Get-ChildItem $desktop -Filter '*.lnk' -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -like 'UltraCompressor*' } |
      Remove-Item -Force -ErrorAction SilentlyContinue

    if (Test-Path $desktop) {
      $lnk = Join-Path $desktop 'UltraCompressor.lnk'
      $shell = New-Object -ComObject WScript.Shell
      $shortcut = $shell.CreateShortcut($lnk)
      $shortcut.TargetPath = $exe.FullName
      $shortcut.WorkingDirectory = $InstallTo
      $shortcut.Save()
      Write-Host ('  Da tao loi tat: {0}' -f $exe.Name) -ForegroundColor Green
    }
  }
}

$dataDir = Join-Path $env:LOCALAPPDATA 'UltraCompressor'

Write-Host ''
Write-Host 'Xong. Chay UltraCompressor.exe de bat dau.' -ForegroundColor Cyan
Write-Host ('Cau hinh, phien va nhat ky nam o: {0}' -f $dataDir)
