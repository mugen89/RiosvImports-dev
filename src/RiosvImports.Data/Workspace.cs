using Microsoft.Data.Sqlite;

namespace RiosvImports.Data;

/// <summary>
/// Местната база — всичко, по което потребителят работи.
///
/// Устройството ѝ е нарочно оскъдно: три таблици и нито едно поле, зависещо
/// от конкретен вид отчет. Видовете отчети идват от схемите на НИСО и се
/// менят без наше участие; ако всеки от тях имаше своя таблица, всяко тяхно
/// изменение щеше да иска нова версия на базата. Затова редовете се пазят
/// като JSON — колона се добавя, без базата да знае за това.
///
/// Разширяване става САМО с нова стъпка в <see cref="Migrations"/>. Стари
/// стъпки не се пипат: те вече са изпълнени по машините на потребителите.
/// </summary>
public sealed class Workspace : IDisposable
{
    /// <summary>
    /// Всяка стъпка се изпълнява веднъж и се брои в „PRAGMA user_version“.
    /// Добавя се В КРАЯ. Никога по средата и никога с промяна на съществуваща.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // 1 — първоначално устройство
        """
        CREATE TABLE documents (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            schema_key  TEXT    NOT NULL,
            report      TEXT    NOT NULL,
            title       TEXT    NOT NULL,
            status      TEXT    NOT NULL DEFAULT 'draft',
            header      TEXT    NOT NULL DEFAULT '{}',
            created_utc TEXT    NOT NULL,
            updated_utc TEXT    NOT NULL,
            signed_utc  TEXT,
            output_path TEXT
        );

        CREATE TABLE sheet_rows (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            node        TEXT    NOT NULL,
            ordinal     INTEGER NOT NULL,
            data        TEXT    NOT NULL
        );

        CREATE INDEX ix_sheet_rows_document ON sheet_rows(document_id, node, ordinal);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE news (
            id        TEXT PRIMARY KEY,
            published TEXT NOT NULL,
            severity  TEXT NOT NULL,
            title     TEXT NOT NULL,
            body      TEXT NOT NULL,
            seen_utc  TEXT
        );
        """,

        // 2 — откъде е дошъл отчетът
        //
        // Стойността е текст, а не число: утре ще има и отчети, съставени от
        // други програми, и всеки нов произход трябва да е нов ред в кода, а
        // не нова стъпка по базата. Заварените записи са правени на ръка.
        """
        ALTER TABLE documents ADD COLUMN origin TEXT NOT NULL DEFAULT 'manual';
        """,

        // 3 — произходът на заварените записи
        //
        // Стъпка 2 сложи „в програмата“ на всичко, включително на отчетите,
        // внесени от Excel преди колоната да съществува. Те обаче се разпознават:
        // при внасяне програмата сама добавя „(от Excel)“ към името. Това не е
        // гадаене по съдържание, а четене на собствен подпис.
        //
        // Отделна стъпка, а не поправка на предишната: стъпка 2 вече е
        // изпълнена по машините и не се пипа.
        """
        UPDATE documents SET origin = 'excel'
        WHERE origin = 'manual' AND title LIKE '%(от Excel)';
        """,

        // 4 — фирми
        //
        // Един счетоводител води по няколко задължени лица. Досега ЕИК-ът беше
        // една стойност в настройките; сега е списък, а всеки отчет сочи към
        // фирма. Броят фирми ще се ограничава от абонамента.
        //
        // Заварената стойност от настройките става първата фирма, за да не се
        // губи вече въведеното. Отчетите остават без фирма — кой на коя
        // принадлежи знае само човекът.
        """
        CREATE TABLE companies (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            eik         TEXT    NOT NULL UNIQUE,
            name        TEXT    NOT NULL,
            created_utc TEXT    NOT NULL
        );

        ALTER TABLE documents ADD COLUMN company_id INTEGER REFERENCES companies(id);

        INSERT INTO companies (eik, name, created_utc)
        SELECT TRIM(eik.value),
               COALESCE(NULLIF(TRIM((SELECT value FROM settings WHERE key = 'default.company')), ''),
                        TRIM(eik.value)),
               datetime('now')
        FROM settings eik
        WHERE eik.key = 'default.eik' AND LENGTH(TRIM(eik.value)) > 0;
        """,

