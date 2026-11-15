using System.Globalization;
using RiosvImports.App.Controls;
using RiosvImports.Core.Balance;
using RiosvImports.Core.Import;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>
/// Кои отчети влизат в баланса и какъв период покрива всеки.
///
/// Балансът е сбор от няколко книги. Ако две от тях покриват един и същ месец,
/// количествата в него се броят два пъти; ако между тях зее седмица, липсва
/// точно толкова. И в двата случая балансът е вярно пресметнат от сгрешени
/// данни — числото изглежда наред и никой не го поглежда втори път.
///
/// Затова тук стоят двете неща един до друг: какво твърди всеки отчет, че
/// покрива, и какви дати наистина има в него. Проверката не поправя нищо —
/// поправката е в самия отчет — а само казва къде да се погледне.
/// </summary>
internal sealed class BalanceReportsDialog : Dialog
{
    private readonly DataGridView _grid = new();
    private readonly IssueList _issues = new();
    private readonly Label _summary = new();

    private readonly IReadOnlyList<ReportPeriod> _reports;

    public BalanceReportsDialog(IReadOnlyList<ReportPeriod> reports, int year, int from, int to)
    {
        _reports = reports;

        Text = Lang.T($"Отчети в баланса — {year} г.", $"Reports in the balance — {year}");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(900), Theme.Scaled(520));
        ClientSize = new Size(Theme.Scaled(1120), Theme.Scaled(640));
        KeyPreview = true;

        var issues = BalanceCoverage.Check(reports, year, from, to);

        // Закачените отгоре се редят наопаки: заглавието влиза последно.
        Controls.Add(BuildBody(issues));
        Controls.Add(BuildSummary(issues));
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Двоен клик върху ред иска отваряне на този отчет.</summary>
    public event EventHandler<long>? OpenRequested;

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(64),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T(
                "Двоен клик отваря отчета. Проверката гледа по фирма — две дружества с еднакви периоди не се застъпват.",
                "Double-click opens a report. The check works per company — two companies with the same periods do not overlap."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            Text = Lang.T("Отчети в баланса", "Reports in the balance"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        return host;
    }

    private Control BuildSummary(IReadOnlyList<CoverageIssue> issues)
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Base.Height + 18,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Bold;

        var gaps = issues.Count(i => i.Kind == CoverageProblem.Gap);
        var overlaps = issues.Count(i => i.Kind == CoverageProblem.Overlap);

        if (issues.Count == 0)
        {
            _summary.ForeColor = Theme.Ok;
            _summary.Text = Lang.T(
                $"{_reports.Count} отчета покриват периода точно веднъж — без пропуски и без застъпвания.",
                $"{_reports.Count} reports cover the period exactly once — no gaps, no overlaps.");
        }
        else
        {
            _summary.ForeColor = Theme.Warn;
            _summary.Text = Lang.T(
                $"{Count(gaps, "пропуск", "пропуска")} и {Count(overlaps, "застъпване", "застъпвания")} " +
                $"между {_reports.Count} отчета.",
                $"{Count(gaps, "gap", "gaps")} and {Count(overlaps, "overlap", "overlaps")} " +
                $"among {_reports.Count} reports.");
        }

        host.Controls.Add(_summary);

        return host;
    }

    /// <summary>Число с думата към него — „1 пропуск“, но „5 пропуска“.</summary>
    private static string Count(int many, string one, string more) =>
        $"{many} {(many == 1 ? one : more)}";

