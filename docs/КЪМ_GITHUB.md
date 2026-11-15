# Към GitHub

Как кодът отива в GitHub, как оттам излизат изданията и как програмата ги
намира. Историята на проекта започва с първия коммит и не се пренаписва лесно —
затова тук стои и какво влиза, и какво не.

**Състояние към 15.11.2026: едно публично хранилище.** Докато програмата се
разработва, кодът, `news.json` (в корена) и изданията са в
`mugen89/RiosvImports-dev`, което е публично. Първото хранилище,
`mugen89/RiosvImports`, вече не се ползва. Изданията качва вграденият
`GITHUB_TOKEN` — тайна `RELEASES_TOKEN` не трябва. Разделянето на две
хранилища по-долу остава планът за истинското издаване: тогава
`UpdateFeed.Repository` пак сочи `RiosvImports-releases`, а `release.yml` пак
взима `secrets.RELEASES_TOKEN`.

---

## Две хранилища

| Хранилище | Видимост | Какво има |
|---|---|---|
| `mugen89/RiosvImports` | **частно** | кодът, тестовете, документацията |
| `mugen89/RiosvImports-releases` | **публично** | README, `news.json` и изданията под „Releases“ |

**Защо две.** Програмата пита GitHub при всяко пускане — без ключ. Ключ, вграден
в програмата, би отворил и кода: в GitHub правото „чети съдържанието“ не
различава изданията от изходния код. Затова изданията стоят отделно и публично,
а кодът остава частен — схемите на НИСО са публични, но начинът, по който се
превръщат в шаблони и XML, е самата услуга.

Адресът на изданията е един ред: `UpdateFeed.Repository` в
`src/RiosvImports.App/Updates/UpdateFeed.cs`. Оттам го чете и
`tools\release.ps1`, за да не се разминат.

---

## 1. Чистата папка

```powershell
.\tools\export-clean.ps1
```

Копира **само позволеното** в `..\GitHub\RiosvImports` (до работната папка) и
при първото пускане прави `..\GitHub\RiosvImports-releases` от
`tools\releases-repo`. Списък на позволеното, а не на забраненото: нов шум в
работната папка не може да се промъкне сам.

| Влиза | Не влиза |
|---|---|
| `src`, `tests`, `tools` — само код и проекти | `bin`, `obj`, `.vs`, `*.user` |
| `schemas/*.xsd` | `dist/` (153 MB пакети за тестващите), `artifacts/` |
| `docs/*.md`, `docs/*.txt` | `docs/Инструкции_за_тестване.docx` — двоичен близнак на `.txt` |
| `README.md`, `CHANGELOG.md`, `.gitignore`, `.gitattributes`, `Directory.Build.props`, `.github/` | `README.txt` — по-стар дубликат на `README.md` |
| | `MainForm`, `DropZone`, `ResultsView`, `ReportPicker` — мъртвият интерфейс |
| | бази, Excel файлове, пробни данни |

Мъртвият интерфейс и `README.txt` още стоят в работната папка: махането им там
чака изрично „да“. Нищо не ги вика — сглобката и тестовете минават и с тях, и
без тях.

Изнасянето може да се повтаря: старото копие се маха, `.git` остава. Пипа се
само папка, която е празна или вече е изнесено копие (има `RiosvImports.sln`).

**Работата продължава в чистата папка.** Работната папка остава архив с
пробите, пакетите за тестващите и истинския износ от „Деметра“ — тестовете
намират износа, като търсят нагоре по папките, затова
`D:\01_Organized\NISO\GitHub\RiosvImports` го вижда точно както досега.

---

## 2. Тайни

Проверено на 01.09.2026 и отново на 14.11.2026:

- Няма `.pem`, `.pfx`, `.key`, `.p12`, `.env` в дървото.
- Няма частен ключ в кода. `AccountOptions.EntitlementPublicKeyPem` е
  **публичен** ключ и мястото му е в програмата.
- Адресите на услугата са `ПОПЪЛНИ` — виж `AccountOptions.cs`.
  `AccountOptions.IsConfigured` нарочно проверява за тази дума.
- Ключът, който **не** влиза никога, е `ENTITLEMENT_SIGNING_KEY` — той живее
  само в услугата. Същото важи за ключа за изданията (`RELEASES_TOKEN`).

`export-clean.ps1` отказва копие, в което има файл с такова разширение или ред
`BEGIN … PRIVATE KEY`. Преди пуша — още веднъж:

```powershell
git ls-files | Select-String -Pattern '\.(pem|pfx|key|p12|env)$'
```

Трябва да не върне нищо.

---

## 3. Първият коммит

```powershell
cd "D:\01_Organized\NISO\GitHub\RiosvImports"
git add -A
git status              # чете се РЕД ПО РЕД, преди коммита
git commit -m "Първоначален внос: програмата, ядрото, тестовете и документацията"
```