        // 5 — начални количества за баланса
        //
        // Балансът тръгва от нула само първата година; после в началото на
        // всяка следваща на площадката вече има отпадък. Това количество не
        // идва от отчет — то е състояние отпреди програмата — затова е
        // отделна таблица, а не изведено число.
        //
        // Ключът е фирма + година + код: балансите се водят по фирми, а
        // повторното внасяне на същия файл трябва да замества, не да трупа.
        // Количеството е текст по същата причина, по която и редовете са JSON
        // — SQLite няма точен десетичен тип, а тонажите се събират.
        """
        CREATE TABLE opening_balances (
            company_id  INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            year        INTEGER NOT NULL,
            ewc         TEXT    NOT NULL,
            amount      TEXT    NOT NULL,
            updated_utc TEXT    NOT NULL,
            PRIMARY KEY (company_id, year, ewc)
        );
        """,

        // 6 — кратки списъци
        //
        // Схемата дава осемстотин ЕКО кода и тринайсет операции по оползотворяване;
        // една фирма ползва двайсет от първите и две-три от вторите. Тук се пази
        // кое от менюто да се предлага — за всяка фирма поотделно, защото това е
        // работа на човека, а не на схемата.
        //
        // Обхватът е три текста, а не три таблици: празен report значи „за всички
        // отчети от вида“, празен node — „за целия отчет“. Правилото кой ред печели
        // живее в Core/Values/ValueLimits.cs и се проверява с тест.
        //
        // Пазят се само кодовете, като JSON: номенклатурата им идва от схемата и се
        // мени, кодът — не. Ако НИСО махне код, той просто изчезва и от двете страни.
        //
        // Ключът е UNIQUE без id: втори списък за същото поле в същия обхват не е
        // допълнение, а недоразумение — вторият замества първия.
        """
        CREATE TABLE value_lists (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id  INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            schema_key  TEXT    NOT NULL,
            report      TEXT    NOT NULL DEFAULT '',
            node        TEXT    NOT NULL DEFAULT '',
            column_name TEXT    NOT NULL,
            allowed     TEXT    NOT NULL,
            enabled     INTEGER NOT NULL DEFAULT 1,
            updated_utc TEXT    NOT NULL,
            UNIQUE (company_id, schema_key, report, node, column_name)
        );
        """,

        // 7 — партиди
        //
        // Партидата е носителят на произхода. Договорните отношения искат за
        // всеки предаден тон да се каже чий е бил; отговорът се пази оттук, а
        // не се гади после по дати и количества.
        //
        // Един приемен ред прави една партида. Не една на клиент: същият клиент
        // носи два кода в един ден и те се третират поотделно. Не една на
        // клиент и код: две доставки в различни дни се изчерпват в различен ред,
        // а протоколът, с който е дошла всяка, е различен.
        //
        // Площадката е в ключа, защото две площадки на едно дружество нямат общ
        // склад. Схемата иска „stagenum“ на всеки ред и отчетът за втората
        // площадка не бива да черпи от наличността на първата.
        //
        // „opening“ отбелязва заварената наличност — тя не е приемане и в
        // отчета не влиза.
        """
        CREATE TABLE batches (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id    INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            stagenum      TEXT    NOT NULL DEFAULT '1',
            code          TEXT    NOT NULL,
            eik           TEXT    NOT NULL DEFAULT '',
            partner_name  TEXT    NOT NULL DEFAULT '',
            ewc           TEXT    NOT NULL,
            received_on   TEXT    NOT NULL,
            document      TEXT    NOT NULL DEFAULT '',
            fields        TEXT    NOT NULL DEFAULT '{}',
            opening       INTEGER NOT NULL DEFAULT 0,
            created_utc   TEXT    NOT NULL,
            UNIQUE (company_id, code)
        );

        CREATE INDEX ix_batches_stock ON batches(company_id, stagenum, ewc);
        """,

