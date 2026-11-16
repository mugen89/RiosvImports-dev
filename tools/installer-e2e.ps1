<#
.SYNOPSIS
    Проверява инсталатора от край до край — с истинския Velopack.

.DESCRIPTION
    Сглобява програмата, прави два пробни пакета (9.9.1 и 9.9.2) и минава с
    нашия инсталатор (src\RiosvImports.Setup) целия път: ново инсталиране,
    обновяване, гол Setup.exe на Velopack отгоре (както го пуска човек),
    преместване в друга папка, отворена програма, махане. Във всяка стъпка
    проверява, че работата („Data“) е цяла.

    Не пипа нищо истинско:
      • пакетът е под друго име (RiosvImportsE2E) и без преки пътища — записът
        в „Приложения“ и връзките на инсталираната RIOSV Imports остават;
      • профилът е подменен (RIOSV_PROFILE) — и за куките на Velopack, които
        наследяват променливата;
      • истинската програма не се отваря: „отворена програма“ е процес със
        същото име от папката.
    Накрая всичко се маха. В GitHub Actions НЕ се пуска — инсталира наистина.

    Трябват: vpk (същият номер като в release.ps1) и Python (за белега в базата).

.EXAMPLE
    .\tools\installer-e2e.ps1
#>
[CmdletBinding()]
param(
    [string] $Work = (Join-Path $env:TEMP 'riosv-installer-e2e')
)

$ErrorActionPreference = 'Stop'

# Пълен път с „\“: инсталаторът пази папката нормализирана, а сравнението е буквално.
$Work  = [System.IO.Path]::GetFullPath($Work)
$root  = Split-Path -Parent $PSScriptRoot
$key   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RiosvImportsE2E'
$notes = New-Object System.Collections.Generic.List[string]

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw 'Липсва vpk (виж tools\release.ps1).' }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Липсва Python.' }
if (Test-Path $key) { throw "Записът $key вече съществува — спирам, за да не пипна чужда инсталация." }

function Note([string] $name, [bool] $ok, [string] $detail = '') {
    $line = '{0}  {1}{2}' -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $name, $(if ($detail) { "  -> $detail" } else { '' })
    Write-Host $line -ForegroundColor $(if ($ok) { 'Gray' } else { 'Red' })
    $notes.Add($line)
}

# Програмите с прозорец не се чакат от „&“ — затова Start-Process -Wait.
function Run([string] $exe, [string[]] $arguments) {
    (Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden).ExitCode
}

function Marker([string] $db) {
    if (-not (Test-Path -LiteralPath $db)) { return '<няма база>' }
    (& python -I $marker read $db 2>&1 | Out-String).Trim()
}

function Remembered {
    $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
    if ($null -eq $item) { return '<няма запис>' }

    # Velopack пише номера с празни знаци (`0) след него — виж Registration.Clean.
    $version = ($item.DisplayVersion -replace "`0", '').Trim()
    "$version @ $($item.InstallLocation)"
}

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# Белегът в базата: таблица с един ред. Отделен файл — PowerShell 5 чупи
# кавичките в аргументите към външни програми.
$marker = Join-Path $Work 'marker.py'
@'
import sqlite3, sys
mode, db = sys.argv[1], sys.argv[2]
c = sqlite3.connect(db)
if mode == 'write':
    c.execute('create table marker(v text)')
    c.execute('insert into marker values(?)', (sys.argv[3],))
    c.commit()
else:
    print(c.execute('select v from marker').fetchone()[0])
c.close()
'@ | Set-Content -LiteralPath $marker -Encoding ASCII

# ------------------------------------------------------------------ сглобяване

Write-Host 'Сглобяване ...' -ForegroundColor Cyan

dotnet publish "$root\src\RiosvImports.App\RiosvImports.App.csproj" -c Release -r win-x64 --self-contained `
    -p:Version=9.9.1 -o "$Work\publish" --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Програмата не се сглоби.' }

foreach ($v in '9.9.1', '9.9.2') {
    & vpk pack --packId RiosvImportsE2E --packTitle 'RIOSV Imports E2E' --packVersion $v --packDir "$Work\publish" `
        --mainExe 'RIOSV Imports.exe' --outputDir "$Work\rel-$v" --noPortable --delta None --shortcuts None | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Пакетът $v не стана." }
}

dotnet build "$root\src\RiosvImports.Setup\RiosvImports.Setup.csproj" -c Release -o "$Work\front" --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Инсталаторът не се сглоби.' }

$front  = "$Work\front\RiosvImports-win-Setup.exe"
$setup1 = "$Work\rel-9.9.1\RiosvImportsE2E-win-Setup.exe"
$setup2 = "$Work\rel-9.9.2\RiosvImportsE2E-win-Setup.exe"
$a      = "$Work\apps\A"
$b      = "$Work\apps\B"
$prof   = "$Work\profile"
$log    = "$Work\front.log"

$env:RIOSV_PACKID  = 'RiosvImportsE2E'
$env:RIOSV_PROFILE = $prof

