namespace P02;

/// <summary>Controls for one globe: on/off, region, trigger point, key.</summary>
public sealed class GlobePanel : Card
{
    private readonly WatcherConfig _cfg;
    private readonly bool _blue;
    private readonly Action _onChange;
    private readonly Func<string> _windowMatch;
    private readonly Func<Box?> _other;
    private readonly Action? _findNumbers;
    private readonly TextProbe _probe;

    private readonly CheckBox _enabled = new();
    private readonly Label _region = new();
    private readonly NumericUpDown _threshold = new();
    private readonly NumericUpDown _cooldown = new();
    private readonly NumericUpDown _panicBelow = new();
    private readonly NumericUpDown _panicGap = new();
    private readonly NumericUpDown _burst = new();
    private readonly NumericUpDown _hold = new();
    private readonly NumericUpDown _knownMax = new();
    private readonly NumericUpDown _uber = new();
    private readonly NumericUpDown _lastDitch = new();
    private readonly Label _burstTime = new();
    private readonly Label _effect = new();
    private bool _blind;
    private bool _settingMax;
    private readonly Label _numbers = new();

    // Only the life panel carries these: energy shield is recovered by the life
    // flask, when it is recovered at all.
    private readonly WatcherConfig? _shield;
    private readonly CheckBox _shieldOn = new();
    private readonly NumericUpDown _shieldBelow = new();
    private readonly NumericUpDown _shieldMax = new();
    private readonly Label _shieldRead = new();
    private readonly Label _tuned = new();

    // Also only the life panel: Low Life setup needs the whole config, not
    // just shield's, since it is a separate on/off from shield's own and its
    // three floors live at the top level rather than on any one pool.
    private readonly AppConfig? _app;
    private readonly CheckBox _lowLifeOn = new();
    private readonly NumericUpDown _lowLifeTier1 = new();
    private readonly NumericUpDown _lowLifeTier2 = new();
    private readonly NumericUpDown _lowLifeTier3 = new();
    private readonly Label _warn = new();
    private readonly KeyBindBox _key = new();
    private readonly ComboBox _pad = new();

    /// <summary>
    /// Whether presses are going to a controller, asked of whoever owns the
    /// settings rather than reached for through a global.
    /// </summary>
    public static Func<bool>? UsingController;

    /// <summary>
    /// Whether memory has the character right now, so a silent numbers box can
    /// be reported as what it is - a spare wheel that is flat while the car is
    /// still moving - rather than as an emergency.
    /// </summary>
    public static Func<bool>? MemoryCovering;
    private readonly LevelBar _bar = new();
    private readonly Label _pct = new();

    /// <summary>Rows of buttons that sit side by side and must not collide.</summary>
    private readonly List<Control[]> _rows = [];

    /// <summary>How tall this card needs to be for its own contents.</summary>
    public int MinimumHeight { get; private set; }