        // 8 — движенията
        //
        // Четирите движения на отчетната книга, но като случки, а не като
        // листове: нещо влиза някъде или излиза оттам. Складът се смята от тях,
        // а отчетът се съставя от тях — оттук нататък отчетът е проекция, не
        // източник.
        //
        // Останалите колони от схемата (категория НУБА, основание, дейност R/D)
        // се пазят като JSON по същата причина, по която и редовете на отчетите
        // са JSON: колоните идват от схемите на НИСО и се менят без наше
        // участие. Колона се добавя, без базата да знае за това.
        //
        // Количеството е текст с точка за десетичен знак — SQLite няма точен
        // десетичен тип, а тонажите се събират и вадят.
        //
        // „period“ се извежда от датата, но се пази отделно и се индексира:
        // изгледът „Месец“ пита точно по него, а „2026-09“ се подрежда по
        // азбучен ред точно както по време.
        """
        CREATE TABLE ledger_moves (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id  INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            stagenum    TEXT    NOT NULL DEFAULT '1',
            kind        TEXT    NOT NULL,
            moved_on    TEXT    NOT NULL,
            period      TEXT    NOT NULL,
            ewc         TEXT    NOT NULL,
            amount      TEXT    NOT NULL,
            fields      TEXT    NOT NULL DEFAULT '{}',
            note        TEXT    NOT NULL DEFAULT '',
            opening     INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT    NOT NULL
        );

        CREATE INDEX ix_moves_period ON ledger_moves(company_id, stagenum, period, kind);
        CREATE INDEX ix_moves_date ON ledger_moves(company_id, stagenum, moved_on);
        """,

        // 9 — кой дял от движението е от коя партида
        //
        // Едно движение пипа няколко партиди — третиране, което черпи от три
        // доставки — затова връзката е отделен запис, а не поле върху самото
        // движение. Поле би побрало само първата от трите.
        //
        // Връзката е една и съща за четирите движения и точно затова произходът
        // се проследява без изключения: полученото и образуваното добавят към
        // партидата, третираното и предаденото вадят от нея, а знакът идва от
        // вида на движението. Образуваното наследява дяловете на третирането,
        // което го е произвело — оттам произходът минава през целия път.
        """
        CREATE TABLE ledger_links (
            move_id  INTEGER NOT NULL REFERENCES ledger_moves(id) ON DELETE CASCADE,
            batch_id INTEGER NOT NULL REFERENCES batches(id) ON DELETE CASCADE,
            amount   TEXT    NOT NULL,
            PRIMARY KEY (move_id, batch_id)
        );

        CREATE INDEX ix_links_batch ON ledger_links(batch_id);
        """,

        // 10 — затворени месеци
        //
        // Затварянето не пази числа — те се смятат от движенията при всяко
        // питане. Пази само решението: този месец е прегледан и приет такъв,
        // какъвто е. Записан ред значи затворен месец; „note“ пази с какво е
        // приет, когато е приет с бележки.
        """
        CREATE TABLE ledger_periods (
            company_id INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            stagenum   TEXT    NOT NULL DEFAULT '1',
            period     TEXT    NOT NULL,
            closed_utc TEXT    NOT NULL,
            note       TEXT    NOT NULL DEFAULT '',
            PRIMARY KEY (company_id, stagenum, period)
        );
        """,

