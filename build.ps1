# Build script: icon + compile via system csc. ASCII only.
# Chinese exe name is composed via char codes (fan qie zhong).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path app.ico)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\make_icon.ps1"
}

$cscs = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscs | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'not found csc.exe (.NET Framework 4.x)' }

$out = Join-Path $PSScriptRoot 'TomatoPomo.exe'
if (Test-Path $out) { Remove-Item $out -Force }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /out:"$out" /win32icon:app.ico /win32manifest:app.manifest /r:System.Web.Extensions.dll Program.cs

if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

# fan=U+756A qie=U+8304 zhong=U+949F  ->  fan-qie-zhong.exe
$cn = ([string][char]0x756A) + [char]0x8304 + [char]0x949F
$cnOut = Join-Path $PSScriptRoot ($cn + '.exe')
Copy-Item $out $cnOut -Force

$kb = [int]((Get-Item $out).Length / 1KB)
Write-Host ("BUILD OK -> TomatoPomo.exe ({0} KB) + {1}.exe" -f $kb, $cn)
