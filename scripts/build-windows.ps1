<#
.SYNOPSIS
    Build GPMPay Webhook Local cho Windows bằng Native AOT (1 file .exe chạy ngay, không cần cài .NET).

.DESCRIPTION
    Yêu cầu: .NET SDK 10 + Visual Studio 2022/2026 (hoặc Build Tools) có workload
    "Desktop development with C++" (Native AOT cần trình liên kết MSVC).

    Kết quả:
      artifacts\win-x64\GpmPayWebhookLocal.exe        (thư mục chạy ngay)
      artifacts\GpmPayWebhookLocal-win-x64.zip        (gói để gửi đi)

.EXAMPLE
    .\scripts\build-windows.ps1
    .\scripts\build-windows.ps1 -Arch arm64
    .\scripts\build-windows.ps1 -Arch all
#>
param(
    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Arch = 'x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'GpmPayWebhookLocal\GpmPayWebhookLocal.csproj'
$artifacts = Join-Path $root 'artifacts'
$archs = if ($Arch -eq 'all') { @('x64', 'arm64') } else { @($Arch) }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Không tìm thấy dotnet. Cài .NET SDK 10: https://dotnet.microsoft.com/download'
}

# Native AOT dò trình liên kết MSVC qua vswhere.exe — thư mục VS Installer thường không có sẵn trong PATH
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path (Join-Path $vsInstaller 'vswhere.exe')) -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $env:PATH = "$vsInstaller;$env:PATH"
}
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    Write-Warning 'Không thấy Visual Studio Installer (vswhere.exe). Native AOT cần Visual Studio / Build Tools có workload "Desktop development with C++".'
}

foreach ($a in $archs) {
    $rid = "win-$a"
    $out = Join-Path $artifacts $rid
    Write-Host "==> Publish Native AOT $rid ($Configuration)" -ForegroundColor Cyan

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $project -c $Configuration -r $rid -o $out -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) { throw "Build $rid thất bại (mã $LASTEXITCODE)." }

    # Native AOT không cần file .pdb / .xml đi kèm
    Get-ChildItem $out -Include *.pdb, *.dbg, *.xml -Recurse | Remove-Item -Force

    $zip = Join-Path $artifacts "GpmPayWebhookLocal-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip

    $exe = Get-Item (Join-Path $out 'GpmPayWebhookLocal.exe')
    Write-Host ("    {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
    Write-Host "    $zip" -ForegroundColor Green
}
