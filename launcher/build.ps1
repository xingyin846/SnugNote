# 重新编译「贴贴便签」一键启动器为 exe
# 用法：在本目录下执行  powershell -ExecutionPolicy Bypass -File build.ps1
# 说明：使用 Windows 自带的 .NET Framework 编译器，无需联网、无需管理员权限。

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
  $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
  Write-Error "未找到 csc.exe，请确认已安装 .NET Framework 4.x"
  exit 1
}

$out = Join-Path (Split-Path -Parent $PSScriptRoot) "任务便签.exe"
& $csc /nologo /target:exe /codepage:65001 /r:System.dll /out:"$out" (Join-Path $PSScriptRoot "Program.cs")

if ($LASTEXITCODE -eq 0) {
  Write-Host "编译成功：" -NoNewline
  Write-Host $out -ForegroundColor Green
} else {
  Write-Error "编译失败（exit $LASTEXITCODE）"
}
