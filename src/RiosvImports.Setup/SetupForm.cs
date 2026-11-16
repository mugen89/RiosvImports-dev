using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RiosvImports.Setup;

/// <summary>
/// Прозорецът на инсталатора: къде, после докъде е стигнало, после готово.
///
/// Една форма с три изгледа, а не съветник с „Напред“: въпросът е един — папката.
/// Под нея винаги стои какво точно ще стане, с думи: ново инсталиране, обновяване
/// на място или преместване, — за да няма изненада, след като е натиснато.
/// </summary>
internal sealed class SetupForm : Form
{
    // Цветовете на програмата (Palette.Light): инсталаторът е първото, което човек вижда от нея.
    private static readonly Color Accent = Color.FromArgb(0x10, 0x49, 0x6E);
    private static readonly Color AccentHover = Color.FromArgb(0x16, 0x5A, 0x87);
    private static readonly Color AccentOff = Color.FromArgb(0x8E, 0xA9, 0xBC);
    private static readonly Color Ink = Color.FromArgb(0x13, 0x1A, 0x21);
    private static readonly Color InkStrong = Color.FromArgb(0x2B, 0x37, 0x42);
    private static readonly Color InkMuted = Color.FromArgb(0x56, 0x64, 0x70);
    private static readonly Color Border = Color.FromArgb(0xC5, 0xD0, 0xDA);
    private static readonly Color Surface = Color.FromArgb(0xF5, 0xF8, 0xFA);
    private static readonly Color Ok = Color.FromArgb(0x1B, 0x6E, 0x3F);
    private static readonly Color Warn = Color.FromArgb(0x93, 0x5C, 0x04);
    private static readonly Color Error = Color.FromArgb(0xA8, 0x23, 0x29);

    private enum Page
    {
        Choose,
        Working,
        Done,
        Failed
    }

    private readonly ExistingInstall? _existing;
    private readonly string? _payload;
    private readonly float _scale;
    private readonly float _fontFactor;
    private readonly int _width;

    private readonly TableLayoutPanel _layout;
    private readonly Label _title;
    private readonly Label _lead;
    private readonly TableLayoutPanel _folderRow;
    private readonly TextBox _folder;
    private readonly Button _browse;
    private readonly Label _message;
    private readonly CheckBox _start;
    private readonly TableLayoutPanel _progressRow;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Label _footer;
    private readonly Button _go;
    private readonly Button _cancel;
    private readonly Timer _typing;

    private InstallPlan _plan;
    private Installer? _installer;
    private Page _page = Page.Choose;

    public SetupForm(string? folder, string? payload, float? scale)
    {
        _existing = Registration.Find();
        _payload = payload;

        float system;
        using (var graphics = CreateGraphics()) system = graphics.DpiX / 96f;

        // За снимка на друг мащаб се увеличават и шрифтовете: точките се мерят по системния.
        _scale = scale ?? system;
        _fontFactor = scale.HasValue ? scale.Value / system : 1f;
        _width = S(520);

        SuspendLayout();

        Text = $"{Product.Title} {Product.Version} — инсталиране";
        Font = Make(10f);
        BackColor = Color.White;
        ForeColor = Ink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;

        _title = Text1(Make(14f, FontStyle.Regular, "Segoe UI Semibold"), Ink, S(4));
        _lead = Text1(Font, InkMuted, S(14));
        _lead.Text = "Програмата и работата ѝ стоят в една папка. Работата е в „Data“ вътре — " +
                     "копирайте нея, за да пазите резервно копие.";

        _folder = new TextBox
        {
            Font = Make(10.5f),
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, S(8), 0)
        };

        _browse = Secondary("Избери…");
        _browse.AutoSize = false;
        _browse.Size = new Size(S(104), _folder.PreferredHeight + S(4));
        _browse.Margin = Padding.Empty;
        _browse.Anchor = AnchorStyles.Right;

