<#
.SYNOPSIS
    Прави ново издание: сглобява, пакетира и — с -Upload — го качва в GitHub.

.DESCRIPTION
    Изданията стоят като GitHub Releases в публичното хранилище, чийто адрес е
    в src\RiosvImports.App\Updates\UpdateFeed.cs. Програмата ги намира оттам, а
    Velopack сваля само разликата спрямо изданието, което човекът вече има.

    Бележките на изданието са разделът със същия номер в CHANGELOG.md. Те се
    показват в програмата, щом изданието бъде намерено, затова се пишат за
    човека, който работи с нея.

    Еднократна подготовка:
        dotnet tool install -g vpk --version 0.0.1298

.PARAMETER Version
    1.3.0 — или 1.4.0-beta.1 за пробно издание. Пробното стига само до тези,
    които са вдигнали „Предлагай и пробните издания“ в „Настройки“.

.PARAMETER Notes
    Файл с бележките. Без него се взема разделът от CHANGELOG.md.

.PARAMETER Upload
    Качва изданието в GitHub и го публикува. Без него то остава в
    artifacts\releases — за проба или за ръчно качване.

.PARAMETER Token
    Ключ за GitHub (fine-grained token) с право „Contents: Read and write“ само
    върху хранилището с изданията. По подразбиране — $env:RELEASES_TOKEN.

.PARAMETER SignParams
    Аргументи към signtool. Без тях изданието е неподписано и Windows
    предупреждава при инсталиране — приемливо само при вътрешно тестване.

.PARAMETER Artifacts
    Къде да стоят сглобката и пакетът. По подразбиране — artifacts до кода.

.EXAMPLE
    .\tools\release.ps1 -Version 1.3.0

.EXAMPLE
    $env:RELEASES_TOKEN = '<ключът>'
    .\tools\release.ps1 -Version 1.3.0 -Upload

.EXAMPLE
    .\tools\release.ps1 -Version 1.3.0 -Upload -SignParams '/fd sha256 /td sha256 /tr <адрес на TSA> /a'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.]*)?$')]
    [string] $Version,

    [string] $Notes,

    [switch] $Upload,

    [string] $Token = $env:RELEASES_TOKEN,

    [string] $SignParams,

    [string] $Artifacts
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $root 'src\RiosvImports.App\RiosvImports.App.csproj'
$feedFile   = Join-Path $root 'src\RiosvImports.App\Updates\UpdateFeed.cs'
$artifacts  = if ($Artifacts) { [System.IO.Path]::GetFullPath($Artifacts) } else { Join-Path $root 'artifacts' }
$publish    = Join-Path $artifacts 'publish'
$releases   = Join-Path $artifacts 'releases'
$notesFile  = Join-Path $artifacts "notes-$Version.md"
$prerelease = $Version.Contains('-')

# ------------------------------------------------------------------ проверки

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw 'Липсва vpk. Инсталирайте го с:  dotnet tool install -g vpk --version 0.0.1298'
}

# vpk и библиотеката Velopack в програмата вървят заедно: при различни номера
# изданието може да не се разпознае от вече инсталираните програми.
$library = ([xml](Get-Content $project -Raw -Encoding UTF8)).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Velopack' } |
    Select-Object -ExpandProperty Version -First 1

$help = (& vpk -x -h 2>&1 | Out-String)
$tool = if ($help -match 'Velopack CLI ([0-9][0-9A-Za-z.\-]*)') { $Matches[1].TrimEnd(',') } else { '' }

if ($library -and $tool -and $tool -ne $library) {
    throw "vpk е $tool, а Velopack в програмата е $library. Сложете същия номер:  dotnet tool update -g vpk --version $library"
}

# Адресът е един — в програмата. Скриптът го чете оттам, за да не се разминат.
$feed = Select-String -Path $feedFile -Pattern 'Repository\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $feed) { throw "Адресът на изданията не е намерен в $feedFile" }
$repository = $feed.Matches[0].Groups[1].Value.TrimEnd('/')