try {
    Write-Host 'Проверки ...' -ForegroundColor Cyan

    # 1. Ново инсталиране
    $code = Run $front @('--silent', '--installto', "`"$a`"", '--payload', "`"$setup1`"", '--log', "`"$log`"")
    Note 'ново: изход 0' ($code -eq 0) "$code"
    Note 'ново: програмата е в A' (Test-Path "$a\current\RIOSV Imports.exe")
    Note 'ново: Windows я помни в A' ((Remembered) -eq "9.9.1 @ $a") (Remembered)

    New-Item -ItemType Directory -Force -Path "$a\Data\Backups" | Out-Null
    $mark = 'e2e-' + [guid]::NewGuid().ToString('N')
    & python -I $marker write "$a\Data\workspace.db" $mark
    Set-Content -LiteralPath "$a\Data\Backups\before-clear-x.db" -Value 'копие'

    # 2. Обновяване през нашия инсталатор
    $code = Run $front @('--silent', '--installto', "`"$a`"", '--payload', "`"$setup2`"", '--log', "`"$log`"")
    Note 'обновяване: изход 0' ($code -eq 0) "$code"
    Note 'обновяване: 9.9.2 в A' ((Remembered) -eq "9.9.2 @ $a") (Remembered)
    Note 'обновяване: работата е цяла' ((Marker "$a\Data\workspace.db") -eq $mark) (Marker "$a\Data\workspace.db")
    Note 'обновяване: резервното копие е там' (Test-Path "$a\Data\Backups\before-clear-x.db")
    Note 'обновяване: копие в профила' (@(Get-ChildItem "$prof\Backups" -Directory -Filter 'before-install-*' -ErrorAction SilentlyContinue).Count -ge 1)

    # 3. Гол Setup.exe на Velopack отгоре — както в 1.3.1, когато триеше работата
    $code = Run $setup2 @('--silent', '--installto', "`"$a`"")
    Note 'гол Velopack: изход 0' ($code -eq 0) "$code"
    Note 'гол Velopack: куката запази работата' ((Marker "$a\Data\workspace.db") -eq $mark) (Marker "$a\Data\workspace.db")

    # 4. Преместване в B
    $code = Run $front @('--silent', '--installto', "`"$b`"", '--payload', "`"$setup2`"", '--log', "`"$log`"")
    Note 'преместване: изход 0' ($code -eq 0) "$code"
    Note 'преместване: Windows я помни в B' ((Remembered) -eq "9.9.2 @ $b") (Remembered)
    Note 'преместване: работата е в B' ((Marker "$b\Data\workspace.db") -eq $mark) (Marker "$b\Data\workspace.db")
    Note 'преместване: A е махната изцяло' (-not (Test-Path $a))
    Note 'преместване: копието от махането на A е преименувано' ((-not (Test-Path "$prof\workspace.db")) -and (Test-Path "$prof\workspace.db.moved"))

    # 5. Отворена програма — процес със същото име от папката, не истинската
    New-Item -ItemType Directory -Force -Path "$b\probe" | Out-Null
    Copy-Item "$env:WINDIR\System32\PING.EXE" "$b\probe\RIOSV Imports.exe"
    $busy = Start-Process -FilePath "$b\probe\RIOSV Imports.exe" -ArgumentList @('-n', '60', '127.0.0.1') -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1
    $code = Run $front @('--silent', '--installto', "`"$b`"", '--payload', "`"$setup2`"", '--log', "`"$log`"")
    Stop-Process -Id $busy.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item "$b\probe" -Recurse -Force
    Note 'отворена: изход 3' ($code -eq 3) "$code"
    Note 'отворена: работата е цяла' ((Marker "$b\Data\workspace.db") -eq $mark)

    # 6. Махане
    $code = Run "$b\Update.exe" @('--uninstall', '--silent')
    Start-Sleep -Seconds 3
    Note 'махане: изход 0' ($code -eq 0) "$code"
    Note 'махане: записът е махнат' (-not (Test-Path $key))
    Note 'махане: работата чака в профила' ((Marker "$prof\workspace.db") -eq $mark) (Marker "$prof\workspace.db")
}
finally {
    $env:RIOSV_PACKID = $null
    $env:RIOSV_PROFILE = $null

    foreach ($folder in @($a, $b)) {
        if (Test-Path "$folder\Update.exe") { Run "$folder\Update.exe" @('--uninstall', '--silent') | Out-Null }
    }

    if (Test-Path $key) { Remove-Item $key -Recurse -Force }

    $notes | Set-Content -LiteralPath (Join-Path $Work 'result.txt') -Encoding UTF8
}

$failed = @($notes | Where-Object { $_.StartsWith('FAIL') }).Count

Write-Host ''
Write-Host "Проверки: $($notes.Count), неуспешни: $failed (дневник: $log)" -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })

if ($failed -eq 0) { Remove-Item "$Work\apps", "$Work\publish", "$Work\rel-9.9.1", "$Work\rel-9.9.2" -Recurse -Force -ErrorAction SilentlyContinue }

exit $failed