        // 11 — пряко предаване
        //
        // Не всичко минава през третиране. Площадка за събиране приема опаковки
        // от стотина контрагента и ги предава на преработвател почти както са
        // дошли: за юли 2026 в един истински дневник има 553 приемания, ЕДНА
        // третиране и осем предавания на близо 1200 тона. Предаденото там не
        // може да излезе от склад „Образуван“ — то никога не е влизало в него.
        //
        // Затова предаването може да черпи и от склад „Получен“. Колоната казва
        // от кой: празна (0) е обичайното — предава се образуваното; 1 е прякото
        // предаване. Отчетът и в двата случая го записва като предаден отпадък,
        // защото за НИСО разликата я няма — тя е в това откъде се вади
        // наличността и чий е бил отпадъкът.
        //
        // Само предаването я чете. За другите три вида складът се определя
        // еднозначно от вида на движението.
        """
        ALTER TABLE ledger_moves ADD COLUMN direct INTEGER NOT NULL DEFAULT 0;
        """,

        // 12 — кодът, с който третираното влиза в отчета
        //
        // Складът се вади по кода, с който отпадъкът е ПРИЕТ — иначе наличността
        // не намалява там, откъдето количеството наистина си отива. Книгата
        // обаче иска кода, под който е третиран, а той невинаги е същият: от
        // площадка се третират пет приети кода в едно и също производство и
        // човекът иска един ред, не пет.
        //
        // Затова два кода на едно движение: „ewc“ пипа склада, „ewc_out“ отива
        // в отчета. Празно значи „същият“ — и точно затова заварените движения
        // не мърдат: те нямат изходен код и продължават да се подават с
        // приетия.
        //
        // Чете го само третирането. За другите три вида вторият код би бил
        // измислен, а не пропуснат.
        """
        ALTER TABLE ledger_moves ADD COLUMN ewc_out TEXT NOT NULL DEFAULT '';
        """,

        // 13 — внесените файлове
        //
        // Внасянето влиза наведнъж и точно затова грешката при него е скъпа:
        // същият файл, внесен два пъти, или правилният файл, внесен в чужда
        // фирма, се поправят ред по ред — а редовете са петстотин и нищо в тях
        // не казва, че са дошли заедно. Човек не помни кои са те и час по-късно
        // вече не може да ги посочи.
        //
        // Затова се пази самото внасяне: кой файл, кога и в коя фирма — и кои
        // записи е родило. Оттам поправката е едно действие вместо петстотин.
        //
        // Връзката е отделна таблица, а не колона по записите: едно внасяне
        // ражда различни неща — движения, партиди, отчети — и колона по всяко
        // от тях щеше да значи нова стъпка по базата за всеки нов вид внасяне.
        // Тук новият вид е нова дума в „target“.
        //
        // „record_id“ не е външен ключ по същата причина: сочи към различни
        // таблици. Изчезнал запис не е грешка — той просто спира да се брои
        // (вж. ImportStore.List) и не се маха втори път.
        """
        CREATE TABLE imports (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id  INTEGER REFERENCES companies(id) ON DELETE CASCADE,
            source      TEXT    NOT NULL,
            file_name   TEXT    NOT NULL DEFAULT '',
            note        TEXT    NOT NULL DEFAULT '',
            created_utc TEXT    NOT NULL
        );

        CREATE TABLE import_items (
            import_id INTEGER NOT NULL REFERENCES imports(id) ON DELETE CASCADE,
            target    TEXT    NOT NULL,
            record_id INTEGER NOT NULL,
            PRIMARY KEY (import_id, target, record_id)
        );

        CREATE INDEX ix_import_items_record ON import_items(target, record_id);
        """,