    public GlobePanel(string title, WatcherConfig cfg, bool blue, Action onChange,
                      Func<string> windowMatch, Func<Box?> otherRegion, TextProbe probe,
                      WatcherConfig? shield = null, Action? findNumbers = null,
                      AppConfig? app = null)
    {
        _findNumbers = findNumbers;
        _probe = probe;
        _shield = shield;
        _app = app;
        _cfg = cfg;
        _blue = blue;
        _onChange = onChange;
        _windowMatch = windowMatch;
        _other = otherRegion;

        Text = title;
        Width = 382;
        MinimumSize = new Size(360, 0);
        Height = 570;


        int y = 38;

        _enabled.Text = $"Watch my {title.ToLowerInvariant()}";
        _enabled.Checked = cfg.Enabled;
        _enabled.Font = new Font(Font, FontStyle.Bold);
        _enabled.SetBounds(14, y, 200, 24);
        _enabled.CheckedChanged += (_, _) => { _cfg.Enabled = _enabled.Checked; _onChange(); };
        Controls.Add(_enabled);
        Tips.On(_enabled, Tips.Watch);
        y += 30;

        _bar.SetBounds(14, y, 200, 18);
        Controls.Add(_bar);
        Tips.On(_bar, "This pool as it is right now, with your trigger marked.");
        _pct.SetBounds(222, y, 150, 18);
        _pct.Font = Theme.UiBold;
        _pct.Text = "--";
        Controls.Add(_pct);
        Tips.On(_pct, "The fraction being compared against your trigger.");
        y += 28;

        Controls.Add(Lab("Region", 14, y + 4, Tips.Region));
        _region.SetBounds(70, y + 4, 150, 18);
        _region.Text = cfg.Region.ToString();
        Controls.Add(_region);
        Tips.On(_region, Tips.Region);
        y += 26;

        var setBtn = new Button { Text = "Set…", Bounds = new Rectangle(70, y, 56, 26) };
        setBtn.Click += (_, _) => PickRegion();
        Controls.Add(setBtn);
        Tips.On(setBtn, Tips.SetRegion);

        var autoBtn = new Button { Text = "Auto-find", Bounds = new Rectangle(130, y, 72, 26) };
        autoBtn.Click += (_, _) => AutoFind();
        Controls.Add(autoBtn);
        Tips.On(autoBtn, Tips.AutoFind);

        var calBtn = new Button { Text = "Full = 100%", Bounds = new Rectangle(206, y, 86, 26) };
        calBtn.Click += (_, _) => CalibrateFull();
        Controls.Add(calBtn);
        Tips.On(calBtn, Tips.FullHundred);

        var prevBtn = new Button { Text = "Check", Bounds = new Rectangle(296, y, 56, 26) };
        prevBtn.Click += (_, _) => Preview();
        Controls.Add(prevBtn);
        Tips.On(prevBtn, Tips.Check);
        _rows.Add([setBtn, autoBtn, calBtn, prevBtn]);
        y += 30;

        var numBtn = new Button { Text = "Numbers...", Bounds = new Rectangle(296, y, 76, 26) };
        numBtn.Click += (_, _) => PickTextRegion();
        Controls.Add(numBtn);

        var numTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        numTip.SetToolTip(numBtn,
            $"Box the {title} line on its own - the word \"{title}\" and its numbers, and"
            + Environment.NewLine
            + "nothing above or below. Each stat gets its own box; this one is only for"
            + Environment.NewLine
            + $"{title}."
            + Environment.NewLine + Environment.NewLine
            + "Find numbers, at the bottom of the window, does all of them at once without"
            + Environment.NewLine
            + "any dragging. Use this only when that cannot find one.");

        var emptyBtn = new Button { Text = "Teach it", Bounds = new Rectangle(206, y, 86, 26) };
        emptyBtn.Click += (_, _) => TeachGlobe();
        Controls.Add(emptyBtn);
        Tips.On(emptyBtn, Tips.TeachIt);

        _tuned.SetBounds(14, y + 5, 188, 18);
        _tuned.ForeColor = SystemColors.GrayText;
        Controls.Add(_tuned);
        Tips.On(_tuned, "What the colour tuning worked out, and whether it can "
            + "separate a full globe from an empty one at all.");
        y += 30;

        _warn.SetBounds(14, y, 338, 30);
        _warn.ForeColor = Color.FromArgb(190, 60, 0);
        Controls.Add(_warn);
        Tips.On(_warn, "Anything about this setup that could let a drained globe read "
            + "as full - the failure that looks like nothing at all.");
        y += 32;

        Controls.Add(Lab("Fire below", 14, y + 4, Tips.FireBelow));
        _threshold.SetBounds(90, y, 62, 24);
        _threshold.Minimum = 1;
        _threshold.Maximum = 99;
        _threshold.Value = (decimal)Math.Clamp(cfg.Threshold * 100, 1, 99);
        _threshold.ValueChanged += (_, _) =>
            { _cfg.Threshold = (double)_threshold.Value / 100.0; _onChange(); };
        Controls.Add(_threshold);
        Tips.On(_threshold, Tips.FireBelow);
        Controls.Add(Lab("%", 156, y + 4));

        Controls.Add(Lab("Key", 190, y + 4));
        _key.SetBounds(222, y, 84, 24);
        _key.Key = cfg.Key;
        _key.KeyBound += k => { _cfg.Key = k; _onChange(); };
        Controls.Add(_key);
        Tips.On(_key, Tips.Key);
        y += 32;

        // Always here, not swapped in.
        //
        // It used to appear only once "Send keys by" had been set to
        // Controller, which put a settings box behind a mechanism that could
        // fail - and did: the panel asked whether a controller was in use
        // before anything had said, got nothing, and drew a key box. A row
        // that is always present cannot fail to appear, and the dropdown then
        // decides which of the two is actually pressed rather than which one
        // you are allowed to fill in.
        Controls.Add(Lab("Controller", 14, y + 4, Tips.PadButton));
        _pad.FlatStyle = FlatStyle.Standard;
        _pad.SetBounds(90, y, 216, 24);
        _pad.DropDownStyle = ComboBoxStyle.DropDownList;
        _pad.Items.Add("not set");
        foreach (var (_, says) in Gamepad.Buttons) _pad.Items.Add(says);
        _pad.SelectedIndex = Math.Max(0,
            1 + Array.FindIndex(Gamepad.Buttons,
                b => b.Name.Equals(cfg.PadButton, StringComparison.OrdinalIgnoreCase)));
        _pad.SelectedIndexChanged += (_, _) =>
        {
            _cfg.PadButton = _pad.SelectedIndex <= 0
                ? ""
                : Gamepad.Buttons[_pad.SelectedIndex - 1].Name;
            Log.Write($"{title}: controller button set to "
                      + (_cfg.PadButton.Length == 0 ? "nothing" : _cfg.PadButton));
            _onChange();
        };
        Controls.Add(_pad);
        Tips.On(_pad, Tips.PadButton);
        Log.Write($"{title}: controller list has {_pad.Items.Count} entries, showing "
                  + $"\"{_pad.SelectedItem}\"");
        y += 32;

        Controls.Add(Lab("Cooldown", 14, y + 4, Tips.Cooldown));
        _cooldown.SetBounds(90, y, 76, 24);
        _cooldown.Minimum = 20;
        _cooldown.Maximum = 60000;
        _cooldown.Increment = 20;
        _cooldown.Value = Math.Clamp(cfg.CooldownMs, 20, 60000);
        _cooldown.ValueChanged += (_, _) =>
            { _cfg.CooldownMs = (int)_cooldown.Value; _onChange(); };
        Controls.Add(_cooldown);
        Tips.On(_cooldown, Tips.Cooldown);
        Controls.Add(Lab("ms", 170, y + 4));
        y += 32;

        Controls.Add(Lab("Panic below", 14, y + 4, Tips.PanicBelow));
        _panicBelow.SetBounds(90, y, 62, 24);
        _panicBelow.Minimum = 0;
        _panicBelow.Maximum = 99;
        _panicBelow.Value = (decimal)Math.Clamp(cfg.PanicBelow * 100, 0, 99);
        _panicBelow.ValueChanged += (_, _) =>
            { _cfg.PanicBelow = (double)_panicBelow.Value / 100.0; _onChange(); };
        Controls.Add(_panicBelow);
        Tips.On(_panicBelow, Tips.PanicBelow);
        Controls.Add(Lab("%", 156, y + 4));

        Controls.Add(Lab("gap", 190, y + 4, Tips.PanicGap));
        _panicGap.SetBounds(222, y, 84, 24);
        _panicGap.Minimum = 10;
        _panicGap.Maximum = 5000;
        _panicGap.Increment = 10;
        _panicGap.Value = Math.Clamp(cfg.PanicCooldownMs, 10, 5000);
        _panicGap.ValueChanged += (_, _) =>
            { _cfg.PanicCooldownMs = (int)_panicGap.Value; _onChange(); };
        Controls.Add(_panicGap);
        Tips.On(_panicGap, Tips.PanicGap);
        y += 30;

        Controls.Add(Lab("Emergency below", 14, y + 4, Tips.Uber));
        _uber.SetBounds(140, y, 56, 24);
        _uber.Minimum = 0;
        _uber.Maximum = 30;
        _uber.Value = (decimal)Math.Clamp(cfg.UberBelow * 100, 0, 30);
        _uber.ValueChanged += (_, _) =>
        { _cfg.UberBelow = (double)_uber.Value / 100.0; _onChange(); };
        Controls.Add(_uber);
        Tips.On(_uber, Tips.Uber);
        Controls.Add(Lab("%", 200, y + 4));

        Controls.Add(new Label
        {
            Bounds = new Rectangle(220, y + 4, 150, 18),
            ForeColor = SystemColors.GrayText,
            Text = "one press, as a last resort",
        });
        y += 30;

        Controls.Add(Lab("Last ditch below", 14, y + 4, Tips.LastDitch));
        _lastDitch.SetBounds(140, y, 56, 24);
        _lastDitch.Minimum = 0;
        _lastDitch.Maximum = 30;
        _lastDitch.Value = (decimal)Math.Clamp(cfg.LastDitchBelow * 100, 0, 30);
        _lastDitch.ValueChanged += (_, _) =>
        { _cfg.LastDitchBelow = (double)_lastDitch.Value / 100.0; _onChange(); };
        Controls.Add(_lastDitch);
        Tips.On(_lastDitch, Tips.LastDitch);
        Controls.Add(Lab("%", 200, y + 4));

        Controls.Add(Cap2(new Label
        {
            Bounds = new Rectangle(220, y + 4, 160, 18),
            ForeColor = SystemColors.GrayText,
            Text = "a second one, further down",
        }, Tips.LastDitch));

        var uberTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        uberTip.SetToolTip(_uber,
            "One press, once, if you fall past this - whatever else is waiting."
            + Environment.NewLine + Environment.NewLine
            + "A cooldown running, a burst still going out, a confirming frame not"
            + Environment.NewLine
            + "yet counted: any of those can be in the way at the moment a heal is"
            + Environment.NewLine
            + "needed. This ignores all of them, fires a single press, and then"
            + Environment.NewLine
            + "stays quiet until you are back above it - a net, not a second"
            + Environment.NewLine
            + "trigger spending charges alongside the first. Set to 0 to turn off."
            + Environment.NewLine + Environment.NewLine
            + "It stops at 30% on purpose. Higher than that it is not an emergency,"
            + Environment.NewLine
            + "it is a way to spend a flask's charges on chip damage - which leaves"
            + Environment.NewLine
            + "nothing for the hit that actually matters.");
        y += 30;

        Controls.Add(Lab("Presses per trigger", 14, y + 4, Tips.Presses));
        _burst.SetBounds(140, y, 50, 24);
        _burst.Minimum = 1;
        _burst.Maximum = 5;
        _burst.Value = Math.Clamp(cfg.BurstCount, 1, 5);
        _burst.ValueChanged += (_, _) =>
            { _cfg.BurstCount = (int)_burst.Value; RefreshBurstTime(); _onChange(); };
        Controls.Add(_burst);
        Tips.On(_burst, Tips.Presses);

        Controls.Add(new Label
        {
            Bounds = new Rectangle(196, y + 4, 120, 18),
            ForeColor = SystemColors.GrayText,
            Text = "charges allowing",
        });
        y += 32;

        Controls.Add(Lab("Hold each press", 14, y + 4, Tips.Hold));
        _hold.SetBounds(140, y, 60, 24);
        _hold.Minimum = 10;
        _hold.Maximum = 400;
        _hold.Increment = 10;
        _hold.Value = Math.Clamp(cfg.HoldMs, 10, 400);
        _hold.ValueChanged += (_, _) =>
        { _cfg.HoldMs = (int)_hold.Value; RefreshBurstTime(); _onChange(); };
        Controls.Add(_hold);
        Tips.On(_hold, Tips.Hold);
        Controls.Add(Lab("ms", 204, y + 4));

        Controls.Add(new Label
        {
            Bounds = new Rectangle(232, y + 4, 130, 18),
            ForeColor = SystemColors.GrayText,
            Text = "raise if presses are missed",
        });
        y += 30;

        Controls.Add(Lab($"My max {title.ToLowerInvariant()}", 14, y + 4, Tips.KnownMax));
        _knownMax.SetBounds(140, y, 72, 24);
        _knownMax.Minimum = 0;
        _knownMax.Maximum = 1_000_000;
        _knownMax.Increment = 1;
        _knownMax.Value = Math.Clamp(cfg.KnownMax, 0, 1_000_000);
        _knownMax.ValueChanged += (_, _) =>
        {
            if (_settingMax) return;   // adopted, not typed
            _cfg.KnownMax = (int)_knownMax.Value;
            RefreshShieldWarning();
            _onChange();
        };
        Controls.Add(_knownMax);
        Tips.On(_knownMax, Tips.KnownMax);
        Controls.Add(new Label
        {
            Bounds = new Rectangle(218, y + 4, 150, 18),
            ForeColor = SystemColors.GrayText,
            Text = "0 = work it out",
        });
        y += 26;

        if (_shield is not null) y = AddShield(y);
        if (_shield is not null && _app is not null) y = AddLowLife(y);

        // Hold time and press count multiply out into how long a trigger takes
        // to send, and nothing else can go out during it. Worth seeing.
        _burstTime.SetBounds(14, y, 340, 18);
        _burstTime.ForeColor = SystemColors.GrayText;
        Controls.Add(_burstTime);
        Tips.On(_burstTime, "How long one trigger takes to send, worked out from the "
            + "presses and the hold.", "",
            "Nothing can fire faster than this, so a cooldown below it does nothing.");
        RefreshBurstTime();
        y += 22;

        _effect.SetBounds(14, y, 340, 18);
        _effect.ForeColor = SystemColors.GrayText;
        Controls.Add(_effect);
        Tips.On(_effect, "Whether the last press actually did anything.", "",
            "A pool that does not move after a press means the key, the binding or "
            + "the charges - not detection. Presses that keep doing nothing stop "
            + "for a few seconds rather than spending what is left.");
        y += 20;

        _numbers.SetBounds(14, y, 340, 18);
        _numbers.ForeColor = SystemColors.GrayText;
        _numbers.Text = "Deciding: not read yet";
        Controls.Add(_numbers);
        Tips.On(_numbers, "Which reading is deciding right now: memory, the printed "
            + "numbers, or the globe pixels.", "",
            "Memory and numbers are exact. Globe pixels are the fallback - they "
            + "follow energy shield too, and read a poisoned globe as empty.");

        // The card was a fixed height, so the last line - which is the one
        // saying what it is actually reading from - was cut in half.
        // Both cards end up the same height whatever each one holds - the life
        // card carries the shield settings and the mana card does not, and a
        // pair of unequal cards reads as one of them being broken.
        Height = Math.Max(Height, _numbers.Bottom + 14);
        MinimumHeight = Height;
    }

