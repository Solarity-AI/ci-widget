# Checks the build and the exe's own install and uninstall steps against scratch folders, without touching the
# real Startup folder or the real config. Run: powershell -File windows\tests\scripts-test.ps1
$fails = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fails++ } }
$win = Split-Path $PSScriptRoot
$root = Split-Path $win
# The build works from folders with brackets, spaces, parentheses and &, like a GitHub zip unpacked twice.
foreach ($name in @("ciwidget-[1]-$PID", "ci widget-main (1) & co-$PID", "ciwidget&co-$PID")) {
    $box = Join-Path ([IO.Path]::GetTempPath()) $name
    [IO.Directory]::CreateDirectory((Join-Path $box 'src')) | Out-Null
    foreach ($f in @('build.cmd', 'build.ps1', 'app.manifest') + @(Get-ChildItem -LiteralPath (Join-Path $win 'src') -Filter *.cs | ForEach-Object { "src\$($_.Name)" })) {
        [IO.File]::Copy((Join-Path $win $f), (Join-Path $box $f), $true)
    }
    $exe = Join-Path $box 'bin\CIWidget.exe'
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $box 'build.ps1') -Out $exe 2>&1 | Out-String
    Check "build: folder '$name' builds" (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $exe))
    Check "build: folder '$name' prints no cmd errors" ($log -notmatch 'cannot find|not recognized|syntax is incorrect')
    if ($name -like 'ci widget*' -and (Test-Path -LiteralPath $exe)) {
        $startup = Join-Path $box 'Startup'
        $lnk = Join-Path $startup 'CI Widget.lnk'
        $cfgHome = Join-Path $box 'home'
        $env:CIWIDGET_HOME = $cfgHome
        Set-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') '99 passed, 0 failed'
        $p = Start-Process -FilePath $exe -ArgumentList '--install', '--startup', "`"$startup`"", '--no-launch' -Wait -PassThru
        Check 'install: exit code 0' ($p.ExitCode -eq 0)
        $last = Get-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') -Tail 1
        Check 'install: a fresh self-test report replaced the old one' (($last -match '^\d+ passed, 0 failed$') -and ($last -ne '99 passed, 0 failed'))
        Check 'install: Startup shortcut written to the scratch folder' (Test-Path -LiteralPath $lnk)
        Check 'install: example config written to CIWIDGET_HOME' (Test-Path -LiteralPath (Join-Path $cfgHome 'config.example.json'))
        if (Test-Path -LiteralPath $lnk) {
            $target = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath
            Check 'install: shortcut points at the exe' ($target -eq $exe)
        }
        $p = Start-Process -FilePath $exe -ArgumentList '--uninstall', '--startup', "`"$startup`"" -Wait -PassThru
        Check 'uninstall: exit code 0' ($p.ExitCode -eq 0)
        Check 'uninstall: shortcut removed' (-not (Test-Path -LiteralPath $lnk))
        Check 'uninstall: config folder kept' (Test-Path -LiteralPath (Join-Path $cfgHome 'config.example.json'))
        Remove-Item Env:CIWIDGET_HOME
    }
    Remove-Item -LiteralPath $box -Recurse -Force -ErrorAction SilentlyContinue
}
# The root installers: right names, no leftovers from the widget they were adapted from.
$inst = [IO.File]::ReadAllText((Join-Path $root 'Install CI Widget (Windows).cmd'))
$unin = [IO.File]::ReadAllText((Join-Path $root 'Uninstall CI Widget (Windows).cmd'))
Check 'installer: builds CIWidget.exe into Programs\CI Widget' (($inst -match 'Programs\\CI Widget') -and ($inst -match 'CIWidget\.exe'))
Check 'installer: has the done marker for the sandbox rerun' ($inst -match 'CIW-INSTALL-DONE')
Check 'installers: no leftover names' (($inst + $unin) -notmatch 'HW ?Widget|HWW_|cpu-temp')
Check 'installers: CRLF line endings' (($inst -match "`r`n") -and ($unin -match "`r`n") -and ($inst -notmatch "[^`r]`n") -and ($unin -notmatch "[^`r]`n"))
Check 'uninstaller: keeps the config folder' ($unin -notmatch 'rmdir[^\r\n]*ci-widget')
Write-Host ""
if ($fails -eq 0) { Write-Host "scripts-test: all passed" } else { Write-Host "scripts-test: $fails failed" }
exit $fails
