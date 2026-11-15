using Microsoft.Data.Sqlite;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Values;

namespace RiosvImports.Data;

/// <summary>
/// Едно движение, както се записва.
///
/// <paramref name="Draws"/> са дяловете по партиди. Празно значи неразпределено
/// движение — записва се, но контролите го показват: произходът е половин
/// отговор, а не грешка, която да спре въвеждането.
/// </summary>
public sealed record MoveDraft(
    MoveKind Kind,
    DateOnly Date,
    string Ewc,
    decimal Amount,
    string Stagenum = "1",
    bool Opening = false,
    bool Direct = false,
    string Note = "",
    IReadOnlyDictionary<string, string>? Fields = null,
    IReadOnlyList<Draw>? Draws = null,
    string EwcOut = "",
    string Contract = "");

/// <summary>
/// Движенията и дяловете им по партиди.
///
/// Хранилището НЕ проверява наличност. Дали складът покрива едно третиране
/// решава <see cref="Reconcile"/> в „Ядро“, а какво да се направи по въпроса —
/// човекът. Тук движение се записва такова, каквото е поискано, включително
/// когато е невъзможно: иначе половин въведен месец не може да се запише и да
/// се поправи после.
///
/// Записът на движение и на дяловете му е в една транзакция. Движение без
/// дяловете си е движение с измислен произход — по-лошо от липсващо.
/// </summary>
public sealed class LedgerStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>
    /// Върви ли обща транзакция (<see cref="Workspace.Atomically"/>). Тогава записът
    /// не отваря своя: SQLite не влага транзакции, а цялото си отива заедно.
    /// </summary>
    internal bool Joined { get; set; }

    private SqliteTransaction? Begin() => Joined ? null : _connection.BeginTransaction();

    private readonly Queue<long> _reused = new();

    /// <summary>
    /// Следващите записи влизат на тези номера, поред.
    ///
    /// За поправката: поправеното движение си остава на мястото си в деня — в месеца,
    /// в пътя на партидата и в реда на отчета. Новият номер би го пратил в края на
    /// деня, след онова, което е станало от него.
    /// </summary>
    public void Reuse(IEnumerable<long> ids)
    {
        _reused.Clear();

        foreach (var id in ids) _reused.Enqueue(id);
    }

    // ------------------------------------------------------------- четене

    /// <summary>
    /// Всички движения на една фирма и площадка.
    ///
    /// Цялата история, не един месец: наличността към дата се смята от всичко
    /// преди нея, а „всичко“ на една площадка са хиляди реда, не милиони.
    /// Пресяването по месец е за изгледа, не за сметката.
    /// </summary>
    public IReadOnlyList<Move> Read(long companyId, string? stagenum = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, moved_on, ewc, amount, stagenum, opening, direct, ewc_out
            FROM ledger_moves
            WHERE company_id = $company AND ($stage IS NULL OR stagenum = $stage)
            ORDER BY moved_on, id;
            """;

        Scope(command, companyId, stagenum);

        var moves = new List<Move>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var date = LedgerText.ReadDate(reader.GetString(2));
            if (date is null) continue;

            moves.Add(new Move(
                reader.GetInt64(0),
                ParseKind(reader.GetString(1)),
                date.Value,
                reader.GetString(3),
                LedgerText.ReadAmount(reader.GetString(4)),
                null,
                reader.GetString(5),
                reader.GetInt32(6) == 1,
                reader.GetInt32(7) == 1,
                reader.GetString(8)));
        }

        return moves;
    }

    /// <summary>Движенията само за един месец — изгледът „Месец“.</summary>
    public IReadOnlyList<Move> InPeriod(long companyId, string? stagenum, LedgerPeriod period) =>
    [
        .. Read(companyId, stagenum).Where(m => period.Covers(m.Date))
    ];

    /// <summary>
    /// Дяловете по партиди за същия обхват.
    ///
    /// Четат се наведнъж с движенията, а не по едно на движение: наличността по
    /// партиди се смята от всички връзки, и обиколка за всеки ред би направила
    /// от един екран хиляда заявки.
    /// </summary>
    public IReadOnlyList<LedgerLink> Links(long companyId, string? stagenum = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT l.move_id, l.batch_id, l.amount
            FROM ledger_links l
            JOIN ledger_moves m ON m.id = l.move_id
            WHERE m.company_id = $company AND ($stage IS NULL OR m.stagenum = $stage);
            """;

        Scope(command, companyId, stagenum);

        var links = new List<LedgerLink>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            links.Add(new LedgerLink(reader.GetInt64(0), reader.GetInt64(1), LedgerText.ReadAmount(reader.GetString(2))));

        return links;
    }

    /// <summary>Дяловете на едно движение — за прозорчето, което ги показва.</summary>
    public IReadOnlyList<Draw> Draws(long moveId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT batch_id, amount FROM ledger_links WHERE move_id = $move ORDER BY batch_id;";
        command.Parameters.AddWithValue("$move", moveId);

        var draws = new List<Draw>();
        using var reader = command.ExecuteReader();

        while (reader.Read()) draws.Add(new Draw(reader.GetInt64(0), LedgerText.ReadAmount(reader.GetString(1))));

        return draws;
    }

    /// <summary>Колоните, които пътуват до отчета, но не участват в сметка.</summary>
    public Dictionary<string, string> Fields(long moveId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT fields FROM ledger_moves WHERE id = $id;";
        command.Parameters.AddWithValue("$id", moveId);

        return LedgerText.ReadFields(command.ExecuteScalar() as string);
    }

    /// <summary>
    /// Получателите на предаванията — по едно четене, а не по едно на движение.
    ///
    /// Изгледът „По получател“ гледа всички предавания наведнъж; питано за всяко
    /// поотделно, това са стотици заявки за отговор, който идва от една.
    ///
    /// Колоните са на СХЕМАТА и се четат по име оттам: те не участват в нито
    /// една сметка и затова не влизат в <see cref="Move"/> (виж бележката там).
    /// Договорът и бележката идват със същото четене — пресяват се заедно с тях.
    /// </summary>
    public Dictionary<long, HandParty> Parties(long companyId, string? stagenum = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = 
            "SELECT id, fields, contract, note FROM ledger_moves " +
            "WHERE company_id = $company AND kind = 'submitted' " +
            "  AND ($stage IS NULL OR stagenum = $stage);";

        Scope(command, companyId, stagenum);

        var parties = new Dictionary<long, HandParty>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var fields = LedgerText.ReadFields(reader.IsDBNull(1) ? null : reader.GetString(1));

            parties[reader.GetInt64(0)] = new HandParty(
                fields.GetValueOrDefault("name_receiver", string.Empty).Trim(),
                fields.GetValueOrDefault("pin_receiver", string.Empty).Trim(),
                reader.GetString(2),
                reader.GetString(3));
        }

        return parties;
    }

    /// <summary>
    /// Бележките на всички движения наведнъж — само непразните.
    ///
    /// „Месец“ ги показва в стълб, а питани по едно на ред, петстотинте приемания
    /// на един истински месец бяха петстотин заявки при всяко щракване по ситото.
    /// </summary>
    public Dictionary<long, string> Notes(long companyId, string? stagenum = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT id, note FROM ledger_moves " +
            "WHERE company_id = $company AND note <> '' AND ($stage IS NULL OR stagenum = $stage);";

        Scope(command, companyId, stagenum);

        var notes = new Dictionary<long, string>();
        using var reader = command.ExecuteReader();

        while (reader.Read()) notes[reader.GetInt64(0)] = reader.GetString(1);

        return notes;
    }

    public string Note(long moveId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT note FROM ledger_moves WHERE id = $id;";
        command.Parameters.AddWithValue("$id", moveId);

        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>По какъв договор е излязло. Празно значи „не е посочен“.</summary>
    public string Contract(long moveId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT contract FROM ledger_moves WHERE id = $id;";
        command.Parameters.AddWithValue("$id", moveId);

        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>
    /// Площадките, за които има записано нещо.
    ///
    /// Чете се от базата, а не се държи списък: площадка се появява с първото
    /// си движение и изчезва с последното, а отделен списък би трябвало да се
    /// поддържа наравно с това.
    /// </summary>
    public IReadOnlyList<string> Stages(long companyId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT stagenum FROM ledger_moves WHERE company_id = $company
            UNION
            SELECT stagenum FROM batches WHERE company_id = $company
            ORDER BY stagenum;
            """;
        command.Parameters.AddWithValue("$company", companyId);

        var stages = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read()) stages.Add(reader.GetString(0));

        return stages.Count == 0 ? ["1"] : stages;
    }

    /// <summary>Месеците с движение — подредени назад, както се отварят.</summary>
    public IReadOnlyList<LedgerPeriod> Periods(long companyId, string? stagenum = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT period FROM ledger_moves
            WHERE company_id = $company AND ($stage IS NULL OR stagenum = $stage) AND opening = 0
            ORDER BY period DESC;
            """;

        Scope(command, companyId, stagenum);

        var periods = new List<LedgerPeriod>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            if (LedgerPeriod.TryParse(reader.GetString(0), out var period))
                periods.Add(period);

        return periods;
    }

    // ------------------------------------------------------------- писане

    /// <summary>Записва движение с дяловете му и връща номера му.</summary>
    public long Add(long companyId, MoveDraft draft)
    {
        using var transaction = Begin();

        long id;

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ledger_moves
                    (id, company_id, stagenum, kind, moved_on, period, ewc, amount,
                     fields, note, opening, direct, ewc_out, contract, created_utc)
                VALUES ($reused, $company, $stage, $kind, $on, $period, $ewc, $amount,
                        $fields, $note, $opening, $direct, $ewcOut, $contract, $now)
                RETURNING id;
                """;

            command.Parameters.AddWithValue("$reused", _reused.TryDequeue(out var reused) ? reused : DBNull.Value);
            command.Parameters.AddWithValue("$company", companyId);
            Bind(command, draft);

            id = Convert.ToInt64(command.ExecuteScalar());
        }

        WriteDraws(transaction, id, draft.Draws);

        transaction?.Commit();
        return id;
    }

    /// <summary>
    /// Пренаписва движение и дяловете му.
    ///
    /// Дяловете се трият и се записват наново, вместо да се сверяват един по
    /// един: те са шепа редове, а разликата в бързина е незабележима срещу
    /// съществената разлика в това колко неща могат да се объркат.
    /// </summary>
    public void Update(long moveId, MoveDraft draft)
    {
        using var transaction = Begin();

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ledger_moves
                SET stagenum = $stage, kind = $kind, moved_on = $on, period = $period,
                    ewc = $ewc, amount = $amount, fields = $fields, note = $note,
                    opening = $opening, direct = $direct, ewc_out = $ewcOut,
                    contract = $contract
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", moveId);
            Bind(command, draft);

            command.ExecuteNonQuery();
        }

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ledger_links WHERE move_id = $move;";
            clear.Parameters.AddWithValue("$move", moveId);
            clear.ExecuteNonQuery();
        }

        WriteDraws(transaction, moveId, draft.Draws);

        transaction?.Commit();
    }

    /// <summary>Маха движение. Дяловете му падат с него.</summary>
    public void Remove(long moveId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM ledger_moves WHERE id = $id;";
        command.Parameters.AddWithValue("$id", moveId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Маха няколко движения наведнъж — всичките или нито едно.
    ///
    /// Цяло действие, изтрито наполовина, е по-лошо от неизтрито: количеството
    /// се връща отчасти и вече не личи кое е останало от кое.
    /// </summary>
    public void Remove(IEnumerable<long> moveIds)
    {
        using var transaction = Begin();

        foreach (var moveId in moveIds)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ledger_moves WHERE id = $id;";
            command.Parameters.AddWithValue("$id", moveId);
            command.ExecuteNonQuery();
        }

        transaction?.Commit();
    }

    // ------------------------------------------------------------- помощни

    private void WriteDraws(SqliteTransaction? transaction, long moveId, IReadOnlyList<Draw>? draws)
    {
        if (draws is not { Count: > 0 }) return;

        // Два дяла към една партида са начин на писане, не грешка — събират се,
        // защото ключът е (движение, партида) и вторият иначе би изместил първия.
        var merged = new Dictionary<long, decimal>();

        foreach (var draw in draws)
        {
            if (draw.Amount == 0) continue;

            merged[draw.BatchId] = merged.GetValueOrDefault(draw.BatchId) + draw.Amount;
        }

        foreach (var (batchId, amount) in merged)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ledger_links (move_id, batch_id, amount)
                VALUES ($move, $batch, $amount);
                """;

            insert.Parameters.AddWithValue("$move", moveId);
            insert.Parameters.AddWithValue("$batch", batchId);
            insert.Parameters.AddWithValue("$amount", LedgerText.Amount(amount));

            insert.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand command, MoveDraft draft)
    {
        command.Parameters.AddWithValue("$stage", BatchStore.Stage(draft.Stagenum));
        command.Parameters.AddWithValue("$kind", draft.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$on", LedgerText.Date(draft.Date));
        command.Parameters.AddWithValue("$period", LedgerPeriod.Of(draft.Date).ToString());
        command.Parameters.AddWithValue("$ewc", ValueConverter.NormalizeCode(draft.Ewc));
        command.Parameters.AddWithValue("$amount", LedgerText.Amount(draft.Amount));
        command.Parameters.AddWithValue("$fields", LedgerText.Fields(draft.Fields));
        command.Parameters.AddWithValue("$note", draft.Note.Trim());
        command.Parameters.AddWithValue("$opening", draft.Opening ? 1 : 0);
        command.Parameters.AddWithValue("$direct", draft.Direct && Moves.CanBeDirect(draft.Kind) ? 1 : 0);

        // Изходният код важи за двете ИЗЛИЗАЩИ движения — кое може и защо, пише
        // на едно място (<see cref="Moves.CanRename"/>). Влизащите се записват с
        // кода, с който пипат склада: втори код там би бил измислен.
        command.Parameters.AddWithValue("$ewcOut", Moves.CanRename(draft.Kind)
            ? ValueConverter.NormalizeCode(draft.EwcOut)
            : string.Empty);

        command.Parameters.AddWithValue("$contract",
            Moves.HasContract(draft.Kind) ? draft.Contract.Trim() : string.Empty);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
    }

    private static void Scope(SqliteCommand command, long companyId, string? stagenum)
    {
        command.Parameters.AddWithValue("$company", companyId);
        command.Parameters.AddWithValue("$stage", (object?)stagenum ?? DBNull.Value);
    }

    private static MoveKind ParseKind(string value) => value switch
    {
        "treated" => MoveKind.Treated,
        "generated" => MoveKind.Generated,
        "submitted" => MoveKind.Submitted,
        "recycled" => MoveKind.Recycled,
        _ => MoveKind.Received
    };
}