if ($Upload) {
    if (-not $Token) { throw 'За -Upload трябва ключ: $env:RELEASES_TOKEN или -Token.' }
    if ($repository -notlike 'https://github.com/*') { throw "Качването е само към GitHub, а адресът е $repository." }
}

# ------------------------------------------------------------------ бележки

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if ($Notes) {
    Copy-Item -LiteralPath $Notes -Destination $notesFile -Force
}
else {
    $changelog = Join-Path $root 'CHANGELOG.md'
    $lines = @(Get-Content $changelog -Encoding UTF8)
    $heading = '^##\s+\[?' + [regex]::Escape($Version) + '\]?(\s|$)'

    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $heading) { $start = $i; break }
    }

    if ($start -lt 0) {
        # В единични кавички: PowerShell чете „ и “ като двойни и иначе низът свършва по средата.
        throw ('В CHANGELOG.md няма раздел „## ' + $Version + '“. Напишете го: това са бележките, които хората ще видят в програмата.')
    }

    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^##\s') { $end = $i; break }
    }

    $section = if ($end - $start -gt 1) { $lines[($start + 1)..($end - 1)] } else { @() }

    # „### Ново“ става „## Ново“: в програмата заглавието е просто ред.
    $text = (($section | ForEach-Object { $_ -replace '^###\s', '## ' }) -join "`n").Trim()

    if (-not $text) { throw ('Разделът „## ' + $Version + '“ в CHANGELOG.md е празен.') }

    [System.IO.File]::WriteAllText($notesFile, $text + "`n", (New-Object System.Text.UTF8Encoding $false))
}

# ------------------------------------------------------------------ сглобяване

# Чиста папка при всяко издание: остатъци от предишно попадат в пакета.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releases | Out-Null

if ($Upload) {
    # Предишното издание е нужно, за да се направи разликата — без него
    # всяко обновяване е цялата програма, над сто мегабайта.
    Write-Host 'Предишното издание, за разликата ...' -ForegroundColor Cyan

    $download = @('download', 'github', '--repoUrl', $repository, '--token', $Token, '--outputDir', $releases)
    if ($prerelease) { $download += '--pre' }

    & vpk @download
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Предишно издание няма — това излиза цяло, без разлика.' }
}

Write-Host "Публикуване $Version ..." -ForegroundColor Cyan

dotnet publish $project -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Публикуването се провали.' }

Write-Host 'Пакетиране ...' -ForegroundColor Cyan

# --packId е името, под което изданията се разпознават: не се сменя никога.
# --mainExe трябва да съвпада с AssemblyName в csproj.
$pack = @(
    'pack'
    '--packId',       'RiosvImports'
    '--packTitle',    'RIOSV Imports'
    '--packVersion',  $Version
    '--packDir',      $publish
    '--mainExe',      'RIOSV Imports.exe'
    '--releaseNotes', $notesFile
    '--outputDir',    $releases
)

if ($SignParams) { $pack += @('--signParams', $SignParams) }

& vpk @pack
if ($LASTEXITCODE -ne 0) { throw 'Пакетирането се провали.' }

# ------------------------------------------------------------------ качване

if ($Upload) {
    Write-Host 'Качване в GitHub ...' -ForegroundColor Cyan

    # Не „$upload“: PowerShell не различава главни и малки букви, а $Upload е
    # ключът на скрипта — списък в него не влиза и всичко спира.
    $uploadArgs = @(
        'upload', 'github'
        '--repoUrl',     $repository
        '--token',       $Token
        '--outputDir',   $releases
        '--publish'
        '--releaseName', "RIOSV Imports $Version"
        '--tag',         "v$Version"
    )

    if ($prerelease) { $uploadArgs += '--pre' }

    & vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) { throw 'Качването се провали.' }
}

Write-Host ''
Write-Host "Готово: $Version" -ForegroundColor Green
Write-Host "  пакетът:  $releases"
Write-Host "  бележки:  $notesFile"

if ($Upload) {
    Write-Host "  в GitHub: $repository/releases/tag/v$Version"
}
else {
    Write-Host ''
    Write-Host "За качване: `$env:RELEASES_TOKEN = '<ключът>'; .\tools\release.ps1 -Version $Version -Upload" -ForegroundColor Yellow
}