С [GitHub CLI](https://cli.github.com/):

```powershell
gh repo create mugen89/RiosvImports --private --source=. --remote=origin --push
```

Без него: празно **частно** хранилище през сайта (без README и без `.gitignore`
— те вече са тук), после

```powershell
git remote add origin https://github.com/mugen89/RiosvImports.git
git push -u origin main
```

---

## 4. Хранилището с изданията

```powershell
cd "D:\01_Organized\NISO\GitHub\RiosvImports-releases"
git init -b main
git add -A
git commit -m "README и първата новина"
gh repo create mugen89/RiosvImports-releases --public --source=. --remote=origin --push
```

Оттам нататък `news.json` се пише направо в GitHub — молив, „Commit changes“ —
и стига до всички при следващото им пускане. Как се пише:
`tools/releases-repo/README.md`.

---

## 5. Ключът за изданията

Изданията се качват с ключ, който може да пише **само** в хранилището с
изданията:

GitHub → Settings → Developer settings → Fine-grained tokens → Generate:

- Repository access: **Only select repositories** → `RiosvImports-releases`
- Permissions → Repository → **Contents: Read and write**

Пази се на две места и никъде другаде:

- на машината, от която се правят изданията: `$env:RELEASES_TOKEN`;
- в частното хранилище: Settings → Secrets and variables → Actions →
  `RELEASES_TOKEN` — за работния поток `release`.

---

## 6. Издание

```powershell
dotnet tool install -g vpk --version 0.0.1298     # веднъж
.\tools\release.ps1 -Version 1.3.0 -Upload
```

или

```powershell
git tag v1.3.0
git push origin v1.3.0                             # работният поток „release“ прави същото
```

Скриптът сглобява, взема раздела `## 1.3.0` от `CHANGELOG.md` за бележки,
пакетира с Velopack и качва в `RiosvImports-releases` като Release `v1.3.0` с
`RiosvImports-win-Setup.exe`, `RiosvImports-win-Portable.zip` и пакета за
обновяване. Без `-Upload` пакетът остава в `artifacts\releases` — за проба.

Номерът на `vpk` следва пакета `Velopack` в `RiosvImports.App.csproj`;
скриптът спира, ако се разминават.

Пробно издание: `-Version 1.4.0-beta.1` (етикет `v1.4.0-beta.1`). Стига само до
програмите с вдигнато „Предлагай и пробните издания“ в „Настройки“.

**Тестващите с 1.2.0** (самостоятелният exe от `dist`) инсталират веднъж от
`RiosvImports-win-Setup.exe`. Оттам нататък програмата се обновява сама.
Базата им в `%LOCALAPPDATA%\RIOSV Imports` е същата и остава.

---

## 7. Как програмата намира новото

| Кога | Какво |
|---|---|
| При пускане (ако е вдигнато „Проверявай…“) | Пита за издание и тегли `news.json`. Нищо не се сваля. |
| Има по-ново | Известие на началния екран с бележките, „● Ново издание“ най-долу в лентата, едно в брояча на „Начало“. |
| „Обнови сега“ | Сваля само разликата; програмата работи през това време. |
| „Рестартирай и обнови“ | Програмата се затваря, базата се затваря, инсталаторът сменя файловете и я отваря наново. |
| Първото пускане на новото | „Обновено до …“ с бележките на изданието. |

Velopack по подразбиране слага всяко свалено издание при следващото пускане,
без да пита. Това е изключено (`SetAutoApplyOnStartup(false)` в `Program.Main`):
отчетите се подават срещу срок и програмата не бива да се смени сама сутринта,
в която човек сяда да подава.

Пусната от папка — от Visual Studio или старият самостоятелен exe —
програмата само казва, че има ново, и води до страницата на изданието.

---

## 8. След първия пуш

| Работа | Бележка |
|---|---|
| Клон `main` защитен | Дори при един разработчик — спира случайния `--force`. |
| Първият работен поток `build` | Тестовете вървят на `windows-latest`. Шрифтът там е Segoe UI, не Segoe UI Variable — прегледът за лаптоп проверява и вида на Windows 10. Ако падне там, пада и при хората с Windows 10. |
| Сертификат за подписване | Без него Windows предупреждава при инсталиране (виж ПЪТНА_КАРТА, 3.5). |

---

## Кратък списък

- [x] Одит за тайни _(повтаря се при всяко изнасяне)_
- [x] `.gitignore` и `.gitattributes`
- [x] Чиста папка — `tools\export-clean.ps1`
- [x] `README.md` пренаписан; `README.txt` и мъртвият интерфейс не влизат
- [x] Обновяване от GitHub; новини от `news.json`
- [x] Работни потоци: `build` и `release`
- [x] `CHANGELOG.md` с първото издание през GitHub — 1.3.0
- [ ] Коммит и частно хранилище
- [ ] Публично хранилище с изданията
- [ ] `RELEASES_TOKEN` — на машината и като тайна
- [ ] Издание 1.3.0
- [ ] Клон `main` защитен
- [ ] Мъртвият интерфейс и `README.txt` — махнати и от работната папка
