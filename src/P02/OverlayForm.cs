using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace P02;

/// <summary>
/// The small readout that sits over the game.
///
/// Composed by hand into a per-pixel alpha window rather than painted into a
/// normal one. A colour-keyed window can only be fully opaque or fully gone,
/// which costs the soft edges on everything - and the key colour itself leaks
/// into anything antialiased against it, which is what turned the whole readout
/// magenta. Here every pixel carries its own alpha, so text can be haloed
/// rather than outlined, bars can sit on a bed that is dark without being
/// black, and there is no colour that must never be drawn.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int Pad = 11;
    private const int RowH = 24;
    private const int BarH = 11;
    private const int NameW = 34;
    private const int ValueW = 54;

    /// <summary>
    /// One font for the alert, used to measure it and to draw it.
    ///
    /// Measuring with one and drawing with another is how text ends up cut off
    /// mid-word, and this readout has done that more than once. It is also
    /// bigger than the rest: an alert is the one thing here that has to be read
    /// rather than glanced at.
    /// </summary>
    private static readonly Font AlertFont = new("Segoe UI Semibold", 9f);

    private const int AlertLine = 16;

    private readonly System.Windows.Forms.Timer _fade = new();

    private string _lifeKey = "";
    private string _manaKey = "";

    private GlobeReading _life = new("Life", 0, false);
    private GlobeReading _mana = new("Mana", 0, false, "off");
    private double _lifeTrigger = 0.5;
    private double _manaTrigger = 0.3;

    /// <summary>
    /// Low Life setup: the first row is showing shield, not life, since
    /// shield is the number actually worth watching for that build - the
    /// reading and trigger passed in are already shield's, this only changes
    /// the label and tally letters drawn beside them.
    /// </summary>
    private bool _firstIsShield;
    private bool _armed;
    private bool _firing;
    private int _fired;
    private string _firedPool = "Life";
    private int _mpWidth;
    private int _lifeFires;
    private int _manaFires;
    private bool _inCombat;
    private string _detail = "";
    private string _alert = "";

    private Point _grabbedAt;
    private Point _wasAt;
    private bool _dragging;

    /// <summary>Raised when a drag finishes, so the position can be saved.</summary>
    public event Action? Moved;

    /// <summary>Raised when the menu changes something worth remembering.</summary>
    public event Action<bool, bool>? OptionsChanged;

    /// <summary>Raised when the menu asks for the default position back.</summary>
    public event Action? ResetAsked;

    /// <summary>Raised when this spot should be remembered for where the character is.</summary>
    public event Action? RememberAsked;

    /// <summary>Raised when one of the three saved places is chosen.</summary>
    public event Action<int>? SlotChosen;

    /// <summary>Raised when following the panels is switched on or off.</summary>
    public event Action<bool>? SlotAutoChanged;

    /// <summary>Whether the position is chosen by where the character is.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SlotAuto { get; set; }

    /// <summary>Which of the three is in use, for the tick in the menu.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Slot { get; set; }

    /// <summary>
    /// Set while the readout is anchored to something in the game, so a drag
    /// cannot quietly fight the thing that keeps putting it back.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Locked { get; set; }

    /// <summary>Whether the mouse passes straight through to the game.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            if (value) _ctrlWatch.Start(); else _ctrlWatch.Stop();
            ApplyClickThrough();
        }
    }

    private void ApplyClickThrough()
    {
        if (!IsHandleCreated) return;

        bool wanted = _clickThrough && !Native.CtrlHeld;

        // Asked of the window, not of a variable we kept.
        //
        // The cached flag went stale the moment Windows rebuilt the handle -
        // which it does for reasons of its own - and the rebuilt window comes
        // back without the transparent bit, because the style is declared fresh
        // in CreateParams. The flag still said "already click-through", so the
        // work was skipped and the setting silently stopped applying. A window
        // knows whether clicks pass through it; there is no reason to remember
        // it on its behalf.
        if (wanted == Native.IsClickThrough(Handle)) { _throughNow = wanted; return; }

        _throughNow = wanted;
        Native.ClickThrough(Handle, wanted);
    }

    private bool _clickThrough;
    private bool _throughNow;

    /// <summary>
    /// Watches for Ctrl while clicks are passing through.
    ///
    /// A window that ignores the mouse cannot be told to stop ignoring it, so
    /// the escape has to come from outside the mouse. Holding Ctrl makes it
    /// solid again for as long as it is held, which is enough to right-click
    /// the menu and turn the whole thing off.
    /// </summary>
    private readonly System.Windows.Forms.Timer _ctrlWatch = new();

    public OverlayForm()
    {
        Text = "QytOCR";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(292, 100);

        _ctrlWatch.Interval = 120;
        _ctrlWatch.Tick += (_, _) => ApplyClickThrough();

        _fade.Interval = 260;
        _fade.Tick += (_, _) => { _fade.Stop(); _firing = false; Render(); };

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || Locked) return;
            _dragging = true;
            _grabbedAt = Cursor.Position;
            _wasAt = Location;
            Cursor = Cursors.SizeAll;
        };
        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            var now = Cursor.Position;
            Location = new Point(_wasAt.X + now.X - _grabbedAt.X,
                                 _wasAt.Y + now.Y - _grabbedAt.Y);
            Render();
        };
        // Ctrl and right-click, deliberately. The readout sits over a game
        // where every ordinary click belongs to the game, and a menu that opens
        // on a plain right-click would open by accident all evening.
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && ModifierKeys == Keys.Control)
            {
                ShowMenu(e.Location);
                return;
            }

            if (!_dragging) return;
            _dragging = false;
            Cursor = Cursors.Default;
            Moved?.Invoke();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                         | Native.WS_EX_NOACTIVATE;
            return p;
        }
    }

    /// <summary>Taking focus off the game to show a readout would be its own bug.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>The keys each pool would press, shown beside its trigger.</summary>
    public void SetKeys(string life, string mana)
    {
        _lifeKey = life;
        _manaKey = mana;
    }

    public void Show(GlobeReading life, GlobeReading mana,
                     double lifeTrigger, double manaTrigger, bool firstIsShield = false)
    {
        _life = life;
        _mana = mana;
        _lifeTrigger = lifeTrigger;
        _manaTrigger = manaTrigger;
        _firstIsShield = firstIsShield;

        _detail = life.Note.Length > 0 ? life.Note : Source(life.TextRaw);

        FitHeight();
        Render();
    }

    /// <summary>
    /// A line under everything else, for something that cannot wait until the
    /// player next looks at the window - which, mid-map, is never.
    /// </summary>
    public void SetAlert(string text)
    {
        if (text == _alert) return;
        _alert = text;
        FitHeight();
        Render();
    }

    public void SetArmed(bool armed)
    {
        _armed = armed;
        Render();
    }

    public void SetFightCount(int life, int mana, bool inCombat)
    {
        if (life == _lifeFires && mana == _manaFires && inCombat == _inCombat) return;
        _lifeFires = life;
        _manaFires = mana;
        _fired = life + mana;
        _inCombat = inCombat;
        Render();
    }

    /// <summary>Blinks when a key is actually sent.</summary>
    public void Fired(string pool = "Life")
    {
        // Which pool it was, so the flash is that pool's colour. A single
        // coloured dot that means "something fired" is one glance short of
        // useful when two flasks can fire.
        _firedPool = pool;
        _firing = true;
        Render();
        _fade.Stop();
        _fade.Start();
    }

    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _manaItem;
    private nint _beforeMenu;

    /// <summary>Raised when the mana row is turned on or off from the menu.</summary>
    public event Action<bool>? ManaShownChanged;
    private ToolStripMenuItem? _lockItem;
    private ToolStripMenuItem? _throughItem;
    private ToolStripMenuItem[]? _slotItems;
    private ToolStripMenuItem? _autoItem;

    /// <summary>
    /// The options menu, built once and kept.
    ///
    /// It used to be built on each open and disposed from its own Closed event
    /// - which runs before the click that closed it has finished being
    /// handled, so choosing anything from it crashed on the disposed menu. A
    /// menu is cheap to keep and nothing is saved by throwing it away.
    /// </summary>
    private void ShowMenu(Point at)
    {
        if (_menu is null)
        {
            _lockItem = new ToolStripMenuItem("Lock position")
            {
                CheckOnClick = true,
                ToolTipText = "Stops it being dragged by accident.",
            };
            _lockItem.Click += (_, _) =>
            {
                Locked = _lockItem.Checked;
                OptionsChanged?.Invoke(Locked, ClickThrough);
            };

            _throughItem = new ToolStripMenuItem("Click through")
            {
                CheckOnClick = true,
                ToolTipText = "Clicks land in the game instead of on the readout. "
                              + "Hold Ctrl to make it solid again for as long as you hold "
                              + "it, which is how you get back to this menu.",
            };
            _throughItem.Click += (_, _) =>
            {
                ClickThrough = _throughItem.Checked;
                OptionsChanged?.Invoke(Locked, ClickThrough);
            };

            _manaItem = new ToolStripMenuItem("Show mana")
            {
                CheckOnClick = true,
                ToolTipText = "Whether the mana bar appears here at all. Plenty of "
                              + "builds never touch a mana flask, and a row that never "
                              + "changes is a row in the way.",
            };
            _manaItem.Click += (_, _) => SetManaShown(_manaItem.Checked);

            var remember = new ToolStripMenuItem("Remember this spot")
            {
                ToolTipText = "Saves where the readout is now, for wherever your "
                              + "character is standing now. Do it once with your panels "
                              + "closed, once with the inventory open, once with the "
                              + "character sheet open - it works out which is which.",
            };
            remember.Click += (_, _) => RememberAsked?.Invoke();

            var reset = new ToolStripMenuItem("Move back to the corner");
            reset.Click += (_, _) => ResetAsked?.Invoke();

            // Three places, because the game slides the character sideways
            // when a panel opens and one remembered spot cannot serve three
            // layouts. Choosing one moves there; dragging saves where you put
            // it, into whichever is chosen.
            _slotItems =
            [
                new ToolStripMenuItem("Left"),
                new ToolStripMenuItem("Middle"),
                new ToolStripMenuItem("Right"),
            ];

            for (int i = 0; i < _slotItems.Length; i++)
            {
                int which = i;
                _slotItems[i].ToolTipText =
                    "Go to this position. Drag the readout afterwards and it is "
                    + "remembered here.";
                _slotItems[i].Click += (_, _) => SlotChosen?.Invoke(which);
            }

            _autoItem = new ToolStripMenuItem("Follow the panels")
            {
                CheckOnClick = true,
                ToolTipText = "Opening your inventory slides the character one way and "
                              + "the character sheet the other. This picks Left, Middle "
                              + "or Right to match, from where he actually is.",
            };
            _autoItem.Click += (_, _) => SlotAutoChanged?.Invoke(_autoItem.Checked);

            var places = new ToolStripMenuItem("Position");
            places.DropDownItems.Add(_autoItem);
            places.DropDownItems.Add(new ToolStripSeparator());
            places.DropDownItems.AddRange(_slotItems);

            _menu = new ContextMenuStrip { ShowImageMargin = false };
            // Whatever had the foreground before the menu opened gets it back.
            // Forcing the menu forward is what makes it usable at all from a
            // window that never activates, and leaving the game unfocused
            // afterwards would quietly stop anything firing - "only fire while
            // the game is focused" is doing its job and would look like a bug.
            _menu.Closed += (_, _) =>
            {
                if (_beforeMenu != 0) Native.ForceForeground(_beforeMenu);
                _beforeMenu = 0;
            };

            _menu.Items.Add(remember);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(places);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_manaItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_lockItem);
            _menu.Items.Add(_throughItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(reset);
        }

        _lockItem!.Checked = Locked;
        _throughItem!.Checked = ClickThrough;
        for (int i = 0; i < _slotItems!.Length; i++) _slotItems[i].Checked = i == Slot;
        _autoItem!.Checked = SlotAuto;
        _manaItem!.Checked = ShowMana;
        _beforeMenu = Native.GetForegroundWindow();
        _menu.Show(this, at);

        // The readout is a WS_EX_NOACTIVATE window, which is right for a thing
        // that sits over a game and must never steal its focus - and it leaves
        // a menu opened from it in an odd half-state. It draws, it highlights,
        // and a click on an item can be swallowed on the way to the handler.
        // Handing the menu the foreground for as long as it is open costs
        // nothing, because the menu closes the moment you choose something.
        Native.ForceForeground(_menu.Handle);
    }

    /// <summary>
    /// Which source decided, without repeating its numbers.
    ///
    /// The numbers are already drawn under the bar. Printing them again beside
    /// "disarmed" said the same thing twice in a readout whose whole point is
    /// being small.
    /// </summary>
    private static string Source(string raw) =>
        raw.Length == 0 ? "globe pixels"
        : raw.StartsWith("memory", StringComparison.Ordinal) ? "memory"
        : raw.StartsWith("numbers", StringComparison.Ordinal) ? "numbers"
        : raw;

    /// <summary>The bare current/maximum out of whatever the source reported.</summary>
    private static string Exact(string raw)
    {
        int slash = raw.IndexOf('/');
        if (slash < 0) return "";

        int from = slash;
        while (from > 0 && (char.IsDigit(raw[from - 1]) || raw[from - 1] == ',')) from--;

        int to = slash + 1;
        while (to < raw.Length && (char.IsDigit(raw[to]) || raw[to] == ',')) to++;

        return to - from > 3 ? raw[from..to] : "";
    }

    /// <summary>
    /// Mana on the readout, lit the colour it is in the game.
    ///
    /// Both bars were drawn green, which makes the readout a pair of identical
    /// green bars sitting under a red globe and a blue one. Brighter than the
    /// game's own blue, because this is read at a glance over whatever the
    /// screen happens to be showing.
    /// </summary>
    private static readonly Color ManaBlue = Color.FromArgb(86, 142, 226);

    /// <summary>Life, lit the colour of its own globe.</summary>
    private static readonly Color LifeRed = Color.FromArgb(214, 86, 78);

    /// <summary>
    /// Turns the mana row on or off, from wherever the request came.
    ///
    /// Written down rather than left in the menu handler because the menu is
    /// the part that was suspect: it opens from a window that never activates,
    /// and a click on an item there can go missing. Anything that goes wrong
    /// now says so in the log instead of simply not happening.
    /// </summary>
    private void SetManaShown(bool show)
    {
        Log.Write($"overlay: mana row turned {(show ? "on" : "off")}");
        ShowMana = show;
        FitHeight();
        Render();
        ManaShownChanged?.Invoke(show);
    }

    /// <summary>Whether the mana row is wanted at all.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowMana { get; set; } = true;

    /// <summary>A globe that is not watched has nothing to say, so it takes no room.</summary>
    /// <summary>Whether mana is being watched at all, which the row follows.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ManaWatched { get; set; } = true;

    /// <summary>
    /// A row nobody asked for takes no space.
    ///
    /// It used to decide this by whether mana had anything to say - a reading
    /// noted "off" meant nothing was watching it. That stopped being true the
    /// moment memory started working: memory reads life, mana and shield
    /// together whether or not you watch them, so mana always has a live
    /// reading now and the row never went away. Somebody with "Watch my mana"
    /// unchecked was still looking at a mana bar.
    ///
    /// Whether it is watched is the actual question, so that is what is asked.
    /// </summary>
    private bool ManaShown => ShowMana && ManaWatched
                              && !(_mana.Note == "off" && !_mana.Ok);

    /// <summary>Whether life is being watched at all, which the row follows.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool LifeWatched { get; set; } = true;

    /// <summary>
    /// Same question as ManaShown, asked of life. Life never had the on/off
    /// menu toggle mana has - most builds do watch it - but it had exactly
    /// the same bug: the row was drawn unconditionally, so "Watch my life"
    /// unchecked still left a life bar sitting there with nothing behind it.
    /// </summary>
    private bool LifeShown => LifeWatched && !(_life.Note == "off" && !_life.Ok);

    private void FitHeight()
    {
        int h = Pad + (LifeShown ? RowH : 0) + (ManaShown ? RowH : 0) + 26
                + (_alert.Length > 0 ? AlertLine + 8 : 0) + Pad;

        // The alert wraps within the width the readout already has. Growing the
        // window to fit a sentence made the whole thing bigger for the sake of
        // a message that is gone in five seconds.
        if (_alert.Length > 0) h += (AlertLines() - 1) * AlertLine;

        if (ClientSize.Height != h) ClientSize = new Size(ClientSize.Width, h);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ExcludeFromCapture(Handle, false);

        // A rebuilt handle is a window with none of this applied to it: the
        // extended style is declared from scratch in CreateParams, so anything
        // stamped on afterwards is gone. Everything that lives in the window
        // rather than in this object has to be put back.
        _throughNow = false;
        ApplyClickThrough();
        if (_clickThrough) _ctrlWatch.Start();
        Log.Write($"overlay: ready - click through {(_clickThrough ? "on" : "off")}, "
                  + $"{(Locked ? "locked" : "movable")}, mana row "
                  + $"{(ShowMana ? "on" : "off")}");
        Render();
    }

    // A layered window never receives WM_PAINT for its contents - everything
    // arrives through UpdateLayeredWindow instead.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Render();
    }

    /// <summary>
    /// Draws the whole readout and hands it to the window as one image.
    ///
    /// It draws straight into a DIB the window can be handed directly, in
    /// premultiplied form. UpdateLayeredWindow reads the colour channels as
    /// already multiplied by the alpha, so an ordinary ARGB bitmap - where they
    /// are not - comes out washed and colour-shifted, which is what left the
    /// whole readout pink even after the key colour was gone.
    /// </summary>
    private void Render()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (!IsHandleCreated || w <= 0 || h <= 0) return;

        var info = new Native.BITMAPINFO();
        info.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        info.bmiHeader.biWidth = w;
        info.bmiHeader.biHeight = -h;   // negative: top-down, like everything else here
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0;   // BI_RGB

        nint screen = Native.GetDC(0);
        nint mem = Native.CreateCompatibleDC(screen);
        nint dib = Native.CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
        nint old = 0;
        try
        {
            if (dib == 0) return;
            old = Native.SelectObject(mem, dib);

            using (var frame = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Subpixel rendering has no ground to antialias against here and
                // leaves coloured fringes on the glyphs. Grey keeps the alpha honest.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                Compose(g);
            }

            var size = new Native.SIZE { Cx = w, Cy = h };
            var src = new Native.POINT { X = 0, Y = 0 };
            var dst = new Native.POINT { X = Left, Y = Top };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src,
                                       0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (old != 0) Native.SelectObject(mem, old);
            if (dib != 0) Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            Native.ReleaseDC(0, screen);
        }
    }

    private void Compose(Graphics g)
    {
        // Barely there, but not nothing: a fully transparent pixel passes the
        // mouse through to the game, and the whole thing has to stay draggable.
        using (var ghost = new SolidBrush(Color.FromArgb(8, 0, 0, 0)))
        using (var path = Rounded(new Rectangle(0, 0, ClientSize.Width - 1,
                                                ClientSize.Height - 1), 10))
            g.FillPath(ghost, path);

        int y = Pad;
        if (LifeShown)
        {
            Row(g, _firstIsShield ? "Shield" : "Life", _life, _lifeTrigger, _lifeKey, y,
                    Theme.Good, LifeRed, _lifeFires);
            y += RowH;
        }

        if (ManaShown)
        {
            Row(g, "Mana", _mana, _manaTrigger, _manaKey, y, ManaBlue, ManaBlue,
                    _manaFires);
            y += RowH;
        }

        // Mana's tally sits under life's percentage, in the same column, a
        // couple of pixels in - so the two read as one stack down the right
        // rather than as two things that happen to be near each other. It is
        // here rather than on mana's own row because that row can be switched
        // off, and the count still matters when it is.
        _mpWidth = 0;
        if (!ManaShown && (_manaFires > 0 || _inCombat))
        {
            string mp = $"MP {_manaFires}";
            int w = (int)Math.Ceiling(g.MeasureString(mp, Theme.Big).Width);
            _mpWidth = w + 8;
            Glyph(g, mp, _inCombat ? ManaBlue : Theme.Dim, Theme.Big,
                  ClientSize.Width - Pad - 2 - w, y - 4);
        }

        Status(g, y + 4);

        if (_alert.Length > 0)
        {
            // Loud on purpose. Everything else here is meant to be glanceable
            // and ignorable; this one is meant to interrupt.
            int lines = AlertLines();
            var box = new Rectangle(Pad - 4, y + 26,
                                    ClientSize.Width - 2 * Pad + 8,
                                    AlertLine + 6 + (lines - 1) * AlertLine);

            using (var back = new SolidBrush(Color.FromArgb(210, 150, 30, 26)))
            using (var path = Rounded(box, 5))
                g.FillPath(back, path);

            int at = box.Y + 3;
            foreach (string part in Wrap(_alert, box.Width - 14))
            {
                Glyph(g, part, Color.White, AlertFont, box.X + 7, at);
                at += AlertLine;
            }
        }
    }

    /// <summary>How many lines the alert needs at the width it has.</summary>
    private int AlertLines() => Wrap(_alert, ClientSize.Width - 2 * Pad - 6).Count;

    /// <summary>
    /// Breaks a sentence to a width, on spaces.
    ///
    /// The alternative was making the window wider, which grows the readout for
    /// the sake of a message that is gone in five seconds.
    /// </summary>
    private List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;

        using var g = CreateGraphics();
        string line = "";

        foreach (string word in text.Split(' '))
        {
            string tried = line.Length == 0 ? word : line + " " + word;
            if (g.MeasureString(tried, AlertFont).Width > width && line.Length > 0)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = tried;
            }
        }

        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private void Row(Graphics g, string name, GlobeReading r, double trigger,
                     string key, int y, Color full, Color tallyInk,
                     int fires)
    {
        // The tally IS the label.
        //
        // Reserving a column for it pushed the bar across and made it shorter,
        // which is the one thing the readout can least afford to lose - the bar
        // is the part you read at a glance, and it was the right size already.
        // "HP" says everything the word "Life" said in half the room, so the
        // count goes where the word was and the bar keeps every pixel it had.
        string tag = name == "Life" ? "HP" : name == "Shield" ? "ES" : "MP";
        string tally = fires > 0 || _inCombat ? $"{tag} {fires}" : tag;
        Glyph(g, tally, _inCombat ? tallyInk : Theme.Dim, Theme.UiBold, Pad, y + 1);

        int barX = Pad + NameW;
        var bar = new Rectangle(barX, y + 4, ClientSize.Width - barX - ValueW - Pad, BarH);

        int radius = BarH / 2;

        // A bed under the bar, so an empty one reads as an empty bar rather
        // than as nothing at all.
        using (var bed = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
        using (var path = Rounded(bar, radius))
            g.FillPath(bed, path);

        // A note - "numbers not trusted", "no effect - waiting" and the like
        // - used to blank the bar and print the bare word "held" in its
        // place, for every one of them, including the ones that still carry
        // a perfectly real reading (Ok is true; a bridged safety net has
        // already fired on it if it needed to). That made a pool that is
        // being watched correctly, just not confidently enough to act on its
        // own, look indistinguishable from one nothing is reading at all -
        // "nonstop held" on a HUD that never actually stopped tracking mana.
        // Ok already means there is a real number to show; the note earns a
        // warning colour, not a blank bar.
        //
        // Except when that number is not real right now. "Stale and real
        // beats fresh and wrong" is the right call for a firing decision
        // bridging a brief gap, and the wrong one for a display that just
        // sat on a confident 100% while shield had actually been carved
        // down to nothing during a real reading gap - "last known X%" is
        // MonitorEngine's own marker for exactly that carried-forward,
        // possibly-long-stale value, and this is the one place showing it
        // as though it were current would have cost someone their character.
        bool stale = MonitorEngine.IsStaleCarry(r.TextRaw);
        bool held = r.Note.Length > 0 && stale;
        if (r.Ok && !stale)
        {
            var inner = new Rectangle(bar.X + 1, bar.Y + 1, bar.Width - 2, bar.Height - 2);
            int w = (int)Math.Round(inner.Width * Math.Clamp(r.Fraction, 0, 1));
            if (w > 2)
            {
                var fill = r.Fraction < trigger ? Theme.Bad : full;
                var lit = new Rectangle(inner.X, inner.Y, w, inner.Height);
                using var brush = new LinearGradientBrush(
                    new Rectangle(lit.X, lit.Y - 1, lit.Width, lit.Height + 2),
                    ControlPaint.Light(fill, 0.35f), fill, LinearGradientMode.Vertical);
                using var path = Rounded(lit, Math.Min(radius, w / 2));
                g.FillPath(brush, path);
            }

            // Where the trigger sits, so a glance says how much room is left
            // rather than only where the level is.
            int tx = inner.X + (int)(inner.Width * Math.Clamp(trigger, 0, 1));
            using var tick = new Pen(Color.FromArgb(200, 255, 255, 255));
            g.DrawLine(tick, tx, bar.Y - 1, tx, bar.Bottom + 1);
        }

        using (var edge = new Pen(Color.FromArgb(90, 255, 255, 255)))
        using (var path = Rounded(bar, radius))
            g.DrawPath(edge, path);

        string right = !r.Ok ? (r.Note.Length > 0 ? r.Note : "--")
                     : held ? "held"
                     : $"{r.Fraction * 100:0}%";
        var colour = !r.Ok ? Theme.Dim
                   : held ? Theme.Warn
                   : r.Fraction < trigger ? Theme.Bad : Theme.Text;
        Glyph(g, right, colour, Theme.UiBold, bar.Right + 8, y + 1);


    }

    private void Status(Graphics g, int y)
    {
        Glyph(g, _armed ? "ARMED" : "disarmed",
              _armed ? Theme.Armed : Theme.Dim, Theme.UiBold, Pad, y);

        string detail = _detail.Length > 22 ? _detail[..22] : _detail;
        var detailColour = _life.Note.Length > 0 || !_life.FromText ? Theme.Warn
                         : detail == "memory" ? Theme.Good
                         : Theme.Dim;
        Glyph(g, detail, detailColour, Theme.Small, Pad + 62, y + 1);

        if (_dragging)
        {
            Glyph(g, $"{Left}, {Top}", Theme.Accent, Theme.Small,
                  Pad, y + 1);
            return;
        }

        if (_firing)
        {
            // A dot rather than a word: it is lit for a quarter of a second and
            // only has to be noticed, not read.
            var lit = _firedPool == "Mana" ? ManaBlue : LifeRed;
            using var glow = new SolidBrush(Color.FromArgb(70, lit));
            g.FillEllipse(glow, ClientSize.Width - Pad - 44 - _mpWidth, y + 1, 15, 15);
            using var dot = new SolidBrush(lit);
            g.FillEllipse(dot, ClientSize.Width - Pad - 41 - _mpWidth, y + 4, 9, 9);
        }

    }

    /// <summary>
    /// Text with a soft dark halo behind it. The ground behind a word changes
    /// constantly over a game and no single colour stays readable against all
    /// of it; with real alpha the halo can be a spread shadow rather than four
    /// hard copies of the glyph.
    /// </summary>
    private static void Glyph(Graphics g, string s, Color colour, Font font, int x, int y)
    {
        if (s.Length == 0) return;

        using (var halo = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            for (int r = 2; r >= 1; r--)
                foreach (var (dx, dy) in new[] { (-r, 0), (r, 0), (0, -r), (0, r),
                                                 (-r, -r), (r, -r), (-r, r), (r, r) })
                    g.DrawString(s, font, halo, x + dx, y + dy);

        using var brush = new SolidBrush(colour);
        g.DrawString(s, font, brush, x, y);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        if (r.Width <= d || r.Height <= d)
        {
            path.AddEllipse(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade.Dispose();
            _ctrlWatch.Dispose();
            _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
