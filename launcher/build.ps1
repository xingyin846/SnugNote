# 重新编译「贴贴便签」一键启动器为 exe
# 用法：在本目录下执行  powershell -ExecutionPolicy Bypass -File build.ps1
#       （PowerShell 5.1 与 PowerShell 7 均可）
# 说明：使用 Windows 自带的 .NET Framework 编译器，无需联网、无需管理员权限。
#
# 注意（踩过的坑）：exe 文件名含中文。若直接把中文字面量传给 csc.exe，
# 在 PowerShell 7 下参数会按系统 ANSI(GBK) 编码传递，产物文件名会变成乱码。
# 因此这里用 [char] 码点拼出文件名 —— 任何 PowerShell 版本、任何代码页下都稳定。

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
  $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
  Write-Error "未找到 csc.exe，请确认已安装 .NET Framework 4.x"
  exit 1
}

# "贴贴便签.exe"：贴=8D34 便=4FBF 签=7B7E
$exeName = ([char]0x8D34 + [char]0x8D34 + [char]0x4FBF + [char]0x7B7E + ".exe")
$out = Join-Path (Split-Path -Parent $PSScriptRoot) $exeName
$src = Join-Path $PSScriptRoot "Program.cs"
$ico = Join-Path $PSScriptRoot "app.ico"
$iconArg = @()
if (Test-Path -LiteralPath $ico) { $iconArg = @("/win32icon:$ico") }

& $csc /nologo /target:exe /codepage:65001 /r:System.dll @iconArg /out:"$out" "$src"

if ($LASTEXITCODE -eq 0) {
  Write-Host "编译成功：" -NoNewline
  Write-Host $out -ForegroundColor Green
} else {
  Write-Error "编译失败（exit $LASTEXITCODE）"
  exit 1
}