        _folderRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, S(10))
        };
        _folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folderRow.Controls.Add(_folder, 0, 0);
        _folderRow.Controls.Add(_browse, 1, 0);

        _message = Text1(Font, InkStrong, S(6));
        // Място за четири реда: прозорецът не подскача, докато се пише пътят.
        _message.MinimumSize = new Size(_width, (int)Math.Ceiling(Font.GetHeight() * 4) + S(2));

        _start = new CheckBox
        {
            Text = $"Пусни {Product.Title}",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, S(4), 0, S(6)),
            Visible = false
        };

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Size = new Size(S(120), S(8)),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, S(10), 0)
        };

        _status = Text1(Font, InkMuted, 0);
        _status.MaximumSize = new Size(_width - S(130), 0);
        _status.Anchor = AnchorStyles.Left;

        _progressRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, S(2), 0, S(8)),
            Visible = false
        };
        _progressRow.Controls.Add(_progress, 0, 0);
        _progressRow.Controls.Add(_status, 1, 0);

        _go = Primary("Инсталирай");
        _cancel = Secondary("Откажи");

        _footer = Text1(Make(8.75f), InkMuted, 0);
        _footer.Text = $"{Product.Title} {Product.Version}";
        _footer.Anchor = AnchorStyles.Left;
        _footer.MaximumSize = Size.Empty;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_go);
        _cancel.Margin = new Padding(S(8), 0, 0, 0);
        _go.Margin = Padding.Empty;

        var bottom = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, S(10), 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_footer, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        _layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(28), S(24), S(28), S(20)),
            Location = Point.Empty,
            BackColor = Color.White
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _width));

        foreach (var control in new Control[] { _title, _lead, _folderRow, _message, _start, _progressRow, bottom })
            _layout.Controls.Add(control);

        Controls.Add(_layout);

        AcceptButton = _go;
        CancelButton = _cancel;

        _typing = new Timer { Interval = 250 };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Replan();
        };

        _folder.Text = folder ?? _existing?.Root ?? Product.DefaultFolder;
        _plan = InstallPlan.For(_folder.Text, _existing, InstallPlan.ProtectedFolders());

        _folder.TextChanged += (_, _) =>
        {
            if (_page != Page.Choose) return;
            _typing.Stop();
            _typing.Start();
        };

        _browse.Click += (_, _) => Browse();
        _go.Click += (_, _) => Go();
        _cancel.Click += (_, _) => Close();

        View(Page.Choose);
        ResumeLayout(true);
        Fit();
    }

    /// <summary>0 — инсталирано, 1 — не стана, 2 — отказано.</summary>
    public int ExitCode { get; private set; } = 2;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Маркиран целият път изчезва с първия натиснат клавиш — курсорът е накрая.
        _folder.SelectionStart = _folder.TextLength;
        _folder.SelectionLength = 0;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Наполовина инсталирана програма е по-лоша от неинсталирана: докато
        // Velopack работи, прозорецът не се затваря.
        if (_page == Page.Working)
        {
            e.Cancel = true;
            return;
        }

        if (_page == Page.Done && _start.Checked && _installer is not null) Launch(_plan.Folder);

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _typing.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>Снимка на прозореца в PNG — за преглед на подредбата без инсталиране.</summary>
    public void Snapshot(string png, string page)
    {
        switch (page)
        {
            case "working":
                View(Page.Working);
                _status.Text = "Инсталира се…";
                break;

            case "done":
                View(Page.Done);
                break;

            case "failed":
                _plan = InstallPlan.For(_folder.Text, _existing, InstallPlan.ProtectedFolders());
                View(Page.Failed, "Инсталаторът на Velopack спря с код 1.",
                    Path.Combine(Product.ProfileFolder, InstallData.BackupsFolder, "before-install-2026-11-16-101500"),
                    Path.Combine(Path.GetTempPath(), "RIOSV Imports Setup", "20261116-101500-000", "setup.log"));
                break;
        }

        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        Show();
        Application.DoEvents();
        Fit();
        Application.DoEvents();

        using (var bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        Hide();
    }

    private void Replan()
    {
        _plan = InstallPlan.For(_folder.Text, _existing, InstallPlan.ProtectedFolders());
        View(Page.Choose);
    }

    private void Browse()
    {
        var picked = FolderPicker.Pick(this, "Къде да се инсталира RIOSV Imports", _folder.Text);

        if (picked is null) return;

        // В полето — папката, в която програмата наистина ще влезе.
        var plan = InstallPlan.For(picked, _existing, InstallPlan.ProtectedFolders());

        _folder.Text = plan.Valid ? plan.Folder : picked;
        _typing.Stop();
        Replan();
    }

    private void Go()
    {
        switch (_page)
        {
            case Page.Done:
                Close();
                return;

            case Page.Failed:
                Replan();
                return;

            case Page.Working:
                return;
        }

        _typing.Stop();
        _plan = InstallPlan.For(_folder.Text, _existing, InstallPlan.ProtectedFolders());

        if (!_plan.Valid)
        {
            View(Page.Choose);
            return;
        }

        var installer = new Installer(_plan, Product.ProfileFolder, Say, _payload);
        _installer = installer;

        View(Page.Working);

        Task.Run(installer.Run).ContinueWith(task =>
        {
            var outcome = task.IsFaulted ? InstallOutcome.Failed : task.Result;

            switch (outcome)
            {
                case InstallOutcome.Done:
                    ExitCode = 0;
                    View(Page.Done);
                    break;

                case InstallOutcome.Busy:
                    View(Page.Choose);
                    Tell(installer.Problem + $" Натиснете „{_go.Text}“ отново, щом я затворите.", Warn);
                    break;

                default:
                    ExitCode = 1;
                    View(Page.Failed, task.IsFaulted ? task.Exception?.GetBaseException().Message ?? "" : installer.Problem,
                        installer.Backup, File.Exists(installer.LogPath) ? installer.LogPath : null);
                    break;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Say(string text)
    {
        if (IsHandleCreated) BeginInvoke(new Action(() => _status.Text = text));
    }

    private void View(Page page, string problem = "", string? backup = null, string? log = null)
    {
        _page = page;

        var choosing = page == Page.Choose || page == Page.Failed;

        _lead.Visible = page == Page.Choose;
        _folderRow.Visible = page != Page.Done;
        _folder.Enabled = _browse.Enabled = choosing;
        _start.Visible = page == Page.Done;
        _progressRow.Visible = page == Page.Working;
        _cancel.Visible = page != Page.Done;
        _cancel.Enabled = page != Page.Working;
        _go.Enabled = page != Page.Working;

        switch (page)
        {
            case Page.Choose:
                _title.Text = "Къде да се инсталира RIOSV Imports?";
                _cancel.Text = "Откажи";
                _go.Text = _plan.Kind switch
                {
                    PlanKind.Update => "Обнови",
                    PlanKind.Move => "Премести",
                    _ => "Инсталирай"
                };
                _go.Enabled = _plan.Valid;
                Describe();
                break;

            case Page.Working:
                _title.Text = _plan.Kind switch
                {
                    PlanKind.Update => "Обновява се RIOSV Imports…",
                    PlanKind.Move => "Мести се RIOSV Imports…",
                    _ => "Инсталира се RIOSV Imports…"
                };
                Tell("Не затваряйте прозореца — отнема около минута. Програмата и работата ѝ се проверяват накрая.",
                    InkMuted);
                break;

            case Page.Done:
                _title.Text = "Готово";
                _go.Text = "Затвори";
                Tell(Finished(), Ok);
                break;

            case Page.Failed:
                _title.Text = "Инсталирането не стана";
                _go.Text = "Опитай пак";
                _cancel.Text = "Затвори";

                var text = problem.Length > 0 ? problem : "Не е ясно защо.";
                text += backup is not null
                    ? $"{Environment.NewLine}Работата е запазена: копие има в „{backup}“."
                    : $"{Environment.NewLine}Работата не е пипната.";
                if (log is not null) text += $"{Environment.NewLine}Подробности: {log}";

                Tell(text, Error);
                break;
        }

        Fit();
    }

    /// <summary>Какво ще стане в избраната папка — преди да е натиснато.</summary>
    private void Describe()
    {
        var plan = _plan;

        if (!plan.Valid)
        {
            Tell(plan.Reason, Error);
            return;
        }

        // Всеки път на свой ред: пренесен по средата, пътят се чете трудно.
        var folder = plan.Folder;
        var data = Path.Combine(folder, InstallData.DataFolder);
        var line = Environment.NewLine;

        switch (plan.Kind)
        {
            case PlanKind.Update when plan.Existing is not null:
                Tell($"Тук е инсталирана {Named(plan.Existing)}. Ще бъде обновена до {Product.Version}." +
                     $"{line}Работата остава: „{data}“.", Ok);
                break;

            case PlanKind.Update:
                Tell($"Тук вече има RIOSV Imports. Ще бъде сложена наново ({Product.Version})." +
                     $"{line}Работата остава: „{data}“.", Ok);
                break;

            case PlanKind.Move:
                Tell($"{Named(plan.Existing!)} е инсталирана в:{line}„{plan.Existing!.Root}“." +
                     $"{line}Ще се премести тук заедно с работата си, а старата папка се маха.", Warn);
                break;

            default:
                var text = plan.Extended
                    ? $"Избраната папка не е празна — програмата влиза в своя подпапка:{line}„{folder}“."
                    : $"Ще се инсталира в „{folder}“.";

                text += plan.KeepsWork
                    ? $"{line}Там вече има работа на RIOSV Imports — програмата продължава с нея."
                    : $"{line}Работата ѝ ще е в „{data}“.";

                Tell(text, Ok);
                break;
        }
    }

    private string Finished()
    {
        var folder = _plan.Folder;
        var text = $"RIOSV Imports {Product.Version} е в „{folder}“.{Environment.NewLine}" +
                   $"Работата ѝ е в „{Path.Combine(folder, InstallData.DataFolder)}“.";

        if (_plan.Kind == PlanKind.Move && _plan.Existing is not null)
            text += $"{Environment.NewLine}Старата папка „{_plan.Existing.Root}“ е махната.";

        if (_installer?.Backup is { } backup)
            text += $"{Environment.NewLine}Копие на работата отпреди инсталирането: „{backup}“.";

        return text;
    }

    /// <summary>„RIOSV Imports 1.3.1“ — или само името, ако Windows не помни номера.</summary>
    private static string Named(ExistingInstall existing) =>
        existing.Version.Length > 0 ? $"{Product.Title} {existing.Version}" : Product.Title;

    private static void Launch(string folder)
    {
        try
        {
            var current = Path.Combine(folder, "current");

            Process.Start(new ProcessStartInfo(Path.Combine(current, Product.MainExe))
            {
                WorkingDirectory = current,
                UseShellExecute = true
            });
        }
        catch
        {
            // Връзката на работния плот и в „Старт“ я има — програмата се пуска и оттам.
        }
    }

    private void Tell(string text, Color colour)
    {
        _message.Text = text;
        _message.ForeColor = colour;
    }

    /// <summary>Прозорецът е колкото съдържанието — по-висок при повече редове.</summary>
    private void Fit()
    {
        _layout.PerformLayout();
        var size = _layout.GetPreferredSize(Size.Empty);
        ClientSize = new Size(size.Width, size.Height);
    }

    private int S(int pixels) => (int)Math.Round(pixels * _scale);

    private Font Make(float points, FontStyle style = FontStyle.Regular, string family = "Segoe UI") =>
        new Font(family, points * _fontFactor, style);

    private Label Text1(Font font, Color colour, int bottom) => new Label
    {
        AutoSize = true,
        Font = font,
        ForeColor = colour,
        MaximumSize = new Size(_width, 0),
        Margin = new Padding(0, 0, 0, bottom),
        UseMnemonic = false
    };

    private Button Primary(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(S(112), S(34)),
            Padding = new Padding(S(10), 0, S(10), 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = Make(10f, FontStyle.Bold),
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };

        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.EnabledChanged += (_, _) => button.BackColor = button.Enabled ? Accent : AccentOff;

        return button;
    }

    private Button Secondary(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(S(96), S(34)),
            Padding = new Padding(S(8), 0, S(8), 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = InkStrong,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };

        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Surface;

        return button;
    }
}