        // 13. Дневник на въведеното от прозорчетата на склада.
        //
        // Оплакването, което го роди: „ако съм сбъркал датата, не знам в кой
        // месец да го търся“. Въведеното отива направо в своя месец и оттам
        // нататък е неразличимо от всичко останало — а сгрешената дата значи
        // именно, че месецът не е този, в който човек ще погледне.
        //
        // Затова редът се помни ОТДЕЛНО от самото движение: кога е въведен, на
        // коя дата е записан и в кой месец е паднал. Оттам поправката е едно
        // щракване, вместо обхождане на дванайсет месеца.
        //
        // Дневникът е за изхвърляне и точно затова е отделна таблица, а не
        // колона по движенията: изчистен, той не оставя следа по склада. Пази
        // се по фирма и се чисти по фирма — работата по едно дружество трае
        // дни и чуждият дневник не влиза в нея.
        //
        // „move_id“ не е външен ключ нарочно: движението може да бъде махнато
        // отвсякъде другаде, а редът в дневника трябва да оцелее, за да каже
        // „това вече го няма“ вместо да изчезне мълчаливо.
        """
        CREATE TABLE action_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id  INTEGER REFERENCES companies(id) ON DELETE CASCADE,
            stagenum    TEXT    NOT NULL DEFAULT '1',
            kind        TEXT    NOT NULL,
            move_id     INTEGER NOT NULL,
            moved_on    TEXT    NOT NULL,
            period      TEXT    NOT NULL,
            ewc         TEXT    NOT NULL,
            amount      TEXT    NOT NULL,
            note        TEXT    NOT NULL DEFAULT '',
            created_utc TEXT    NOT NULL
        );

        CREATE INDEX ix_action_log_company ON action_log(company_id, id DESC);
        CREATE INDEX ix_action_log_move ON action_log(move_id);
        """,

        // 14. Получателите: на кого предава тази фирма.
        //
        // Името на получателя е СВОБОДЕН текст по схемата и ще си остане такъв —
        // НИСО не дава номенклатура и не бива да се измисля. Но свободно поле,
        // писано на ръка по трийсет пъти месечно, ражда „Претопи ООД“,
        // „Претопи OOD“ и „претопи оод“ — три получателя вместо един, и нито
        // едно търсене не ги събира.
        //
        // Затова написаното веднъж се помни и се предлага втория път. Списъкът е
        // малък по устройство: площадка предава на шепа оползотворители, не на
        // стотици.
        //
        // „role“ съществува заради другата страна: подателят вече се помни като
        // партида, но утре и той може да поиска същото. Нов вид е нова дума, не
        // нова стъпка по базата.
        //
        // Ключът е (фирма, вид, ЕИК, име): един и същ получател под два ЕИК-а е
        // два записа нарочно — тогава единият е сгрешен и се вижда, че е.
        """
        CREATE TABLE partners (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id  INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
            role        TEXT    NOT NULL DEFAULT 'receiver',
            eik         TEXT    NOT NULL DEFAULT '',
            name        TEXT    NOT NULL,
            used        INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT    NOT NULL
        );

        CREATE UNIQUE INDEX ux_partners ON partners(company_id, role, eik, name);
        """,

        // 15. Кое е записано наведнъж.
        //
        // Едно третиране от прозореца често са три движения — по код — и
        // сгрешената дата е една и съща и за трите. Поправката отваря действието
        // цяло, а не ред по ред; затова редовете на един запис носят общ белег.
        // Старите редове остават без белег и се поправят поотделно.
        """
        ALTER TABLE action_log ADD COLUMN action TEXT NOT NULL DEFAULT '';
        """,

        // 16. Дневникът на два раздела.
        //
        // Първият е работният — стои чист, докато отчетът се подаде. Махнатото
        // оттам не се трие, а отива във втория, „Отложено“: там се трие
        // истински, защото е сгрешено. Движението остава, докато не бъде
        // изтрито оттам.
        """
        ALTER TABLE action_log ADD COLUMN trashed INTEGER NOT NULL DEFAULT 0;
        """,

        // 17. По какъв договор е излязло предаденото.
        //
        // Свое поле, а не ключ в „fields“: там стоят колоните на СХЕМАТА, които
        // пътуват до отчета, а договорът е наш и в книгата няма място. Не е и
        // бележката: тя е свободен текст за еднократни неща, а договорът се
        // избира от списък и по него се пресява — изписан по два начина, той
        // са два договора.
        //
        // Самият списък не иска таблица: той е нова дума в „partners.role“,
        // точно както обещава стъпка 14.
        """
        ALTER TABLE ledger_moves ADD COLUMN contract TEXT NOT NULL DEFAULT '';
        """,

