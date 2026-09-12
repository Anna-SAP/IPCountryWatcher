param([Parameter(Mandatory=$true)][string]$ProjectRoot)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $ProjectRoot
New-Item -ItemType Directory -Force bin, obj | Out-Null
# Build x86/x64 helpers and probe DLLs without relying on a configured developer shell.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++.' }
$vs = & $vswhere -all -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual C++ x86/x64 build tools not found.' }
$vc = Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\windows.h') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows 10/11 SDK not found.' }
foreach ($arch in @('x86', 'x64')) {
    $nativeCompiler = Join-Path $vc.FullName "bin\Hostx64\$arch\cl.exe"
    $objectDir = Join-Path $ProjectRoot "obj\native-$arch"
    New-Item -ItemType Directory -Force -Path $objectDir | Out-Null
    $nativeCommon = @('/nologo', '/O2', '/MT', '/W4', '/WX', '/utf-8', '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0603',
        "/I$($vc.FullName)\include", "/I$($sdk.FullName)\ucrt", "/I$($sdk.FullName)\shared", "/I$($sdk.FullName)\um")
    $nativeLink = @("/LIBPATH:$($vc.FullName)\lib\$arch", "/LIBPATH:$kits\Lib\$($sdk.Name)\ucrt\$arch",
        "/LIBPATH:$kits\Lib\$($sdk.Name)\um\$arch", 'kernel32.lib', 'advapi32.lib', '/DYNAMICBASE', '/NXCOMPAT')
    & $nativeCompiler @nativeCommon /LD native/ProbeDll.cpp "/Fo$objectDir\ProbeDll.obj" "/Febin\IPCountryWatcher.Probe.$arch.dll" /link @nativeLink "/IMPLIB:$objectDir\Probe.lib"
    if ($LASTEXITCODE -ne 0) { throw "Probe DLL compilation failed: $arch" }
    & $nativeCompiler @nativeCommon native/ProbeHost.cpp "/Fo$objectDir\ProbeHost.obj" "/Febin\IPCountryWatcher.ProbeHost.$arch.exe" /link @nativeLink
    if ($LASTEXITCODE -ne 0) { throw "Probe host compilation failed: $arch" }
    if ($Test) {
        & $nativeCompiler @nativeCommon tests/ProbeFixture.cpp "/Fo$objectDir\ProbeFixture.obj" "/Febin\IPCountryWatcher.ProbeFixture.$arch.exe" /link @nativeLink
        if ($LASTEXITCODE -ne 0) { throw "Probe fixture compilation failed: $arch" }
    }
}