    /// <summary>
    /// Flags settings that let a drained globe read as full - the failure that
    /// looks like nothing at all until it costs you a character.
    /// </summary>
    /// <summary>
    /// Shows how long one trigger takes to send. Nothing else can be sent
    /// during it, so this is the real floor on how often it can act - the
    /// cooldown cannot go below it however low it is set.
    /// </summary>
    private void RefreshBurstTime()
    {
        int n = Math.Max(1, _cfg.BurstCount);
        int ms = n * _cfg.HoldMs + (n - 1) * _cfg.BurstGapMs;
        _burstTime.Text = n == 1
            ? $"One press takes {ms} ms to send, so at most {1000 / Math.Max(1, ms)} a second."
            : $"{n} presses take {ms} ms to send, so at most "
              + $"{1000 / Math.Max(1, ms)} bursts a second.";

        // A long hold does throttle firing, but only as a side effect, and it
        // slows the emergency press down with everything else.
        if (_cfg.HoldMs > 150)
        {
            _burstTime.Text += "  Hold is not a rate limit - use Cooldown.";
            _burstTime.ForeColor = Color.FromArgb(240, 180, 70);
        }
        else
        {
            _burstTime.ForeColor = Theme.Dim;
        }
    }

    /// <summary>
    /// Memory needs both maxima even for a pool nobody is watching: one number
    /// matches thousands of places in a heap, two identify a character. That is
    /// not obvious from a panel that is switched off.
    /// </summary>
    public void NeedMaxForMemory(bool needed)
    {
        if (!needed || _cfg.KnownMax > 0) return;
        _warn.Text = "Memory needs this maximum too, even with this globe off - "
                   + "press Find numbers.";
        _warn.ForeColor = Color.FromArgb(240, 180, 70);
    }

    private void RefreshWarning()
    {
        if (_blind) return;
        if (!_cfg.Region.IsValid) { _warn.Text = ""; return; }

        // The globe is the fallback. Once the numbers are set up, or memory is
        // reading, none of the colour advice below applies to anything that
        // decides whether to fire - and a red line about calibration on a
        // working setup reads as a fault, which is worse than saying nothing.
        if (_cfg.TextRegion.IsValid) { _warn.Text = ""; return; }

        // Nor does any of it matter for a pool nobody asked to watch.
        if (!_cfg.Enabled) { _warn.Text = ""; return; }

        if (!_cfg.TextRegion.IsValid && _probe.Available)
            _warn.Text = "No numbers set. Press Numbers... - it is exact, needs no "
                       + "calibration, and stops it acting on menu screens.";
        else if (_cfg.ColourMargin < 10 && _cfg.EmptyDominance < 0)
            _warn.Text = "Colour margin is very low. An empty globe may read as full. "
                       + "Press Tune colours with the globe part way down.";
        else if (_cfg.EmptyDominance < 0)
            _warn.Text = "Not calibrated against an empty globe. If it never fires, "
                       + "press Tune colours with the globe part way down.";
        else
            _warn.Text = "";
    }

