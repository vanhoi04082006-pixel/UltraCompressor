<#
.SYNOPSIS
    Chạy đúng các bước mà CI chạy, trên máy, trước khi commit.

.DESCRIPTION
    Một script duy nhất để "trước khi commit" thay vì nhớ thủ công bốn lệnh. Rất dễ
    quên bước kiểm tra định dạng: nó không làm build hỏng, chỉ làm GitHub Actions đỏ
    lúc đã push xong.

    Chạy `-Fix` để cho phép `dotnet format` tự sửa luôn.

.EXAMPLE
    .\check.ps1
    .\check.ps1 -Fix
#>
[CmdletBinding()]
param([switch]$Fix)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
Push-Location $root

$failed = @()

function Step($name, [scriptblock]$body) {
  Write-Host ''
  Write-Host "== $name ==" -ForegroundColor Cyan
  $global:LASTEXITCODE = 0
  & $body
  if ($LASTEXITCODE -ne 0) {
    Write-Host "   FAIL (exit $LASTEXITCODE)" -ForegroundColor Red
    $script:failed += $name
  }
  else {
    Write-Host '   OK' -ForegroundColor Green
  }
}

if ($Fix) {
  Step 'Sua dinh dang' { dotnet format --verbosity quiet }
}

Step 'Kiem tra dinh dang' { dotnet format --verify-no-changes --verbosity quiet }
Step 'Build Release' { dotnet build UltraCompressor.slnx -c Release --nologo }
Step 'Test' { dotnet test tests\UltraCompressor.Core.Tests\UltraCompressor.Core.Tests.csproj -c Release --no-build --nologo }
# Build phat hanh ha cap canh bao, nen phai kiem tra Debug de bat phan con sot.
Step 'Build Debug (coi canh bao la loi)' { dotnet build UltraCompressor.slnx -c Debug --nologo --no-incremental -warnaserror }

# --no-incremental bat buoc, khong phai cho nhanh.
#
# Build tang di khong chay lai analyzer cho file da bien dich, nen mot loi analyzer
# (vi du CS4014) co the xanh o may nhung do CI build sach lai bat. Da gap dung truong
# hop: local OK, CI fail. Rebuild lai moi la cach tin chuong trinh nay co that khong.

Pop-Location

Write-Host ''
if ($failed.Count -eq 0) {
  Write-Host 'Tat ca deu dung. Du kien commit.' -ForegroundColor Green
  exit 0
}

Write-Host ("FAIL: " + ($failed -join ', ')) -ForegroundColor Red
exit 1