        // 18. Поправката да е същото действие, а не ново.
        //
        // „Поправи…“ отваря прозореца, от който действието е дошло, но от него
        // оставаше само записаното: кои партиди са взети. Какво е предлагал
        // прозорецът — и какво е показал кантарът — се губеше, а без него грешно
        // взета партида не може да се смени с вярната. Затова действието пази
        // прозореца си. Отделна таблица, по белег: то е едно за всичките си редове,
        // а обхватът може да е стотици партиди.
        //
        // В дневника редът пази и следата: кога е поправян и какъв е бил, защото
        // поправка, от която нищо не личи, е загубена следа.
        """
        CREATE TABLE action_windows (
            action TEXT PRIMARY KEY,
            window TEXT NOT NULL
        );

        ALTER TABLE action_log ADD COLUMN amended_utc TEXT NOT NULL DEFAULT '';
        ALTER TABLE action_log ADD COLUMN was TEXT NOT NULL DEFAULT '';
        """,

        // 20 — новините получават връзка и могат да се скрият
        //
        // Таблицата е от първата стъпка, но дотук не се пълнеше от нищо. Сега
        // се пълни от файл в GitHub при всяко пускане, а файлът е истината:
        // махнатото от него си отива и оттук (NewsStore.Sync). Затова скриването
        // е отметка върху реда, а не триене — изтрито, съобщението щеше да се
        // върне при следващото теглене.
        //
        // Връзката е празен низ, а не NULL: „няма връзка“ е обичайното и се чете
        // без проверка.
        """
        ALTER TABLE news ADD COLUMN link TEXT NOT NULL DEFAULT '';
        ALTER TABLE news ADD COLUMN hidden_utc TEXT;
        """
    ];

    private readonly SqliteConnection _connection;

    private Workspace(SqliteConnection connection)
    {
        _connection = connection;

        Documents = new DocumentStore(connection);
        Companies = new CompanyStore(connection);
        Openings = new OpeningStore(connection);
        // Настройките се раждат преди списъците: в тях стои отметката „попълвай
        // останалата една стойност“, която списъците подават нататък.
        Settings = new SettingsStore(connection);
        Lists = new ValueListStore(connection, Settings);
        News = new NewsStore(connection);

        // Складът се сглобява след настройките: месецът, от който се води, е
        // настройка на фирма, а не колона в таблица.
        Batches = new BatchStore(connection);
        Ledger = new LedgerStore(connection);
        Periods = new PeriodStore(connection, Settings);
        Setup = new LedgerSetup(Batches, Ledger, Periods);
        // Контрагентите се раждат преди писача: всяко записано приемане
        // научава доставчика си.
        Partners = new PartnerStore(connection);
        Writer = new LedgerWriter(Batches, Ledger, Partners);

        // Дневникът на внесеното е последен: махането на внасяне минава през
        // писача и през месеците, а те трябва да ги има преди него.
        Imports = new ImportStore(connection);
        History = new ImportHistory(Imports, Documents, Batches, Ledger, Periods, Writer);

        // Дневникът на въведеното от ръка е отделен от дневника на внесеното:
        // единият помни файл, другият — едно движение, и се чистят поотделно.
        Actions = new ActionLog(connection);

        // Получателите и доставчиците се помнят сами — от предаванията и от
        // приеманията: свободното поле не се заменя със списък, а се научава.
    }

    /// <summary>
    /// Доставчиците на фирмата — от кого приема.
    ///
    /// Първият път списъкът се допълва от вече заведените партиди: доставчик,
    /// приеман преди списъка да съществува, не бива да чака следващото си
    /// приемане, за да бъде предложен. Само веднъж — иначе махнат оттук
    /// доставчик би се връщал при всяко отваряне.
    /// </summary>
    public IReadOnlyList<Partner> Originators(long companyId)
    {
        var key = $"partners.originators.seeded.{companyId}";

        if (Settings.Get(key, string.Empty).Length == 0)
        {
            Partners.Seed(companyId,
                Batches.List(companyId).Where(b => !b.Opening).Select(b => (b.Eik, b.PartnerName)),
                PartnerRole.Originator);

            Settings.Set(key, "1");
        }

        return Partners.List(companyId, PartnerRole.Originator);
    }

    public DocumentStore Documents { get; }
    public CompanyStore Companies { get; }
    public OpeningStore Openings { get; }
    public ValueListStore Lists { get; }
    public SettingsStore Settings { get; }
    public NewsStore News { get; }

    /// <summary>Партидите: кой какво е донесъл и кога.</summary>
    public BatchStore Batches { get; }

    /// <summary>Движенията и дяловете им по партиди.</summary>
    public LedgerStore Ledger { get; }

    /// <summary>Отварянето и затварянето на месеца.</summary>
    public PeriodStore Periods { get; }

    /// <summary>Дневникът на въведеното от прозорчетата на склада — за изхвърляне.</summary>
    public ActionLog Actions { get; }

    /// <summary>На кого предава тази фирма — научено от самите предавания.</summary>
    public PartnerStore Partners { get; }

    /// <summary>Началото на воденето: от кой месец и с какво в склада.</summary>
    public LedgerSetup Setup { get; }

    /// <summary>Записването на движение с партидата и дяловете му.</summary>
    public LedgerWriter Writer { get; }

    /// <summary>Следата на внесените файлове: кой файл какво е родил.</summary>
    public ImportStore Imports { get; }

    /// <summary>Махането на цяло внасяне — с правилата на склада.</summary>
    public ImportHistory History { get; }

    /// <summary>Къде стои файлът — показва се в настройките, за да се архивира.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Отваря базата в профила на потребителя и я привежда до последната версия.</summary>
    public static Workspace Open(string? path = null)
    {
        path ??= DefaultPath();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Записът е кратък и чест — WAL пази работата при внезапно спиране.
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        connection.Open();

        Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");

        var workspace = new Workspace(connection) { Path = path };
        workspace.Migrate();

        return workspace;
    }

    /// <summary>
    /// Старото място на базата — в профила на потребителя.
    ///
    /// От 1.3.1 програмата пази работата до себе си (виж DataLocation в
    /// програмата) и оттук само я пренася при първото пускане. Остава като
    /// подразбиране за онези, които не знаят къде е програмата — инструментът за
    /// пробни данни, — и като мястото, където работата чака след деинсталиране.
    /// </summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RIOSV Imports",
        "workspace.db");

    /// <summary>
    /// Копира база от един файл в друг през SQLite — с всичко, което още стои в WAL.
    ///
    /// Без пул на връзките: върната в пула, връзката държи файла отворен, а
    /// след копието старият файл се преименува — и Windows не дава да се
    /// преименува отворен файл.
    /// </summary>
    public static void Copy(string from, string to)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = from,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = to,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        source.Open();
        target.Open();

        source.BackupDatabase(target);
    }

    /// <summary>
    /// Прилага само липсващите стъпки. Всички са в една транзакция: наполовина
    /// приведена база е по-лоша от непокътната.
    /// </summary>
    private void Migrate()
    {
        var current = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (current >= Migrations.Length) return;

        using var transaction = _connection.BeginTransaction();

        for (var version = current; version < Migrations.Length; version++)
            Execute(_connection, Migrations[version], transaction);

        // PRAGMA не приема параметър, а стойността идва от дължината на масив.
        Execute(_connection, $"PRAGMA user_version = {Migrations.Length};", transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Изпълнява работата цялата или никак.
    ///
    /// За неща, които минават през няколко хранилища наведнъж: поправката маха
    /// старото, пише новото и подрежда дневника. Прекъсната по средата, тя щеше да
    /// остави НИЩО — нито старото, нито новото. Грешката тук се връща нагоре,
    /// след като базата е върната такава, каквато беше.
    /// </summary>
    public void Atomically(Action work)
    {
        Execute(_connection, "BEGIN IMMEDIATE;");

        Ledger.Joined = true;

        try
        {
            work();

            Execute(_connection, "COMMIT;");
        }
        catch
        {
            Execute(_connection, "ROLLBACK;");
            throw;
        }
        finally
        {
            Ledger.Joined = false;
            Ledger.Reuse([]);
        }
    }

    /// <summary>
    /// Колко има в склада и във внесеното — за всички фирми.
    ///
    /// Прозорецът за изчистване казва числата, преди да изтрие: „ще се изтрият
    /// 1 150 движения“ е решение, „ще се изтрие складът“ — не.
    /// </summary>
    public StockCounts CountStock() => new(
        Count("SELECT COUNT(*) FROM ledger_moves WHERE opening = 0;"),
        Count("SELECT COUNT(*) FROM ledger_moves WHERE opening = 1;"),
        Count("SELECT COUNT(*) FROM batches;"),
        Count("SELECT COUNT(*) FROM imports;"),
        Count("SELECT COUNT(*) FROM action_log;"),
        Count("SELECT COUNT(*) FROM ledger_periods;"),
        Count($"SELECT COUNT(*) FROM settings WHERE key LIKE '{SettingsStore.Keys.LedgerStart}.%';"));

    /// <summary>
    /// Изчиства склада и всичко внесено — за всички фирми, наведнъж.
    ///
    /// За проба: тестващият внася един и същ дневник отново и отново и всеки път
    /// трябва да започне от празен склад. Махат се движенията с дяловете им,
    /// партидите, началните количества и началото на воденето, затворените
    /// месеци, дневникът на въведеното и следата на внесените файлове.
    ///
    /// Остават фирмите, отчетите, кратките списъци, получателите, доставчиците,
    /// договорите и настройките: те не са склад, а без тях всяка проба почва с
    /// попълване на фирма. Отчет, внесен от файл, остава като обикновен отчет —
    /// маха се само следата, че е внесен.
    ///
    /// Всичко или нищо: наполовина изчистен склад е по-лош от неизчистен.
    /// </summary>
    public StockCounts ClearStock()
    {
        var counts = CountStock();

        Atomically(() =>
        {
            // Редът е по зависимост: който сочи, пада преди този, към когото сочи.
            Execute(_connection, "DELETE FROM import_items;");
            Execute(_connection, "DELETE FROM imports;");
            Execute(_connection, "DELETE FROM action_windows;");
            Execute(_connection, "DELETE FROM action_log;");
            Execute(_connection, "DELETE FROM ledger_links;");
            Execute(_connection, "DELETE FROM ledger_moves;");
            Execute(_connection, "DELETE FROM batches;");
            Execute(_connection, "DELETE FROM ledger_periods;");
            Execute(_connection, $"DELETE FROM settings WHERE key LIKE '{SettingsStore.Keys.LedgerStart}.%';");
        });

        return counts;
    }

    private int Count(string sql) => Convert.ToInt32(Scalar(sql));

    /// <summary>
    /// Прави копие на базата.
    ///
    /// През SQLite, а не с копиране на файла: при WAL част от последните записи
    /// стоят в отделен файл до него и обикновено копие би ги пропуснало.
    /// </summary>
    public void BackupTo(string path)
    {
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        target.Open();
        _connection.BackupDatabase(target);
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Колко има в склада и във внесеното — за всички фирми.
///
/// <paramref name="Openings"/> са началните количества (заварената наличност),
/// <paramref name="Starts"/> — за колко фирми е обявен месецът, от който се води
/// складът. <paramref name="Logged"/> са редовете в дневника на въведеното.
/// </summary>
public sealed record StockCounts(
    int Moves, int Openings, int Batches, int Imports, int Logged, int Closed, int Starts)
{
    /// <summary>Няма какво да се изчиства.</summary>
    public bool IsEmpty => Moves + Openings + Batches + Imports + Logged + Closed + Starts == 0;
}