    private Control BuildBody(IReadOnlyList<CoverageIssue> issues)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Canvas,
            SplitterWidth = Theme.Gap
        };

        BuildGrid();

        split.Panel1.Controls.Add(Frame(_grid));
        split.Panel2.Controls.Add(Frame(BuildIssues(issues)));

        host.Controls.Add(split);

        // Разделителят се задава след като контейнерът има размер: преди това
        // Windows Forms го отказва като „извън обхват“.
        split.HandleCreated += (_, _) => split.SplitterDistance = Math.Max(180, ClientSize.Height / 2);

        return host;
    }

    private static Control Frame(Control inner)
    {
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        inner.Dock = DockStyle.Fill;
        card.Controls.Add(inner);

        return card;
    }

    private void BuildGrid()
    {
        _grid.Font = Theme.Base;
        _grid.BackgroundColor = Theme.Canvas;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.InkStrong;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.InkStrong;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);

        _grid.DefaultCellStyle.BackColor = Theme.Canvas;
        _grid.DefaultCellStyle.ForeColor = Theme.Ink;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.GridColor = Theme.Border;

        var line = Math.Max(1, _grid.Font.Height);
        _grid.RowTemplate.Height = line + 14;
        _grid.ColumnHeadersHeight = line + 22;

        // Тесни нарочно: с широки колони последната пада зад воден плъзгач, а
        // тя е тази, заради която прозорецът се отваря.
        Column("title", Lang.T("Отчет", "Report"), 280, fill: true);
        Column("company", Lang.T("Фирма", "Company"), 170);
        Column("status", Lang.T("Състояние", "Status"), 96);
        Column("period", Lang.T("Обявен период", "Declared period"), 172);
        Column("data", Lang.T("Дати в данните", "Dates in the data"), 172);
        Column("rows", Lang.T("Редове", "Rows"), 70, right: true);
        Column("outside", Lang.T("Извън периода", "Outside"), 104, right: true);

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _reports.Count) return;

            OpenRequested?.Invoke(this, _reports[e.RowIndex].Id);
            DialogResult = DialogResult.OK;
        };
    }

    private void Column(string name, string header, int width, bool fill = false, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = Theme.Scaled(220);
        }

        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        _grid.Columns.Add(column);
    }

    private Control BuildIssues(IReadOnlyList<CoverageIssue> issues)
    {
        _issues.Fill(issues.Select(issue => new IssueEntry(
            issue.Kind == CoverageProblem.Overlap ? IssueSeverity.Error : IssueSeverity.Warning,
            issue.Company + " · " + issue.KindText,
            issue.Text)));

        if (issues.Count == 0)
        {
            _issues.Fill([new IssueEntry(IssueSeverity.Info,
                Lang.T("Покритие", "Coverage"),
                Lang.T("Няма пропуски и застъпвания.", "No gaps and no overlaps."))]);
        }

        return _issues;
    }

    private Control BuildButtons()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Base.Height + 30,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var close = Theme.SecondaryButton(Lang.T("Затвори", "Close"));
        close.Dock = DockStyle.Right;
        close.Height = Theme.Base.Height + 16;
        close.FitWidth(16);
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        host.Controls.Add(close);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    private void Fill()
    {
        foreach (var report in _reports)
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells["title"].Value = report.Title;
            row.Cells["company"].Value = report.Company;
            row.Cells["status"].Value = report.Status;
            row.Cells["period"].Value = Range(report.From, report.To);
            row.Cells["data"].Value = Range(report.FirstRow, report.LastRow);
            row.Cells["rows"].Value = report.Rows.ToString(CultureInfo.InvariantCulture);

            row.Cells["outside"].Value = report.Outside == 0
                ? string.Empty
                : report.Outside.ToString(CultureInfo.InvariantCulture);

            if (report.Unknown)
            {
                row.Cells["period"].Style.BackColor = Theme.WarnSoft;
                row.Cells["period"].Style.ForeColor = Theme.Warn;
            }

            // Редовете извън обявения период не са грешка — понякога са
            // нарочни. Затова се отбелязват, но не се броят за проблем.
            if (report.Outside > 0)
            {
                row.Cells["outside"].Style.BackColor = Theme.WarnSoft;
                row.Cells["outside"].Style.ForeColor = Theme.Warn;
            }

            if (report.Draft) row.DefaultCellStyle.ForeColor = Theme.Warn;
        }

        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = null;
            _grid.ClearSelection();
        }
    }

    private static string Range(DateTime? from, DateTime? to)
    {
        if (from is null && to is null) return "—";

        return Show(from) + " – " + Show(to);
    }

    private static string Show(DateTime? date) =>
        date?.ToString(ValueConverter.BulgarianDateFormat, CultureInfo.InvariantCulture) ?? "—";

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }
}
