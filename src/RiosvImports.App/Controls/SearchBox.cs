using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace RiosvImports.App.Controls;

/// <summary>
/// Поле за търсене с икона и заоблен кант.
///
/// Windows Forms няма как да заобли текстово поле, затова полето стои в
/// нарисуван панел без собствен кант. Отстрани се появява кръстче, което
/// изчиства написаното — при търсене из четиридесет и два отчета това се
/// прави често.
/// </summary>
internal sealed class SearchBox : Card
{
    private readonly TextBox _input = new();
    private readonly AppButton _clear;

    /// <summary>
    /// Колко се чака след последния натиснат клавиш, преди да се търси.
    ///
    /// Търсенето в склада пресмята и рисува стотици редове наново. Пуснато на
    /// всеки клавиш, „170405“ беше шест пресмятания едно след друго и полето
    /// изоставаше от пръстите. Двеста милисекунди не се усещат като чакане, а
    /// думата вече е написана.
    /// </summary>
    private const int Pause = 220;

    private readonly System.Windows.Forms.Timer _typing = new() { Interval = Pause };

    /// <summary>Текстът идва отвън, не от клавиатурата — търси се веднага.</summary>
    private bool _setting;

    public SearchBox(string placeholder)
    {
        Fill = Theme.Canvas;
        Outline = Theme.Border;
        Radius = Theme.RadiusSmall;
        Padding = new Padding(36, 0, 34, 0);

        _input.BorderStyle = BorderStyle.None;
        _input.Font = Theme.Big;
        _input.PlaceholderText = placeholder;
        _input.BackColor = Theme.Canvas;
        _input.TextChanged += (_, e) =>
        {
            _clear!.Visible = _input.Text.Length > 0;

            // Писаното се изчаква, подаденото — не: копче, тест или „Изчисти“
            // искат отговора още сега, а не след пауза, която никой не чака.
            if (_setting || !_input.Focused)
            {
                _typing.Stop();
                TextChanged?.Invoke(this, e);
                return;
            }

            _typing.Stop();
            _typing.Start();
        };

        _typing.Tick += (_, _) => Flush();

        // Enter не чака паузата: човекът е казал, че е готов.
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || !_typing.Enabled) return;

            Flush();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        // Не се закача отдясно: закачането го разтяга на цялата височина и той
        // изтрива канта на картата, докато си чисти фона.
        _clear = new AppButton("✕", ButtonVariant.Ghost)
        {
            Width = Theme.Scaled(28),
            Height = Theme.Scaled(28),
            Visible = false,
            TabStop = false,
            Font = Theme.Small,
            Radius = 14
        };

        _clear.Click += (_, _) =>
        {
            _setting = true;

            try { _input.Clear(); }
            finally { _setting = false; }

            _input.Focus();
        };

        Controls.Add(_input);
        Controls.Add(_clear);

        // Размерът се задава последен — чак когато има какво да се подрежда.
        Height = Theme.Scaled(40);
        OnResize(EventArgs.Empty);
    }

    /// <summary>
    /// Текстовото поле се разполага на ръка, а не със закачане: закачено
    /// „Fill“ то заема цялата височина на картата и подсказващият текст остава
    /// извън видимото. Тук то е високо колкото един ред и стои по средата.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Присвояването на Height в конструктора вече вдига това събитие, а
        // тогава част от полетата още не съществуват. Затова се проверява:
        // подредбата се прави наново веднага щом всичко е налице.
        if (_input is null || _clear is null) return;

        var height = _input.PreferredHeight;
        _input.SetBounds(36, (Height - height) / 2, Math.Max(20, Width - 74), height);
        _clear.SetBounds(Width - 34, (Height - _clear.Height) / 2, _clear.Width, _clear.Height);
    }

    public new event EventHandler? TextChanged;

    /// <summary>Основата допуска null; тук то се приема като празен текст.</summary>
    [AllowNull]
    public override string Text
    {
        get => _input.Text;
        set
        {
            _setting = true;

            try { _input.Text = value ?? string.Empty; }
            finally { _setting = false; }
        }
    }

    /// <summary>Изчаканото търсене се пуска сега — ако има такова.</summary>
    public void Flush()
    {
        if (!_typing.Enabled) return;

        _typing.Stop();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _typing.Dispose();

        base.Dispose(disposing);
    }

    public new void Focus() => _input.Focus();

    /// <summary>Позицията на курсора — ползва се, когато текстът се допълва отвън.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectionStart
    {
        get => _input.SelectionStart;
        set => _input.SelectionStart = value;
    }

    /// <summary>Дали курсорът е вътре в полето.</summary>
    public new bool ContainsFocus => _input.Focused;

    protected override void OnGotFocus(EventArgs e)
    {
        _input.Focus();
        base.OnGotFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Outline = _input.Focused ? Theme.Accent : Theme.Border;

        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        TextRenderer.DrawText(e.Graphics, Theme.Glyph.Search, Theme.Icon,
            new Rectangle(10, 0, 24, Height), _input.Focused ? Theme.Accent : Theme.InkFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>Стрелките местят из списъка, без курсорът да напуска полето.</summary>
    public void ForwardArrowsTo(ListBox list)
    {
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Down or Keys.Up)) return;

            var next = list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
            if (next >= 0 && next < list.Items.Count) list.SelectedIndex = next;

            e.Handled = true;
            e.SuppressKeyPress = true;
        };
    }
}
