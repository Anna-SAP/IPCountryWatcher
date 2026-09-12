# Exercise packaging on the disposable GitHub runner. Never launches the network application.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Run this install/uninstall test only on a disposable GitHub Actions runner.'
}
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$target = Join-Path $env:RUNNER_TEMP 'IPCountryWatcher-InstallTest'
$log = Join-Path $PWD 'test-results\installer.log'
$setup = Join-Path $PWD 'dist\IPCountryWatcher-Setup.exe'
$result = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$target`"", "/LOG=`"$log`"") -WindowStyle Hidden -Wait -PassThru
if ($result.ExitCode -ne 0) { throw "Installer failed: $($result.ExitCode)" }
$expected = @('IPCountryWatcher.exe', 'IPCountryWatcher.exe.config', 'README.md', 'THIRD-PARTY-NOTICES.md', 'IPCountryWatcher.Probe.x64.dll', 'IPCountryWatcher.Probe.x86.dll', 'IPCountryWatcher.ProbeHost.x64.exe', 'IPCountryWatcher.ProbeHost.x86.exe')
foreach ($name in $expected) {
    $installed = Join-Path $target $name
    if (!(Test-Path -LiteralPath $installed)) { throw "Missing installed file: $name" }
    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path 'bin' $name)).Hash) {
        throw "Installed file differs: $name"
    }
}
if (Get-Process IPCountryWatcher -ErrorAction SilentlyContinue) {
    throw 'Silent installation must not launch the network application.'
}
$uninstall = Join-Path $target 'unins000.exe'
$result = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -WindowStyle Hidden -Wait -PassThru
if ($result.ExitCode -ne 0) { throw "Uninstaller failed: $($result.ExitCode)" }
if (Test-Path -LiteralPath (Join-Path $target 'IPCountryWatcher.exe')) {
    throw 'Uninstaller left the application executable behind.'
}
'PASS silent install, installed payload hashes, no automatic launch, and uninstall' |
    Set-Content -LiteralPath test-results\installer-result.txt
