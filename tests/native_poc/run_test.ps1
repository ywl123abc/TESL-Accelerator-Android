$ErrorActionPreference = "Stop"

$gccPath = "C:\Users\dell\tools\w64devkit\bin\gcc.exe"
if (-not (Test-Path $gccPath)) {
    $gccCmd = Get-Command "gcc" -ErrorAction SilentlyContinue
    if ($gccCmd) { $gccPath = $gccCmd.Source }
}

Write-Host ">>> 使用编译器: $gccPath" -ForegroundColor Cyan

$srcDir = "D:\TESL\tesl-double\src\native\cfnat\src"
$includeDir = "D:\TESL\tesl-double\src\native\cfnat\include"
$testSrc = "D:\TESL\tesl-double\tests\native_poc\test_dual_instance.c"
$outExe = "D:\TESL\tesl-double\tests\native_poc\test_dual_instance.exe"

if (Test-Path $outExe) { Remove-Item $outExe -Force }

# 编译动态库及测试执行文件
Write-Host ">>> 正在编译 test_dual_instance.exe..." -ForegroundColor Yellow
& $gccPath -I$includeDir -I$srcDir `
    "$srcDir\cfnat_tls.c" `
    "$srcDir\cfnat_registry.c" `
    "$srcDir\cfnat_api.c" `
    $testSrc `
    -o $outExe `
    -lws2_32 -lwinpthread -lbcrypt -O2

if (-not (Test-Path $outExe)) {
    throw "编译失败，未生成 $outExe"
}

Write-Host ">>> 编译成功！开始执行测试用例..." -ForegroundColor Green
Set-Location "D:\TESL\tesl-double\tests\native_poc"
& $outExe
$testExitCode = $LASTEXITCODE

if ($testExitCode -ne 0) {
    throw "测试失败，退出代码: $testExitCode"
}

Write-Host "`n>>> [SUCCESS] 全部测试通过！" -ForegroundColor Green