    /// <summary>
    /// Energy shield, sharing this globe's key and timing because the life
    /// flask is what recovers it - and only for characters who have taken
    /// something that makes it do so.
    /// </summary>
    private int AddShield(int y)
    {
        Controls.Add(new Label
        {
            Bounds = new Rectangle(14, y, 352, 2),
            BorderStyle = BorderStyle.Fixed3D,
        });
        y += 10;

        _shieldOn.Text = "Also fire for energy shield";
        _shieldOn.Checked = _shield!.Enabled;
        _shieldOn.SetBounds(14, y, 190, 22);
        _shieldOn.CheckedChanged += (_, _) =>
        {
            _shield.Enabled = _shieldOn.Checked;
            if (_shieldOn.Checked && _app is not null && _lowLifeOn.Checked)
            {
                _app.LowLifeEnabled = false;
                _lowLifeOn.Checked = false;
                RefreshLowLifeEnabled();
            }
            _onChange();
        };
        Controls.Add(_shieldOn);
        Tips.On(_shieldOn, Tips.ShieldOn);

        var tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        tip.SetToolTip(_shieldOn,
            "Only useful if something in your build makes a life flask recover energy"
            + Environment.NewLine
            + "shield. Without that a flask does nothing for shield, and firing at a"
            + Environment.NewLine
            + "draining shield only spends charges."
            + Environment.NewLine + Environment.NewLine
            + "It uses the life flask's key and all of its timing - cooldown, panic gap,"
            + Environment.NewLine
            + "presses per trigger and hold. Only the trigger below, its own Numbers box"
            + Environment.NewLine
            + "and its own maximum are separate."
            + Environment.NewLine + Environment.NewLine
            + "My max here is your maximum SHIELD, not life. Leave it at 0 if unsure -"
            + Environment.NewLine
            + "the word \"Shield\" in the Numbers box is the better anchor anyway.");
        tip.SetToolTip(_shieldMax,
            "Your maximum energy shield, or 0. Do not put your life maximum here:"
            + Environment.NewLine
            + "it is used to pick which line of the HUD to read, so a life value"
            + Environment.NewLine
            + "would make this read your life.");

        Controls.Add(Lab("below", 208, y + 3, Tips.ShieldBelow));
        _shieldBelow.SetBounds(250, y, 54, 24);
        _shieldBelow.Minimum = 1;
        _shieldBelow.Maximum = 99;
        _shieldBelow.Value = (decimal)Math.Clamp(_shield.Threshold * 100, 1, 99);
        _shieldBelow.ValueChanged += (_, _) =>
        { _shield.Threshold = (double)_shieldBelow.Value / 100.0; _onChange(); };
        Controls.Add(_shieldBelow);
        Tips.On(_shieldBelow, Tips.ShieldBelow);
        Controls.Add(Lab("%", 308, y + 3));
        y += 28;

        var numBtn = new Button { Text = "Numbers...", Bounds = new Rectangle(14, y, 80, 24) };
        numBtn.Click += (_, _) => PickShieldNumbers();
        Controls.Add(numBtn);

        var shieldTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        shieldTip.SetToolTip(numBtn,
            "Box the Shield line on its own - the word \"Shield\" and its numbers, and"
            + Environment.NewLine
            + "nothing above or below. Not the life line: this is a separate reading."
            + Environment.NewLine + Environment.NewLine
            + "Only needed if Also fire for energy shield or Low life setup is"
            + Environment.NewLine
            + "ticked, and only if Find numbers could not locate it.");

        Controls.Add(Lab("My max shield", 102, y + 3, Tips.ShieldMax));
        _shieldMax.SetBounds(190, y, 72, 24);
        _shieldMax.Minimum = 0;
        _shieldMax.Maximum = 1_000_000;
        _shieldMax.Value = Math.Clamp(_shield.KnownMax, 0, 1_000_000);
        _shieldMax.ValueChanged += (_, _) =>
        {
            _shield.KnownMax = (int)_shieldMax.Value;
            RefreshShieldWarning();
            _onChange();
        };
        Controls.Add(_shieldMax);
        y += 26;

        _shieldRead.SetBounds(14, y, 352, 18);
        _shieldRead.ForeColor = SystemColors.GrayText;
        _shieldRead.Text = "Shield: not set - needs its own Numbers box";
        Controls.Add(_shieldRead);
        Tips.On(_shieldRead, "What the shield line reads right now, and whether it is "
            + "set up at all.");
        RefreshShieldWarning();
        return y + 22;
    }

    /// <summary>
    /// A separate "oh shit" net for a low-life build: shield's own reading
    /// decides when to drink the life flask, at three floors instead of
    /// shield's own single threshold. Mutually exclusive with "Also fire for
    /// energy shield" above - the two checkboxes clear each other, since only
    /// one firing system may ever act on shield's drop at a time.
    /// </summary>
    private int AddLowLife(int y)
    {
        Controls.Add(new Label
        {
            Bounds = new Rectangle(14, y, 352, 2),
            BorderStyle = BorderStyle.Fixed3D,
        });
        y += 10;

        _lowLifeOn.Text = "Low life setup (heal off energy shield)";
        _lowLifeOn.Checked = _app!.LowLifeEnabled;
        _lowLifeOn.SetBounds(14, y, 260, 22);
        _lowLifeOn.CheckedChanged += (_, _) =>
        {
            _app.LowLifeEnabled = _lowLifeOn.Checked;
            if (_lowLifeOn.Checked && _shieldOn.Checked) _shieldOn.Checked = false;
            RefreshLowLifeEnabled();
            _onChange();
        };
        Controls.Add(_lowLifeOn);
        Tips.On(_lowLifeOn, Tips.LowLifeOn);
        y += 28;

        Controls.Add(Lab("First below", 14, y + 4, Tips.LowLifeTier1));
        _lowLifeTier1.SetBounds(140, y, 56, 24);
        _lowLifeTier1.Minimum = 0;
        _lowLifeTier1.Maximum = 80;
        _lowLifeTier1.Value = (decimal)Math.Clamp(_app.LowLifeTier1 * 100, 0, 80);
        _lowLifeTier1.ValueChanged += (_, _) =>
        { _app.LowLifeTier1 = (double)_lowLifeTier1.Value / 100.0; _onChange(); };
        Controls.Add(_lowLifeTier1);
        Tips.On(_lowLifeTier1, Tips.LowLifeTier1);
        Controls.Add(Lab("%", 200, y + 4));
        y += 28;

        Controls.Add(Lab("Second below", 14, y + 4, Tips.LowLifeTier2));
        _lowLifeTier2.SetBounds(140, y, 56, 24);
        _lowLifeTier2.Minimum = 0;
        _lowLifeTier2.Maximum = 50;
        _lowLifeTier2.Value = (decimal)Math.Clamp(_app.LowLifeTier2 * 100, 0, 50);
        _lowLifeTier2.ValueChanged += (_, _) =>
        { _app.LowLifeTier2 = (double)_lowLifeTier2.Value / 100.0; _onChange(); };
        Controls.Add(_lowLifeTier2);
        Tips.On(_lowLifeTier2, Tips.LowLifeTier2);
        Controls.Add(Lab("%", 200, y + 4));
        y += 28;

        Controls.Add(Lab("Third at/below", 14, y + 4, Tips.LowLifeTier3));
        _lowLifeTier3.SetBounds(140, y, 56, 24);
        _lowLifeTier3.Minimum = 0;
        _lowLifeTier3.Maximum = 30;
        _lowLifeTier3.Value = (decimal)Math.Clamp(_app.LowLifeTier3 * 100, 0, 30);
        _lowLifeTier3.ValueChanged += (_, _) =>
        { _app.LowLifeTier3 = (double)_lowLifeTier3.Value / 100.0; _onChange(); };
        Controls.Add(_lowLifeTier3);
        Tips.On(_lowLifeTier3, Tips.LowLifeTier3);
        Controls.Add(Lab("%", 200, y + 4));
        y += 30;

        RefreshLowLifeEnabled();
        return y;
    }

    /// <summary>
    /// The old shield checkbox turning on clears this one, the same way this
    /// one clears it - checked from here too so either direction keeps the
    /// tier fields' enabled state honest without a second event to forget.
    /// </summary>
    private void RefreshLowLifeEnabled()
    {
        bool on = _lowLifeOn.Checked;
        _lowLifeTier1.Enabled = on;
        _lowLifeTier2.Enabled = on;
        _lowLifeTier3.Enabled = on;
    }

    /// <summary>
    /// The shield maximum is used to choose which line of the HUD to read, so
    /// putting the life maximum in it makes the shield read life.
    /// </summary>
    private void RefreshShieldWarning()
    {
        if (_shield is null) return;
        if (_shield.KnownMax > 0 && _shield.KnownMax == _cfg.KnownMax)
        {
            _shieldRead.Text = "That is your LIFE maximum - shield will read life. Use your "
                             + "shield maximum, or 0.";
            _shieldRead.ForeColor = Color.FromArgb(200, 30, 30);
            return;
        }
        GuardMaxima();
    }

