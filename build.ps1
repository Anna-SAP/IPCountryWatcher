param(
    [switch]$Test,
    [switch]$Package,
    [switch]$Installer,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
foreach ($part in $Version.Split('.')) {
    if ([int]$part -gt 65534) { throw 'Version components must be between 0 and 65534.' }
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
New-Item -ItemType Directory -Force bin, obj, test-results | Out-Null
$flags = @(Get-ChildItem -LiteralPath assets\flags -Filter *.png)
if ($flags.Count -lt 240) { throw 'Missing bundled flag assets. Restore assets/flags before building.' }
$common = @('/nologo', '/utf8output', '/optimize+', '/warn:4', '/warnaserror+', '/langversion:5', '/platform:anycpu',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll',
    '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/win32manifest:app.manifest')
$assemblyInfo = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src\AssemblyInfo.cs'))
$assemblyInfo = $assemblyInfo.Replace('AssemblyVersion("1.0.0.0")', ('AssemblyVersion("' + $Version + '.0")'))
$assemblyInfo = $assemblyInfo.Replace('AssemblyFileVersion("1.0.0.0")', ('AssemblyFileVersion("' + $Version + '.0")'))
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'obj\AssemblyInfo.cs'), $assemblyInfo, [Text.UTF8Encoding]::new($true))
$inputs = @(Get-ChildItem src\*.cs | Where-Object { $_.Name -ne 'AssemblyInfo.cs' } | ForEach-Object { '"' + $_.FullName + '"' })
$inputs += '"' + (Join-Path $PSScriptRoot 'obj\AssemblyInfo.cs') + '"'
$inputs += $flags | ForEach-Object { '/resource:"' + $_.FullName + '",Flags.' + $_.Name }
function Invoke-Compile([string[]]$Options, [string]$ResponseFile) {
    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot $ResponseFile), ($Options + $inputs), [Text.UTF8Encoding]::new($true))
    & $compiler ('@' + $ResponseFile)
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $ResponseFile" }
}
Invoke-Compile ($common + @('/target:winexe', '/main:IPCountryWatcher.Program', '/out:bin/IPCountryWatcher.exe')) 'obj/app.rsp'
Copy-Item -LiteralPath app.config -Destination bin\IPCountryWatcher.exe.config -Force
Copy-Item -LiteralPath README.md,THIRD-PARTY-NOTICES.md -Destination bin -Force
Write-Output "Built bin/IPCountryWatcher.exe v$Version with $($flags.Count) embedded flags."
if ($Test) {
    Invoke-Compile ($common + @('/target:exe', '/main:IPCountryWatcher.Tests', '/out:bin/IPCountryWatcher.Tests.exe', 'tests\Tests.cs')) 'obj/tests.rsp'
    & .\bin\IPCountryWatcher.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
$packages = @()
if ($Package -or $Installer) {
    New-Item -ItemType Directory -Force dist | Out-Null
    $portable = 'dist\IPCountryWatcher-portable.zip'
    Compress-Archive -LiteralPath bin\IPCountryWatcher.exe,bin\IPCountryWatcher.exe.config,bin\README.md,bin\THIRD-PARTY-NOTICES.md -DestinationPath $portable -Force
    $packages += $portable
    Write-Output "Packaged $portable"
}
if ($Installer) {
    if (!$InnoCompiler) {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$InnoCompiler) {
            $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($command) { $InnoCompiler = $command.Source }
        }
    }
    if (!$InnoCompiler -or !(Test-Path -LiteralPath $InnoCompiler)) {
        throw 'Inno Setup 6 not found. Install it or pass -InnoCompiler. GitHub windows-2022 includes it.'
    }
    & $InnoCompiler "/DAppVersion=$Version" 'installer\IPCountryWatcher.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $packages += 'dist\IPCountryWatcher-Setup.exe'
}
if ($packages.Count -gt 0) {
    $checksums = foreach ($path in $packages) {
        $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
        $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($path)
    }
    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'dist\SHA256SUMS.txt'), [string[]]$checksums, [Text.Encoding]::ASCII)
}