    private void PickShieldNumbers()
    {
        if (!_probe.Available)
        {
            MessageBox.Show(this, "Windows OCR is not available, so the shield numbers "
                            + "cannot be read.", "Energy shield",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(180);
        var r = RegionPickerForm.Pick(
            "Drag a box around the Shield LINE ONLY - the word \"Shield\" and its "
            + "numbers, nothing above or below it");
        if (r is null) { owner?.Show(); return; }

        string got = _probe.Probe(r.Value, out var shieldShot);
        owner?.Show();
        if (got.Length == 0)
        {
            ReportUnreadable(r.Value, shieldShot);
            return;
        }
        shieldShot?.Dispose();

        _shield!.TextRegion = Box.From(r.Value);
        _shield.UseText = true;

        string filled = "";
        if (TextOcr.TryParse(got, out _, out int smax, "Shield") && smax > 0
            && smax != _shield.KnownMax)
        {
            _shield.KnownMax = smax;
            _settingMax = true;
            _shieldMax.Value = Math.Clamp(smax, 0, 1_000_000);
            _settingMax = false;
            filled = Environment.NewLine + Environment.NewLine
                     + $"Maximum filled in as {smax:N0}.";
        }

        RefreshShieldWarning();
        _onChange();
        MessageBox.Show(this, $"Read: \"{got}\"" + filled, "Energy shield",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Latest energy shield reading, shown under the life controls.</summary>
    public void UpdateShield(GlobeReading r)
    {
        if (_shield is null) return;
        if ((_shield.KnownMax > 0 && _shield.KnownMax == _cfg.KnownMax)
            || (_cfg.KnownMax == 0 && _shield.KnownMax > 0))
        {
            RefreshShieldWarning();
            return;
        }
        bool lowLife = _app?.LowLifeEnabled ?? false;
        if (!_shield.Enabled && !lowLife)
        {
            _shieldRead.Text = "Shield: not watched";
            _shieldRead.ForeColor = SystemColors.GrayText;
            return;
        }
        if (!r.Ok || !r.FromText)
        {
            _shieldRead.Text = "Shield: no reading - set its Numbers box";
            _shieldRead.ForeColor = Color.FromArgb(190, 60, 0);
            return;
        }
        _shieldRead.Text = $"Shield: {r.TextRaw}  ({r.Fraction * 100:0} %)";
        double warnBelow = lowLife ? _app!.LowLifeTier1 : _shield.Threshold;
        _shieldRead.ForeColor = r.Fraction < warnBelow
            ? Color.FromArgb(190, 60, 0)
            : Color.FromArgb(0, 100, 0);
    }

    /// <summary>
    /// A caption. It takes the same explanation as the field it names: the
    /// caption is the part you read, so it is the part the pointer lands on,
    /// and finding nothing there reads as nothing to find.
    /// </summary>
    /// <summary>A ready-made label given the same explanation as its field.</summary>
    /// <summary>The bare current/maximum out of whatever a source reported.</summary>
    private static string Numbers(string raw)
    {
        int slash = raw.IndexOf('/');
        if (slash < 0) return "";

        int from = slash;
        while (from > 0 && (char.IsDigit(raw[from - 1]) || raw[from - 1] == ',')) from--;

        int to = slash + 1;
        while (to < raw.Length && (char.IsDigit(raw[to]) || raw[to] == ',')) to++;

        return to - from > 3 ? raw[from..to] : "";
    }

    private static Label Cap2(Label l, string[] tip)
    {
        Tips.On(l, tip);
        return l;
    }

    private static Label Lab(string text, int x, int y, string[]? tip = null)
    {
        var l = new Label { Text = text, Bounds = new Rectangle(x, y, 76, 18), AutoSize = true };
        if (tip is not null) Tips.On(l, tip);
        return l;
    }

    /// <summary>
    /// Both maxima sat under the word "My max", one above the other, and the
    /// life value ended up in the shield box - which then makes the shield
    /// watcher read life. Naming each one for the pool it holds costs nothing.
    /// </summary>
    private void GuardMaxima()
    {
        if (_shield is null) return;
        if (_cfg.KnownMax == 0 && _shield.KnownMax > 0)
        {
            _shieldRead.Text = "Life has no maximum but shield does - are these the right way "
                             + "round?";
            _shieldRead.ForeColor = Color.FromArgb(200, 30, 30);
        }
    }

    /// <summary>
    /// The numbers beside the globe are an exact reading, so this is the most
    /// reliable thing to set: no calibration, no colour thresholds, and the
    /// maximum is read too, so gear that changes your pool does not matter.
    /// </summary>
    private void PickTextRegion()
    {
        if (!_probe.Available)
        {
            MessageBox.Show(this,
                "Windows OCR is not available on this machine, so the numbers cannot be "
                + "read. The globe pixels will be used instead."
                + Environment.NewLine + Environment.NewLine + _probe.Reason,
                "Numbers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(180);
        var r = RegionPickerForm.Pick(
            $"Drag a box around the {Text} LINE ONLY - the word \"{Text}\" and its "
            + "numbers, nothing above or below it");
        if (r is null) { owner?.Show(); return; }

        // Read it while this window is still hidden. Capture takes whatever is
        // on the screen, so showing the window first risks reading QytOCR instead
        // of the game.
        string got = _probe.Probe(r.Value, out var shot);
        owner?.Show();
        if (got.Length == 0)
        {
            ReportUnreadable(r.Value, shot);
            return;
        }
        shot?.Dispose();

        _cfg.TextRegion = Box.From(r.Value);
        _cfg.UseText = true;
        _numbers.Text = $"Numbers: read \"{got}\"";

        // It has just read the maximum out loud. Reporting it in a dialog and
        // then leaving the box showing the old one is the app disagreeing with
        // itself in two places on the same screen.
        string filled = "";
        if (TextOcr.TryParse(got, out _, out int max, Text) && max > 0 && max != _cfg.KnownMax)
        {
            int was = _cfg.KnownMax;
            _cfg.KnownMax = max;
            _settingMax = true;
            _knownMax.Value = Math.Clamp(max, 0, 1_000_000);
            _settingMax = false;
            filled = Environment.NewLine + Environment.NewLine
                     + (was == 0 ? $"Maximum filled in as {max:N0}."
                                 : $"Maximum updated from {was:N0} to {max:N0}.");
        }

        RefreshWarning();
        _onChange();

        MessageBox.Show(this,
            $"Read: \"{got}\"" + filled + Environment.NewLine + Environment.NewLine
            + "This is now what decides when to fire, and it needs no calibration. "
            + "The globe pixels stay as the fallback.",
            "Numbers", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// "Nothing readable" on its own is a dead end. Saving what was actually in
    /// the box turns it into something anyone can look at - most often it shows
    /// the box landed on scenery, or on this window.
    /// </summary>
    private void ReportUnreadable(Rectangle box, Bitmap? shot)
    {
        string saved = "";
        try
        {
            if (shot is not null)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                saved = Path.Combine(AppConfig.Dir,
                    $"{Text}-numbers-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                shot.Save(saved, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { Log.Write($"could not save probe image: {ex.Message}"); }
        finally { shot?.Dispose(); }

        Log.Write($"{Text}: nothing readable in {box}"
                  + (saved.Length > 0 ? $" - saved {saved}" : ""));

        string msg = $"Nothing readable in that box ({box.Width}x{box.Height} at "
                   + $"{box.X},{box.Y})." + Environment.NewLine + Environment.NewLine
                   + "Include the whole line - the word and its numbers - and a little space "
                   + "around it. A box only a few pixels tall cannot be read at all."
                   + Environment.NewLine + Environment.NewLine
                   + "Find numbers, at the bottom of the window, does this without dragging.";

        if (saved.Length > 0)
            msg += Environment.NewLine + Environment.NewLine
                 + "What was in the box has been saved to:" + Environment.NewLine + saved;

        MessageBox.Show(this, msg, "Numbers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void PickRegion()
    {
        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(180);           // let the window actually disappear first
        var r = RegionPickerForm.Pick($"Drag a box around the {Text} globe");
        owner?.Show();
        if (r is null) return;
        Apply(r.Value);
    }

    private void AutoFind()
    {
        if (MessageBox.Show(
                $"Auto-find looks for the {Text.ToLowerInvariant()} globe by colour, so it only " +
                "works when the globe is FULL.\n\nMake sure the game is on screen and the " +
                $"{Text.ToLowerInvariant()} globe is topped up, then press OK. " +
                "The window hides for a moment while it looks.",
                "Auto-find", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
            return;

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(400);

        // The globes are in the corners of the GAME, which on a multi-monitor
        // desktop is not the corner of the virtual screen. Searching the whole
        // virtual desktop is why the bottom-right search used to land on a
        // second monitor and never find the mana globe at all.
        var area = GameArea();
        int w = (int)(area.Width * 0.25);
        int h = (int)(area.Height * 0.36);
        var search = _blue
            ? new Rectangle(area.Right - w, area.Bottom - h, w, h)
            : new Rectangle(area.Left, area.Bottom - h, w, h);

        // Deliberately not the detection settings: auto-find has shape checks
        // to fall back on, and coupling the two led to advice that lowered the
        // detection margin until an empty globe read as full.
        var found = OrbDetector.AutoLocate(search, _blue, margin: 18, minV: 45);
        owner?.Show();

        if (found is null)
        {
            string why = OrbDetector.LastLocateNote;
            string msg = "Couldn't find it.";
            msg += Environment.NewLine + Environment.NewLine +
                   $"Looked in {search.Width}x{search.Height} at {search.X},{search.Y} " +
                   $"(the {(_blue ? "bottom-right" : "bottom-left")} of {area.Width}x{area.Height} " +
                   $"at {area.X},{area.Y}).";
            if (why.Length > 0) msg += Environment.NewLine + Environment.NewLine + why;
            msg += Environment.NewLine + Environment.NewLine +
                   "Use Set... and drag the box by hand - that always works, and the "
                   + "Check window will confirm it reads correctly.";
            MessageBox.Show(msg, "Auto-find", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Apply(found.Value);

        // Auto-find already requires a full globe, and its box comes from the
        // edges of the colour blob, which sit a few percent inside the real
        // liquid. Calibrating straight away off the same full globe is what
        // makes a full globe read exactly 100% instead of 88-96%.
        bool calibrated = CalibrateFullSilently(out string note, out double reads);

        // A box on the globe reads full right after calibrating against it. One
        // that does not is on something else, or on a fraction of the globe.
        if (calibrated && reads >= 0.95)
        {
            MessageBox.Show(this,
                "Found it and calibrated against the full globe." + Environment.NewLine
                + note + Environment.NewLine + Environment.NewLine
                + "Now take it to about half and press Tune colours to finish.",
                "Auto-find", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this,
                $"Found a {found.Value.Width}x{found.Value.Height} region, but it does not "
                + "look right: after calibrating against it, a full globe reads "
                + $"{reads:P0} rather than 100%." + Environment.NewLine + Environment.NewLine
                + "That usually means the box is on part of the globe rather than all of it, "
                + "or on something else entirely. Press Check to see what it is looking at, "
                + "and use Set... to drag the box yourself if it is wrong.",
                "Auto-find", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Preview();
    }

    /// <summary>
    /// The measuring half of Full = 100%, with no prompts. Returns false if no
    /// liquid could be seen in the region.
    /// </summary>
    private bool CalibrateFullSilently(out string note, out double reads)
    {
        note = "";
        reads = 0;
        if (!_cfg.Region.IsValid) return false;

        using var shot = ScreenCapture.Snapshot(_cfg.Region.ToRect());
        var buf = ScreenCapture.ToBuffer(shot);
        if (!OrbDetector.CalibrateFull(buf, shot.Width, shot.Height, _cfg,
                                       out int full, out int empty))
            return false;

        _cfg.FullRow = full;
        _cfg.EmptyRow = empty;

        var st = OrbDetector.Measure(buf, shot.Width, shot.Height, _cfg, full, empty);
        _cfg.FullDominance = st.DomLow;
        _cfg.FullValue = st.ValLow;

        reads = OrbDetector.Fraction(buf, shot.Width, shot.Height, _cfg);
        note = $"Full is rows {full}-{empty} of {shot.Height}; it now reads {reads:P0}.";
        RefreshWarning();
        _onChange();
        return true;
    }

    /// <summary>
    /// Where to look for globes: the game window if we can find it, else the
    /// screen the other globe was set on, else the primary screen. Never the
    /// whole virtual desktop, which spans monitors the game is not on.
    /// </summary>
    private Rectangle GameArea()
    {
        var byTitle = Native.FindWindowRect(_windowMatch());
        if (byTitle is { } g && g.Width > 400 && g.Height > 300)
            return g;

        var other = _other();
        if (other is { IsValid: true })
            return Screen.FromRectangle(other.ToRect()).Bounds;

        return (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
    }

    private void Apply(Rectangle r)
    {
        _cfg.Region = Box.From(r);
        // Rows are box-relative, so an old calibration means nothing now.
        _cfg.FullRow = -1;
        _cfg.EmptyRow = -1;
        _region.Text = _cfg.Region.ToString();
        _onChange();
    }

    /// <summary>
    /// Pins "full" to where the liquid actually is, instead of assuming the box
    /// top is the top of the globe. Any frame caught in the box otherwise makes
    /// a full globe read a few percent short, and no colour setting can fix it.
    /// </summary>
    private void CalibrateFull()
    {
        if (!_cfg.Region.IsValid)
        {
            MessageBox.Show("Set a region first.", "Full = 100%",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string ask = $"Make sure your {Text.ToLowerInvariant()} globe is completely full, "
                     + "then press OK." + Environment.NewLine + Environment.NewLine + 
                       "This records where the liquid sits when full, so the reading "
                     + "hits a true 100%.";
        if (MessageBox.Show(ask,
                "Full = 100%", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
            return;

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(250);
        using var shot = ScreenCapture.Snapshot(_cfg.Region.ToRect());
        owner?.Show();

        var buf = ScreenCapture.ToBuffer(shot);
        if (!OrbDetector.CalibrateFull(buf, shot.Width, shot.Height, _cfg,
                                       out int full, out int empty))
        {
            MessageBox.Show(
                "Couldn't see any liquid in that box. Open Check and lower Colour margin " +
                "until the globe lights up green, then try again.",
                "Full = 100%", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.FullRow = full;
        _cfg.EmptyRow = empty;
        RefreshWarning();

        // Remember what liquid looks like, so Tune colours has something to
        // compare against.
        var st = OrbDetector.Measure(buf, shot.Width, shot.Height, _cfg, full, empty);
        _cfg.FullDominance = st.DomLow;
        _cfg.FullValue = st.ValLow;
        TryTune();
        _onChange();
        string done = $"Calibrated: full at row {full}, bottom at row {empty} "
                    + $"of {shot.Height}.";
        if (full > 0)
            done += Environment.NewLine + Environment.NewLine +
                    $"The box has {full} rows of frame above the liquid; that is what "
                    + "was costing you the missing percent.";
        MessageBox.Show(done, "Full = 100%", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
        Preview();
    }

    private void Preview()
    {
        if (!_cfg.Region.IsValid)
        {
            MessageBox.Show("Set a region first.", "Check",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var owner = FindForm();
        // The preview is live and sits on top, so the main window gets out of
        // the way rather than covering the globe being watched.
        owner?.Hide();
        using (var dlg = new PreviewForm(_cfg.Region.ToRect(), _cfg, Text, _onChange))
            dlg.ShowDialog();
        owner?.Show();
    }

    /// <summary>
    /// Records what the globe looks like when drained. A hue test alone cannot
    /// separate full from empty on the life globe — the drained part is the
    /// same red, only darker — so the threshold has to be learned from both.
    /// </summary>
    /// <summary>
    /// Learns the two colours from one frame of a part-full globe.
    ///
    /// Asking for an empty globe was asking for something unreachable: life
    /// regenerates, so it is never at zero while alive, and the death screen -
    /// where it is - paints everything red, so nothing measured there compares
    /// with anything measured during play. A globe part way down has both
    /// colours in it at once, lit identically.
    /// </summary>
    /// <summary>
    /// Learns the globe from two pictures of it: full, and empty.
    ///
    /// Colour tuning asked which colours are liquid, and on many globes there
    /// is no answer - drained and full are the same hue at overlapping
    /// brightness, and pressing it again cannot change that. This asks a
    /// question that always has one: does this row look more like it did when
    /// the globe was full, or when it was empty? Rows differ from each other
    /// even where the picture as a whole does not, because the frame, the
    /// shading and the gargoyle sit in fixed places.
    /// </summary>
    private void TeachGlobe()
    {
        if (!_cfg.Region.IsValid)
        {
            MessageBox.Show(this, "Set the globe's box first, with Auto-find or Set.",
                            "Teach it", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int[]? full = Snapshot($"Fill your {Text.ToLowerInvariant()} globe right up, then "
                               + "press OK without alt-tabbing back.");
        if (full is null) return;

        int[]? empty = Snapshot($"Now get the {Text.ToLowerInvariant()} globe as low as you "
                                + "can - dead is ideal and is the easiest to be sure of - "
                                + "then press OK.");
        if (empty is null) return;

        if (full.Length != empty.Length)
        {
            MessageBox.Show(this, "The box changed size between the two pictures. Try again.",
                            "Teach it", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int useful = 0;
        for (int i = 0; i < full.Length; i++)
            if (Apart(full[i], empty[i]) >= 24) useful++;

        if (useful < full.Length / 6)
        {
            MessageBox.Show(this,
                "Those two pictures are nearly identical, so there is nothing to learn "
                + "from them." + Environment.NewLine + Environment.NewLine
                + "Usually that means the globe was not actually full for the first one, or "
                + "not actually low for the second. If it happens again with a genuinely "
                + "full and a genuinely empty globe, this globe cannot be read by looking "
                + "at it - use the numbers, which have none of these problems.",
                "Teach it", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.FullLook = full;
        _cfg.EmptyLook = empty;
        _onChange();

        _tuned.Text = $"taught: {useful} of {full.Length} rows tell full from empty";
        MessageBox.Show(this,
            $"Learned. {useful} of {full.Length} rows can tell a full globe from an empty "
            + "one, which is what it now measures against - no colours to guess at."
            + Environment.NewLine + Environment.NewLine
            + "Press Check to watch it follow the globe down.",
            "Teach it", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private int[]? Snapshot(string ask)
    {
        if (MessageBox.Show(this, ask + Environment.NewLine + Environment.NewLine
                            + "This window hides for a moment while it looks.",
                            "Teach it", MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Information) != DialogResult.OK)
            return null;

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(400);

        using var cap = new ScreenCapture();
        bool got = cap.Grab(_cfg.Region.ToRect());
        int[]? look = got ? OrbDetector.Look(cap.Buffer, cap.Width, cap.Height) : null;
        owner?.Show();

        if (look is null)
            MessageBox.Show(this, "Could not photograph the globe.", "Teach it",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);

        return look;
    }

    private static int Apart(int a, int b)
    {
        int dr = ((a >> 16) & 255) - ((b >> 16) & 255);
        int dg = ((a >> 8) & 255) - ((b >> 8) & 255);
        int db = (a & 255) - (b & 255);
        return Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
    }

    private void CalibrateEmpty()
    {
        if (!_cfg.Region.IsValid || _cfg.FullRow < 0)
        {
            MessageBox.Show(this, "Set a region and press Full = 100% first.", "Tune colours",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string ask = $"Get your {Text.ToLowerInvariant()} to somewhere around half - not "
                   + "full, not empty, and not dead - then press OK."
                   + Environment.NewLine + Environment.NewLine
                   + "Both colours are read from the same frame: above the liquid is "
                   + "drained, below it is full. That is why it does not ask for an empty "
                   + "globe, which regeneration never allows, or a death screen, which "
                   + "tints everything red.";
        if (MessageBox.Show(this, ask, "Tune colours", MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Information) != DialogResult.OK)
            return;

        var owner = FindForm();
        owner?.Hide();
        Thread.Sleep(250);
        using var shot = ScreenCapture.Snapshot(_cfg.Region.ToRect());
        owner?.Show();

        int wasMargin = _cfg.ColourMargin;
        int wasValue = _cfg.MinValue;
        bool wasIgnoreHue = _cfg.IgnoreHue;

        var buf = ScreenCapture.ToBuffer(shot);
        bool tuned = OrbDetector.LearnFromPartial(buf, shot.Width, shot.Height, _cfg,
                                                  out string note);
        double reads = OrbDetector.Fraction(buf, shot.Width, shot.Height, _cfg);

        // A globe that was part full must read as part full. Pinned at either
        // end means the two colours were not told apart.
        bool worked = tuned && reads > 0.05 && reads < 0.95;

        if (!worked)
        {
            _cfg.ColourMargin = wasMargin;
            _cfg.MinValue = wasValue;
            _cfg.IgnoreHue = wasIgnoreHue;
            _cfg.EmptyDominance = -1;
            _cfg.EmptyValue = -1;
            _tuned.Text = "colour cannot separate full from drained here";
            _onChange();

            string why = (tuned
                    ? $"After tuning, that globe reads {reads * 100:0.0}% - pinned at one end "
                      + "rather than part way down, so the two colours were not told apart."
                    : $"Could not tune: {note}.")
                + Environment.NewLine + Environment.NewLine
                + "The colours have been left as they were. Some globes cannot be separated "
                + "this way at all: drained and full are the same hue and their brightness "
                + "ranges overlap, which is not a setting anyone can find by retrying."
                + Environment.NewLine + Environment.NewLine
                + "The numbers beside the globe have none of these problems - exact, no "
                + "calibration, and they cannot confuse life with shield or ward."
                + Environment.NewLine + Environment.NewLine
                + "Set the numbers up now instead?";

            if (_findNumbers is not null
                && MessageBox.Show(this, why, "Tune colours", MessageBoxButtons.YesNo,
                                   MessageBoxIcon.Warning) == DialogResult.Yes)
                _findNumbers();
            else if (_findNumbers is null)
                MessageBox.Show(this, why, "Tune colours", MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
            return;
        }

        _tuned.Text = note;
        RefreshWarning();
        _onChange();

        MessageBox.Show(this,
            $"Tuned: {note}." + Environment.NewLine + Environment.NewLine
            + $"That globe now reads {reads * 100:0.0}% - check that against what it "
            + "actually looks like." + Environment.NewLine + Environment.NewLine
            + "Open Check and watch it track as you spend.",
            "Tune colours", MessageBoxButtons.OK, MessageBoxIcon.Information);

        Preview();
    }

    private void TryTune()
    {
        if (OrbDetector.AutoTune(_cfg, out string note)) _tuned.Text = note;
    }

    /// <summary>
    /// Reports whether the last press actually moved the globe. A run of
    /// presses that change nothing is the clearest signal there is that the
    /// key, the charges, or the input path is the problem rather than the
    /// detection.
    /// </summary>
    public void ShowEffect(bool worked, int noEffectStreak)
    {
        if (worked)
        {
            // Deliberately not "it worked": regen and leech raise the globe too.
            _effect.Text = "Last press: the globe jumped, which looks like a flask.";
            _effect.ForeColor = Color.FromArgb(0, 120, 0);
        }
        else if (noEffectStreak >= 3)
        {
            _effect.Text = $"Last {noEffectStreak} presses moved nothing at all. Check the "
                         + "key, your charges, and Hold each press.";
            _effect.ForeColor = Color.FromArgb(190, 60, 0);
        }
        else if (noEffectStreak > 0)
        {
            _effect.Text = $"Last press: the globe did not move ({noEffectStreak} in a row).";
            _effect.ForeColor = SystemColors.GrayText;
        }
        else
        {
            _effect.Text = "Last press: the globe rose a little - could be regen or leech.";
            _effect.ForeColor = SystemColors.GrayText;
        }
    }

    /// <summary>
    /// The box is reading nothing at all. This is almost always a region that
    /// is not on the globe, and it is silent in every other way: no firing, no
    /// error, indistinguishable from a globe that is simply full.
    /// </summary>
    public void ShowBlind(bool blind)
    {
        _blind = blind;
        if (blind)
        {
            _warn.Text = $"{Text} has read 0% for 8 seconds. If the globe is not empty, "
                       + "this box is not on it - press Set... and drag one around it.";
            _warn.ForeColor = Color.FromArgb(200, 30, 30);
            _warn.Font = new Font(_warn.Font, FontStyle.Bold);
        }
        else
        {
            _warn.Font = new Font(_warn.Font, FontStyle.Regular);
            RefreshWarning();
        }
    }

    /// <summary>
    /// Wipes the last press/would-fire line. It describes a moment, not a
    /// state, so it has to go when the state it was written under changes -
    /// otherwise "Disarmed: would have fired" sits there while armed.
    /// </summary>
    public void ClearStatus()
    {
        _effect.Text = "";
        _effect.ForeColor = SystemColors.GrayText;
    }

    /// <summary>Shown while disarmed, when the trigger point is crossed.</summary>
    public void ShowWouldFire(double frac)
    {
        _effect.Text = $"Would have fired at {frac:P0} - disarmed, so nothing was sent.";
        _effect.ForeColor = Color.FromArgb(0, 90, 160);
    }

    /// <summary>
    /// The numbers keep reading a maximum that is not the one entered. Almost
    /// always a level or a gear change; never adopted automatically, because
    /// the entered value is what protects against misreads.
    /// </summary>
    public void MaxAdopted(int was, int now)
    {
        _settingMax = true;
        _knownMax.Value = Math.Clamp(now, 0, 1_000_000);
        _settingMax = false;

        _warn.Text = was == 0
            ? $"Maximum read as {now:N0} - filled in from the numbers."
            : $"Maximum changed from {was:N0} to {now:N0} - updated to match.";
        _warn.ForeColor = Color.FromArgb(0, 100, 0);
    }

    /// <summary>Re-reads settings that something else has changed.</summary>
    /// <summary>
    /// Lays each row of buttons out from its own widths.
    ///
    /// They were placed at typed coordinates that assumed how wide each label
    /// would come out. Once the window was scaled and the text measured, the
    /// assumption stopped holding and they overlapped - so each row is packed
    /// left to right from where it starts, with the same gap between every
    /// pair, which is also what makes them line up.
    /// </summary>
    public void FitRows()
    {
        foreach (var row in _rows)
        {
            if (row.Length == 0) continue;

            // Right-aligned to the card, as they were, so the panel keeps its
            // shape rather than drifting left.
            int gap = 6;
            int total = row.Sum(c => c.Width) + gap * (row.Length - 1);
            int x = Math.Max(14, Width - 14 - total);

            foreach (var c in row)
            {
                c.Left = x;
                x += c.Width + gap;
            }
        }
    }

    /// <summary>
    /// Follows the width it is given.
    ///
    /// The insides were laid out at typed coordinates for a 382-wide card, so
    /// widening the window left everything huddled against the left edge with
    /// a growing empty strip beside it. The things that should span the card
    /// now do, and the button rows repack themselves.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_bar is null) return;

        int pad = 14;
        int wide = Math.Max(120, Width - pad * 2);

        foreach (var full in new Control[] { _warn, _numbers, _effect, _burstTime,
                                             _shieldRead, _region })
            if (full is not null) full.Width = Math.Max(60, Width - full.Left - pad);

        // The level bar takes what the reading beside it does not need.
        int reading = Math.Max(150, (int)(wide * 0.42));
        _bar.Width = Math.Max(80, wide - reading - 8);
        _pct.Left = _bar.Right + 8;
        _pct.Width = Width - _pct.Left - pad;

        _tuned.Width = Math.Max(80, Width - _tuned.Left - pad);

        FitRows();
    }

    /// <summary>
    /// Takes what the controller box actually says, rather than waiting to be
    /// told it changed.
    ///
    /// Same reason as the send-method box: a selection that never reaches its
    /// handler leaves a setting that disagrees with the screen, and the screen
    /// is the thing somebody looked at and believed.
    /// </summary>
    public void SyncFromControls()
    {
        if (IsDisposed || _pad.IsDisposed) return;

        string says = _pad.SelectedIndex <= 0
            ? ""
            : Gamepad.Buttons[_pad.SelectedIndex - 1].Name;

        if (says == _cfg.PadButton) return;

        Log.Write($"{Text}: the controller box says "
                  + (says.Length == 0 ? "nothing" : says)
                  + $" and the settings said "
                  + (_cfg.PadButton.Length == 0 ? "nothing" : _cfg.PadButton)
                  + " - taking the box");
        _cfg.PadButton = says;
        _onChange();
    }

    public void RefreshFromConfig()
    {
        _region.Text = _cfg.Region.ToString();


        // Find numbers fills the maxima in as it goes, and the boxes were left
        // showing whatever was in them before.
        _settingMax = true;
        _knownMax.Value = Math.Clamp(_cfg.KnownMax, 0, 1_000_000);
        if (_shield is not null)
            _shieldMax.Value = Math.Clamp(_shield.KnownMax, 0, 1_000_000);
        _settingMax = false;

        _numbers.Text = _cfg.TextRegion.IsValid
            ? "Numbers: set - waiting for a reading"
            : "Deciding: globe pixels - these follow energy shield too";
        _numbers.ForeColor = SystemColors.GrayText;
        RefreshWarning();
    }

    /// <summary>Called from the UI thread with the latest reading.</summary>
    public void Update(GlobeReading r)
    {
        if (_warn.Text.Length == 0 && _cfg.EmptyDominance < 0) RefreshWarning();

        if (!r.Ok)
        {
            _pct.Text = r.Note.Length > 0 ? r.Note : "--";
            _bar.Value = 0;
            _bar.Below = false;
            return;
        }
        _bar.Value = r.Fraction;
        _bar.Below = r.Fraction < _cfg.Threshold;
        // The percentage, and what it is a percentage of. "60.1 %" on its own
        // is the number nobody could check, and checking it is what every
        // argument about this came down to.
        string exact = Numbers(r.TextRaw);
        _pct.Text = exact.Length > 0
            ? $"{r.Fraction * 100:0.0} %      {exact}"
            : $"{r.Fraction * 100:0.0} %";

        if (r.FromText && r.TextRaw.Length > 0)
        {
            // Which source decided matters more than the number itself. The
            // globe pixels cannot tell life from energy shield - the shield is
            // drawn over the same globe - while the numbers and memory read the
            // life value specifically.
            //
            // A source being thrown out is reported here too, and that is not
            // good news however well the fallback is coping - so it does not
            // get to be green.
            bool refused = r.TextRaw.Contains("ignored", StringComparison.Ordinal);
            _numbers.Text = $"Deciding: {r.TextRaw}";
            _numbers.ForeColor = refused ? Theme.Warn : Theme.Good;
        }
        else if (r.Note == "cannot read the globe")
        {
            _numbers.Text = "Holding fire: the globe cannot be read - press Numbers...";
            _numbers.ForeColor = Theme.Bad;
        }
        else if (r.Note == "no exact reading yet")
        {
            _numbers.Text = "Holding fire: memory or numbers asked for but never read yet";
            _numbers.ForeColor = Theme.Warn;
        }
        else if (r.Note == "numbers not on screen")
        {
            _numbers.Text = "Numbers not on screen - holding fire until they are back";
            _numbers.ForeColor = Theme.Accent;
        }
        else if (r.Note == "numbers not trusted")
        {
            // Not the same as unreadable, and not worth a red alarm while
            // memory has you. The box is producing pairs - it is just producing
            // the wrong ones, off a neighbouring line - and refusing them is
            // the app working, not failing.
            bool covered = MemoryCovering?.Invoke() ?? false;
            _numbers.Text = covered
                ? "The numbers are misreading - memory is covering. Ctrl+/ re-finds them"
                : "Holding fire: the numbers are misreading - press Ctrl+/ to re-find them";
            _numbers.ForeColor = covered ? Theme.Warn : Theme.Bad;
        }
        else if (_cfg.TextRegion.IsValid)
        {
            // Not "deciding" - it is refusing to decide. The numbers are set up
            // and silent, so nothing is being acted on at all, and saying
            // "globe pixels" read as though they were in charge.
            bool covered = MemoryCovering?.Invoke() ?? false;
            _numbers.Text = covered
                ? "The numbers cannot be read - memory is covering. Ctrl+/ re-finds them"
                : "Holding fire: your numbers are set but cannot be read - press Ctrl+/";
            _numbers.ForeColor = covered ? Theme.Warn : Theme.Bad;
        }
        else
        {
            _numbers.Text = "Deciding: globe pixels - these follow energy shield too";
            _numbers.ForeColor = Theme.Warn;
        }
    }
}
