using System.Diagnostics;
using System.Reflection;

namespace P02;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0xA02;

    /// <summary>Ctrl+/ runs the whole setup, from anywhere, without alt-tabbing.</summary>
    private const int SetupHotkeyId = 0xA03;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_OEM_2 = 0xBF;   // the / key
    private bool _setupHotkeyRegistered;
    private const int WM_HOTKEY = 0x0312;

    private readonly AppConfig _cfg;
    private readonly MonitorEngine _engine;
    private readonly System.Windows.Forms.Timer _critical = new();
    private readonly System.Windows.Forms.Timer _watch = new();
    private readonly System.Windows.Forms.Timer _poll = new();
    private int _criticalLeft = 31;
    private bool _criticalDone;

    private readonly GlobePanel _life;
    private readonly GlobePanel _mana;
    private readonly Button _arm = new();
    private readonly Label _status = new();

    /// <summary>
    /// What the app has to say, beside the window rather than on top of it.
    /// </summary>
    private readonly NoticeBoard _notices = new();

    /// <summary>The turning orb in the corner, which is only ever decoration.</summary>
    private readonly OrbBadge _badge = new();

    /// <summary>Waits for a drag to stop before doing the expensive part.</summary>
    private readonly System.Windows.Forms.Timer _settle = new();
    private readonly Label _focus = new();
    private readonly TextBox _window = new();
    private readonly ComboBox _hotkey = new();
    private readonly NotifyIcon _tray = new();

    /// <summary>Set when quitting for real, so the close question is not asked twice.</summary>
    private bool _reallyQuitting;
    private readonly Label _live = new();
    private readonly NumericUpDown _pollHz = new();
    private readonly ComboBox _ocrEngine = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    /// <summary>
    /// Where every control built by this constructor actually ends up living.
    /// Everything here was written against a bare "Controls.Add" meaning the
    /// form itself, long before tabs existed - rewriting every one of those
    /// call sites was a much larger, much riskier change than moving the
    /// finished result once the form is otherwise built. The one place that
    /// runs again on every resize (Group, below) targets this directly
    /// instead, since re-parenting on every resize is not "once".
    /// </summary>
    private readonly TabPage _gamePage = new("Path of Exile 2");
    private readonly Button _pin = new();
    private OverlayForm? _overlay;
    private bool _hotkeyRegistered;

    public MainForm(AppConfig cfg)
    {
        _cfg = cfg;
        _engine = new MonitorEngine(cfg);

        // Before anything is built. The pool panels ask this when they decide
        // whether to show a key box or a controller button, and they are built
        // in the first hundred lines of this constructor - so setting it near
        // the end meant they asked, got nothing, and drew a key box whatever
        // the setting said.
        GlobePanel.UsingController = () => _cfg.UseController;
        GlobePanel.MemoryCovering = () => _engine.MemoryLocked;

        Text = $"QytOCR  v{Version}";
        // Resizable, and it scrolls. It was a fixed 900x856 that had grown with
        // every feature until it was taller than a 1080p screen, which meant the
        // bottom of it - the arm button, among other things - simply could not
        // be reached on the most common monitor there is.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1210, 856);

        _notices.Visible = true;
        _notices.FollowLog();
        Controls.Add(_notices);

        _pin.SetBounds(864, 6, 24, 22);
        _pin.Text = "P";
        _pin.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        _pin.FlatStyle = FlatStyle.System;
        var tip = new ToolTip();
        Tips.On(_pin, Tips.Pin);
        _pin.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ResetOverlay(); };
        _critical.Interval = 1000;
        _critical.Tick += (_, _) => OnCriticalTick();

        // One small request every five minutes, and no window unless something
        // is actually wrong. A release that fixes a way to die quietly is no
        // use sitting on a server while somebody plays the version it fixes.
        _watch.Interval = 5 * 60 * 1000;
        _watch.Tick += (_, _) =>
        {
            if (_criticalDone || Updater.Critical is not null) return;
            _ = Updater.CheckAsync(this, silent: true, beforeExit: _cfg.SaveNow, ui: false);
        };

        _pin.Click += (_, _) => ToggleOverlay(!(_overlay?.Visible ?? false));
        Controls.Add(_pin);

        // The hint that used to sit here said what the P button's own tooltip
        // says, and it was the thing the third checkbox was clipped against.

        var snap = new CheckBox
        {
            Text = "Snap to the numbers",
            AutoSize = true,
            Checked = cfg.OverlaySnap,
        };
        Controls.Add(snap);
        Tips.On(snap, Tips.OverlaySnap);

        var follow = new CheckBox
        {
            Text = "Move it as my character does",
            AutoSize = true,
            Checked = cfg.SlotAuto,
        };
        follow.CheckedChanged += (_, _) =>
        {
            _cfg.SlotAuto = follow.Checked;
            Save();
            ApplyOverlayOptions();
            PlaceOverlay();
        };
        Controls.Add(follow);
        Tips.On(follow, Tips.FollowBar);



        // Two ways to be anchored, and they cannot both be it.
        snap.CheckedChanged += (_, _) =>
        {
            _cfg.OverlaySnap = snap.Checked;
            if (snap.Checked) { _cfg.SlotAuto = false; follow.Checked = false; }
            Save();
            if (_overlay is { IsDisposed: false })
                ApplyOverlayOptions();
            PlaceOverlay();
        };

        var autoHide = new CheckBox
        {
            Text = "Hide when covered",
            AutoSize = true,
            Checked = cfg.OverlayAutoHide,
        };
        autoHide.CheckedChanged += (_, _) => { _cfg.OverlayAutoHide = autoHide.Checked; Save(); };
        Controls.Add(autoHide);
        Tips.On(autoHide, Tips.OverlayAutoHide);

        // Laid out from their own measured widths, right to left off the pin,
        // so a longer label cannot clip the one beside it.
        int right = _pin.Left - 14;
        foreach (var box in new[] { follow, snap, autoHide })
        {
            box.PerformLayout();
            right -= box.Width;
            box.Location = new Point(right, 9);
            right -= 18;
        }

        var probe = new TextProbe(_engine.TextAvailable, _engine.TextUnavailable,
                                  _engine.ProbeText);

        _life = new GlobePanel("Life", cfg.Life, blue: false, Save,
            () => _cfg.WindowMatch, () => _cfg.Mana.Region, probe, cfg.Shield, FindAllNumbers)
            { Location = new Point(12, 36) };
        _mana = new GlobePanel("Mana", cfg.Mana, blue: true, Save,
            () => _cfg.WindowMatch, () => _cfg.Life.Region, probe, null, FindAllNumbers)
            { Location = new Point(406, 36) };
        int cardH = Math.Max(_life.MinimumHeight, _mana.MinimumHeight);
        _life.Height = cardH;
        _mana.Height = cardH;
        _life.Accent = Theme.Life;
        _mana.Accent = Theme.Mana;
        Controls.Add(_life);
        Controls.Add(_mana);

        // Driven by how tall the cards actually came out, not by a number typed
        // when they were shorter. Adding one row to a panel used to push its
        // contents underneath the arm button, which is a fault that arrives
        // silently and only in the release where the row was added.
        int y = Math.Max(_life.Bottom, _mana.Bottom) + 16;

        _arm.SetBounds(12, y, 208, 60);
        _arm.Font = new Font("Segoe UI Semibold", 14f);
        _arm.FlatStyle = FlatStyle.Flat;
        _arm.Click += (_, _) => _engine.Toggle();
        Controls.Add(_arm);
        Tips.On(_arm, "Arms and disarms. Nothing is ever sent while disarmed.",
            "", "It starts disarmed every launch, on purpose.");

        _status.SetBounds(234, y + 8, 460, 24);
        _status.Font = new Font("Segoe UI Semibold", 10.5f);
        Controls.Add(_status);
        Tips.On(_status, Tips.Status);

        _focus.SetBounds(234, y + 34, 640, 20);
        _focus.ForeColor = SystemColors.GrayText;
        Controls.Add(_focus);
        Tips.On(_focus, Tips.Focus);

        y += 66;
        var winLbl = Cap(new Label { Text = "Only fire while the focused window is", AutoSize = true }, Tips.WindowMatch);
        Controls.Add(winLbl);
        _window.SetBounds(236, y, 190, 24);
        _window.Text = cfg.WindowMatch;
        _window.TextChanged += (_, _) => { _cfg.WindowMatch = _window.Text; Save(); };
        Controls.Add(_window);
        Tips.On(_window, Tips.WindowMatch);

        var clearBtn = new Button { Text = "Any window", Bounds = new Rectangle(430, y, 86, 24) };
        clearBtn.Click += (_, _) => _window.Text = "";
        Controls.Add(clearBtn);
        Tips.On(clearBtn, Tips.AnyWindow);

        var pollLbl = Cap(new Label { Text = "Readings a second", AutoSize = true }, Tips.PollHz);
        Controls.Add(pollLbl);
        _pollHz.SetBounds(586, y, 64, 24);
        _pollHz.Minimum = 5;
        _pollHz.Maximum = 250;
        _pollHz.Increment = 5;
        _pollHz.Value = Math.Clamp(cfg.PollHz, 5, 250);
        _pollHz.ValueChanged += (_, _) => { _cfg.PollHz = (int)_pollHz.Value; Save(); };
        Controls.Add(_pollHz);
        Tips.On(_pollHz, Tips.PollHz);

        var armKeyLbl = Cap(new Label { Text = "Arm key", AutoSize = true }, Tips.ArmKey);
        Controls.Add(armKeyLbl);
        _hotkey.SetBounds(708, y, 52, 24);
        _hotkey.DropDownStyle = ComboBoxStyle.DropDownList;
        _hotkey.Items.AddRange(Enumerable.Range(1, 12).Select(i => (object)$"F{i}").ToArray());
        _hotkey.SelectedItem = cfg.ArmHotkey;
        if (_hotkey.SelectedIndex < 0) _hotkey.SelectedIndex = 7;
        _hotkey.SelectedIndexChanged += (_, _) =>
        {
            _cfg.ArmHotkey = (string)_hotkey.SelectedItem!;
            Save();
            RegisterArmHotkey();
        };
        Controls.Add(_hotkey);
        Tips.On(_hotkey, Tips.ArmKey);

        y += 34;
        var logBtn = new Button { Text = "Open log folder", Bounds = new Rectangle(508, y, 122, 28) };
        logBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppConfig.Dir);
            System.Diagnostics.Process.Start("explorer.exe", AppConfig.Dir);
        };
        Controls.Add(logBtn);
        Tips.On(logBtn, Tips.OpenLog);

        var updBtn = new Button { Text = "Check for updates", Bounds = new Rectangle(182, y, 156, 28) };
        updBtn.Click += async (_, _) =>
            await Updater.CheckAsync(this, silent: false, beforeExit: _cfg.SaveNow);
        Controls.Add(updBtn);
        Tips.On(updBtn, Tips.CheckUpdates);

        var histBtn = new Button { Text = "History", Bounds = new Rectangle(0, 0, 70, 24) };
        histBtn.Click += (_, _) => { using var h = new HistoryForm(Version); h.ShowDialog(this); };
        Controls.Add(histBtn);
        Tips.On(histBtn, Tips.History);

        var findAll = new Button
        {
            Text = "Set it up for me   (Ctrl+/)",
            Bounds = new Rectangle(12, y, 164, 28),
        };
        findAll.Click += (_, _) => FixSetup();
        Controls.Add(findAll);

        Tips.On(findAll, Tips.FindNumbers);


        var upd = new CheckBox
        {
            Text = "Check at launch",
            Bounds = new Rectangle(640, y + 4, 118, 22),
            Checked = cfg.CheckUpdatesOnStart,
        };
        upd.CheckedChanged += (_, _) =>
        { _cfg.CheckUpdatesOnStart = upd.Checked; Save(); };
        Controls.Add(upd);
        Tips.On(upd, Tips.UpdateAtLaunch);

        var hide = new CheckBox
        {
            Text = "Hide from capture",
            Bounds = new Rectangle(762, y + 4, 140, 22),
            Checked = cfg.HideFromCapture,
        };
        hide.CheckedChanged += (_, _) =>
        {
            _cfg.HideFromCapture = hide.Checked;
            Save();
            Native.ExcludeFromCapture(Handle, hide.Checked);
            if (hide.Checked)
                MessageBox.Show(this,
                    "This window is now invisible to screen capture of any kind - "
                    + "screenshots, the Snipping Tool, Discord and OBS included."
                    + Environment.NewLine + Environment.NewLine
                    + "It stops QytOCR being read as a globe if it covers one. Untick it "
                    + "before trying to screenshot or share the window.",
                    "Hide from screen capture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Controls.Add(hide);
        Tips.On(hide, Tips.HideCapture);


        var howBtn = new Button { Text = "How do I use this?", Bounds = new Rectangle(0, 0, 160, 28) };
        howBtn.Click += (_, _) => { using var f = new HowToForm(); f.ShowDialog(this); };
        Controls.Add(howBtn);
        Tips.On(howBtn, Tips.HowTo);

        var checkBtn = new Button { Text = "What is wrong?", Bounds = new Rectangle(0, 0, 150, 28) };
        checkBtn.Click += (_, _) =>
        {
            var found = SelfCheck.Run(_cfg, _engine, Version);
            Told(found.Any(f => f.Stops) ? NoticeBoard.Level.Alert
                     : found.Count > 0 ? NoticeBoard.Level.Warn : NoticeBoard.Level.Info,
                 found.Count == 0 ? "Nothing is wrong." : "What is wrong",
                 SelfCheck.Describe(found));
        };
        Controls.Add(checkBtn);
        Tips.On(checkBtn, Tips.WhatIsWrong);

        var diagBtn = new Button
        {
            Text = "Export diagnostics",
            Bounds = new Rectangle(344, y, 158, 28),
        };
        diagBtn.Click += (_, _) => ExportDiagnostics();
        Controls.Add(diagBtn);


        Tips.On(diagBtn, Tips.Diagnostics);

        var sound = new CheckBox
        {
            Text = "Ding on fire",
            Bounds = new Rectangle(130, y + 35, 96, 22),
            Checked = cfg.SoundOnFire,
        };
        sound.CheckedChanged += (_, _) =>
        {
            _cfg.SoundOnFire = sound.Checked;
            Save();
            if (sound.Checked) _engine.TestSound();
        };
        Controls.Add(sound);
        Tips.On(sound, Tips.Ding);

        var oftenLbl = Cap(new Label { Text = "no more often than", AutoSize = true }, Tips.DingGap);
        Controls.Add(oftenLbl);
        var gap = new NumericUpDown { Bounds = new Rectangle(342, y + 34, 62, 24) };
        gap.Minimum = 0;
        gap.Maximum = 120000;
        gap.Increment = 100;
        gap.Value = Math.Clamp(cfg.SoundGapMs, 0, 120000);
        gap.ValueChanged += (_, _) => { _cfg.SoundGapMs = (int)gap.Value; Save(); };
        Controls.Add(gap);
        Tips.On(gap, Tips.DingGap);
        var msLbl = Cap(new Label { Text = "ms", AutoSize = true }, Tips.DingGap);
        Controls.Add(msLbl);

        var disarmedDing = new CheckBox
        {
            Text = "when disarmed",
            Bounds = new Rectangle(700, y + 36, 116, 22),
            Checked = cfg.SoundWhenDisarmed,
        };
        disarmedDing.CheckedChanged += (_, _) =>
        { _cfg.SoundWhenDisarmed = disarmedDing.Checked; Save(); };
        Controls.Add(disarmedDing);
        Tips.On(disarmedDing, Tips.DingDisarmed);

        var volLbl = Cap(new Label { Text = "volume", AutoSize = true }, Tips.Volume);
        Controls.Add(volLbl);
        var vol = new NumericUpDown { Bounds = new Rectangle(490, y + 34, 56, 24) };
        vol.Minimum = -24;
        vol.Maximum = MonitorEngine.MaxGainDb;
        vol.Value = Math.Clamp(cfg.SoundGainDb, -24, MonitorEngine.MaxGainDb);
        vol.ValueChanged += (_, _) =>
        {
            _cfg.SoundGainDb = (int)vol.Value;
            Save();
            // Play at the new level so it can be judged by ear.
            if (_cfg.SoundOnFire) _engine.SetSoundGain(_cfg.SoundGainDb);
        };
        Controls.Add(vol);
        Tips.On(vol, Tips.Volume);
        var dbLbl = Cap(new Label { Text = $"dB (max +{MonitorEngine.MaxGainDb})", AutoSize = true }, Tips.Volume);
        Controls.Add(dbLbl);

        y += 32;
        var rescan = new Button
        {
            Text = "Re-scan",
            Bounds = new Rectangle(766, y + 32, 96, 26),
        };
        rescan.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Stand at full life and mana before this runs." + Environment.NewLine
                    + Environment.NewLine
                    + "A full pool is the one hint that needs nothing read off the screen: "
                    + "at full, your current value IS your maximum, and that is what it "
                    + "looks for. It is how this can find you on a machine where the "
                    + "numbers will not read at all."
                    + Environment.NewLine + Environment.NewLine
                    + "Ready?",
                    "Re-scan", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information) != DialogResult.OK)
                return;

            Log.Write("memory: manual rescan, at full");
            _engine.RescanMemory();
        };
        Controls.Add(rescan);
        Tips.On(rescan, Tips.Rescan);

        var testBtn = new Button { Text = "Test keys (3s)", Bounds = new Rectangle(12, y, 110, 26) };
        testBtn.Click += (_, _) => TestKeys();
        Controls.Add(testBtn);
        Tips.On(testBtn, Tips.TestKeys);

        y += 32;
        var sendLbl = Cap(new Label { Text = "Send keys by", AutoSize = true }, Tips.SendBy);
        Controls.Add(sendLbl);
        var method = new ComboBox
        {
            Bounds = new Rectangle(64, y, 150, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        // A controller is not a third way of sending a key. A game in
        // controller mode is not listening to the keyboard at all, which is why
        // every press went nowhere and the flasks simply never fired.
        method.Items.AddRange(["Injected input", "Posted to window", "Controller"]);
        method.SelectedIndex = cfg.UseController ? 2
            : cfg.InputMethod.Equals("postmessage", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        void ChoseMethod()
        {
            Log.Write($"input: \"{method.SelectedItem}\" chosen (index {method.SelectedIndex})");
            _cfg.UseController = method.SelectedIndex == 2;
            if (!_cfg.UseController)
                _cfg.InputMethod = method.SelectedIndex == 1 ? "postmessage" : "sendinput";

            // Always, and before anything that might fail. A setting that
            // changes nothing and says nothing cannot be told apart from a
            // setting that was never changed.
            Log.Write($"presses now go by {(_cfg.UseController ? "controller" : _cfg.InputMethod)}");

            if (_cfg.UseController)
            {
                Gamepad.TryAgain();
                if (!Gamepad.Available)
                    Told(NoticeBoard.Level.Alert, "There is no virtual controller to press",
                         Gamepad.Why);
                else
                    Told(NoticeBoard.Level.Info, "A virtual controller is connected",
                         "Pick the button each flask sits on, on its own panel. The game "
                         + "sees the presses as coming from a pad, which is the only thing "
                         + "it listens to while you are playing on one.");
            }

            _life.RefreshFromConfig();
            _mana.RefreshFromConfig();
            Save();
        }

        method.SelectedIndexChanged += (_, _) => ChoseMethod();
        method.SelectionChangeCommitted += (_, _) => ChoseMethod();

        // And, because neither of those has ever once fired on this machine.
        //
        // The wiring above is correct and the events are attached to the box
        // that is on screen - and the box has been sitting on "Controller"
        // through three releases while the settings file said otherwise and the
        // log recorded no choice at all. Something between the click and the
        // handler is eating it, and four attempts at guessing what have each
        // cost a release and changed nothing.
        //
        // So this stops asking to be told. Once a second it reads what the box
        // actually says and makes that true. It is not elegant; it cannot fail
        // to notice.
        _poll.Interval = 1000;
        _poll.Tick += (_, _) =>
        {
            if (IsDisposed || method.IsDisposed) return;
            bool shown = method.SelectedIndex == 2;
            string shouldBe = method.SelectedIndex == 1 ? "postmessage" : "sendinput";

            _life.SyncFromControls();
            _mana.SyncFromControls();

            if (shown == _cfg.UseController
                && (shown || _cfg.InputMethod.Equals(shouldBe, StringComparison.OrdinalIgnoreCase)))
                return;

            Log.Write($"input: the box says \"{method.SelectedItem}\" and the settings said "
                      + $"{(_cfg.UseController ? "controller" : _cfg.InputMethod)} - "
                      + "taking the box");
            ChoseMethod();
        };
        _poll.Start();

        // Connected now, not at the first press.
        //
        // A game enumerates controllers when it starts and when one arrives. A
        // pad that springs into existence in the same instant as the press it
        // is carrying has not been seen by anything yet, and that press goes
        // nowhere - which is indistinguishable from the press not working.
        if (cfg.UseController)
        {
            Task.Run(() =>
            {
                if (Gamepad.Available) return;
                Log.Write($"controller: {Gamepad.Why}");
            });
        }

        Log.Write($"input: presses go by "
                  + (cfg.UseController ? "controller" : cfg.InputMethod)
                  + $", life button \"{cfg.Life.PadButton}\", mana button "
                  + $"\"{cfg.Mana.PadButton}\"");

        Controls.Add(method);
        Tips.On(method, Tips.SendBy);

        // Which pad to be, and whether to send the key as well.
        //
        // Both exist because of Steam Input, which reads your controller and
        // hands the game one of its own - so a third pad turning up is at the
        // mercy of what Steam decides it is. Neither of these can be reasoned
        // out from here; they are two things to try, and trying them should
        // not mean editing a settings file.
        var padKindLbl = Cap(new Label { Text = "Pretend to be", AutoSize = true },
                             Tips.PadKind);
        Controls.Add(padKindLbl);

        var padKind = new ComboBox
        {
            Bounds = new Rectangle(0, 0, 150, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        // Three entries, and the third is honest about what it is.
        //
        // No program can present itself as a Steam Controller: the virtual pad
        // driver offers an Xbox pad and a PlayStation pad and nothing else, and
        // Steam knows its own controller by Valve's hardware and protocol. What
        // CAN reach a game played through Steam Input is a pad Steam takes in
        // as one more controller of its own - so "through Steam" presses the
        // Xbox pad, and says plainly, when chosen, what Steam must be set to
        // for that to work.
        padKind.Items.AddRange(["An Xbox pad", "A PlayStation pad",
                                "A Steam Controller (through Steam)"]);
        padKind.SelectedIndex =
            cfg.PadKind.Equals("sony", StringComparison.OrdinalIgnoreCase) ? 1
            : cfg.PadKind.Equals("steam", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        Gamepad.Kind = cfg.PadKind;
        Controls.Add(padKind);
        Tips.On(padKind, Tips.PadKind);

        var alsoKey = new CheckBox
        {
            Text = "Send the key too",
            Checked = cfg.AlsoPressKey,
            AutoSize = true,
        };
        Controls.Add(alsoKey);
        Tips.On(alsoKey, Tips.AlsoKey);

        // Which HUD the game is showing, and so which output to use.
        //
        // The two layouts are nothing alike - the pad bar with its coloured
        // face buttons along the bottom centre, or the flasks in their own box
        // beside the life globe - and the game switches between them the
        // moment you touch the other device. Keeping "Send keys by" in step by
        // hand meant presses going to a device the game had stopped listening
        // to.
        var matchMode = new CheckBox
        {
            Text = "Match the game's mode",
            Checked = cfg.FollowGameMode,
            AutoSize = true,
        };
        Controls.Add(matchMode);
        Tips.On(matchMode, Tips.FollowMode);

        var lastSeen = ModeDetector.Mode.Unknown;
        int agreeing = 0;
        long lookedAt = 0;
        bool looking = false;

        _poll.Tick += (_, _) =>
        {
            if (IsDisposed || matchMode.IsDisposed) return;

            if (matchMode.Checked != _cfg.FollowGameMode)
            {
                _cfg.FollowGameMode = matchMode.Checked;
                Log.Write($"input: following the game's mode is "
                          + (matchMode.Checked ? "on" : "off"));
                Save();
            }

            if (!_cfg.FollowGameMode || looking) return;

            long now = Environment.TickCount64;
            if (now - lookedAt < 3000) return;
            lookedAt = now;

            if (Native.FindWindowRect(_cfg.WindowMatch) is not { } game) return;

            // A screen capture, so off the window's own thread.
            looking = true;
            Task.Run(() => ModeDetector.DetectNow(game)).ContinueWith(done =>
            {
                looking = false;
                if (done.IsFaulted || IsDisposed || !IsHandleCreated) return;
                var seen = done.Result;

                try
                {
                    BeginInvoke(() =>
                    {
                        // A loading screen or a menu shows neither HUD, and
                        // says nothing about which device is in your hands.
                        if (seen == ModeDetector.Mode.Unknown) { agreeing = 0; return; }

                        agreeing = seen == lastSeen ? agreeing + 1 : 1;
                        lastSeen = seen;

                        // Two looks, three seconds apart, before anything
                        // changes - one odd frame is not a change of device.
                        if (agreeing < 2) return;

                        bool pad = seen == ModeDetector.Mode.Controller;
                        if (pad == (method.SelectedIndex == 2)) return;

                        method.SelectedIndex = pad ? 2
                            : _cfg.InputMethod.Equals("sendinput", StringComparison.OrdinalIgnoreCase)
                                ? 0 : 1;

                        Log.Write($"input: the game is showing its "
                                  + (pad ? "controller" : "keyboard") + " HUD - presses now go by "
                                  + (pad ? "controller" : "keyboard"));
                        Told(NoticeBoard.Level.Info,
                             pad ? "The game switched to your controller"
                                 : "The game switched to your keyboard",
                             pad ? "Presses now go to the controller."
                                 : "Presses now go to the keyboard.");
                    });
                }
                catch (ObjectDisposedException) { /* closing */ }
                catch (InvalidOperationException) { /* closing */ }
            });
        };

        // Read rather than awaited, the same as the send method above, because
        // on this machine a box can be changed without its handler ever
        // hearing about it.
        _poll.Tick += (_, _) =>
        {
            if (IsDisposed || padKind.IsDisposed) return;

            string kind = padKind.SelectedIndex switch { 1 => "sony", 2 => "steam", _ => "xbox" };
            if (kind != _cfg.PadKind)
            {
                Log.Write($"controller: pretending to be " + kind switch
                {
                    "sony" => "a PlayStation pad",
                    "steam" => "a controller Steam takes in (an Xbox pad underneath)",
                    _ => "an Xbox pad",
                });

                if (kind == "steam")
                    Told(NoticeBoard.Level.Warn, "Steam Controller mode - what it really does",
                         "Nothing can pretend to be a Steam Controller: the only virtual "
                         + "pads Windows supports are Xbox and PlayStation. This presses a "
                         + "virtual Xbox pad for Steam to take in as one more controller. "
                         + "For the game to act on it, Steam > Settings > Controller must "
                         + "have Xbox support on (it shows up there as \"Xbox 360 "
                         + "Controller\"), and Path of Exile 2 must accept a second "
                         + "controller. If presses still do nothing, the route that works "
                         + "is Steam's gamepad template or SISR with Steam Input off for "
                         + "the game only - your controller keeps working that way.");
                _cfg.PadKind = kind;
                Gamepad.Kind = kind;
                Gamepad.Rebuild();
                if (_cfg.UseController) _ = Gamepad.Available;
                Save();
            }

            if (alsoKey.Checked != _cfg.AlsoPressKey)
            {
                _cfg.AlsoPressKey = alsoKey.Checked;
                Log.Write($"controller: the key is {(alsoKey.Checked ? "also" : "no longer")} sent");
                Save();
            }
        };

        var postedNote = Cap(new Label { Text = "Posting reaches a window that is not focused.", AutoSize = true, ForeColor = SystemColors.GrayText }, Tips.SendBy);
        Controls.Add(postedNote);

        var numbersOnly = new CheckBox
        {
            Text = "Numbers only",
            Bounds = new Rectangle(766, y + 2, 120, 22),
            Checked = cfg.NumbersOnly,
        };
        numbersOnly.CheckedChanged += (_, _) =>
        {
            _cfg.NumbersOnly = numbersOnly.Checked;
            Save();
            _life.RefreshFromConfig();
            _mana.RefreshFromConfig();
        };
        Controls.Add(numbersOnly);
        Tips.On(numbersOnly, Tips.NumbersOnly);

        var mem = new CheckBox
        {
            Text = "Read game memory",
            Bounds = new Rectangle(608, y + 2, 150, 22),
            Checked = cfg.UseMemory,
        };
        Log.Write($"startup: Read game memory checkbox built as "
                  + $"{(cfg.UseMemory ? "checked" : "unchecked")} (config said UseMemory={cfg.UseMemory})");
        mem.CheckedChanged += (_, _) =>
        {
            Log.Write($"Read game memory: checkbox changed to {mem.Checked}");
            if (mem.Checked && MessageBox.Show(this,
                    "This reads life and mana straight out of the game's memory. It is exact "
                    + "and instant, and it is the most intrusive thing here by a distance: "
                    + "reading another process is what anti-cheat looks for, where watching "
                    + "the screen is passive."
                    + Environment.NewLine + Environment.NewLine
                    + "It finds the values by searching for their shape rather than using "
                    + "fixed offsets, so it survives patches, and it takes a couple of "
                    + "seconds on first use."
                    + Environment.NewLine + Environment.NewLine + "Turn it on?",
                    "Read game memory", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                Log.Write("Read game memory: declined the warning dialog - turning back off");
                mem.Checked = false;
                return;
            }
            _engine.SetMemory(mem.Checked);
            Save();
            Log.Write($"Read game memory: saved UseMemory={_cfg.UseMemory}");
        };
        Controls.Add(mem);
        Tips.On(mem, Tips.Memory);

        var ocrEngineLbl = Cap(new Label { Text = "Read the numbers with", AutoSize = true },
                               Tips.OcrEngine);
        Controls.Add(ocrEngineLbl);
        _ocrEngine.SetBounds(766, y + 32, 150, 24);
        _ocrEngine.Items.AddRange(["Windows (built in)", "Tesseract (light)", "PaddleOCR (accurate)"]);
        _ocrEngine.SelectedIndex = cfg.OcrEngine switch
        {
            "tesseract" => 1,
            "paddle" => 2,
            _ => 0,
        };
        _ocrEngine.SelectedIndexChanged += (_, _) =>
        {
            _cfg.OcrEngine = _ocrEngine.SelectedIndex switch
            {
                1 => "tesseract",
                2 => "paddle",
                _ => "windows",
            };
            Save();
            _engine.SetOcrEngine(_cfg.OcrEngine);
            Log.Write($"ocr: reading with {_ocrEngine.Text}");
        };
        Controls.Add(_ocrEngine);
        Tips.On(_ocrEngine, Tips.OcrEngine);

        y += 32;
        var shareBtn = new Button
        {
            Text = "Share settings",
            Bounds = new Rectangle(12, y, 130, 26),
        };
        shareBtn.Click += (_, _) => ShareSettings();
        Controls.Add(shareBtn);
        Tips.On(shareBtn, Tips.ShareSettings);

        var applyBtn = new Button
        {
            Text = "Apply shared",
            Bounds = new Rectangle(150, y, 130, 26),
        };
        applyBtn.Click += (_, _) => ApplyShared();
        Controls.Add(applyBtn);
        Tips.On(applyBtn, Tips.ApplyShared);

        y += 32;
        _live.SetBounds(12, y, 876, 20);
        _live.ForeColor = SystemColors.GrayText;
        Controls.Add(_live);
        Tips.On(_live, Tips.Live);

        // So the engine never re-reads our own window instead of the game.
        Move += (_, _) => _engine.OwnWindow = Bounds;
        Resize += (_, _) => _engine.OwnWindow = Bounds;
        VisibleChanged += (_, _) => _engine.OwnWindow = Visible ? Bounds : Rectangle.Empty;

        _engine.Sampled += OnSampled;
        _engine.ArmedChanged += _ => BeginInvoke(RefreshArmUi);
        _engine.Fired += (pool, _) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (_overlay is { IsDisposed: false, Visible: true })
                        _overlay.Fired(pool);
                });
            }
            catch (ObjectDisposedException) { /* closing */ }
        };

        _engine.MaxAdopted += (name, was, now) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (name == "Life") _life.MaxAdopted(was, now);
                    else if (name == "Mana") _mana.MaxAdopted(was, now);
                    _cfg.SaveNow();
                });
            }
            catch (ObjectDisposedException) { /* closing */ }
        };

        _engine.Blind += (name, blind) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() => (name == "Life" ? _life : _mana).ShowBlind(blind));
            }
            catch (ObjectDisposedException) { /* closing */ }
        };

        _engine.WouldFire += (name, frac) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() => (name == "Life" ? _life : _mana).ShowWouldFire(frac));
            }
            catch (ObjectDisposedException) { /* closing */ }
        };

        _engine.EffectChecked += (name, worked, streak) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    var panel = name == "Life" ? _life : _mana;
                    panel.ShowEffect(worked, streak);
                });
            }
            catch (ObjectDisposedException) { /* closing */ }
        };

        // Come back where it was left, unless that screen has since gone away.
        if (cfg.WindowX >= 0 && cfg.WindowY >= 0)
        {
            var spot = new Rectangle(cfg.WindowX, cfg.WindowY, Width, Height);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(spot)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(cfg.WindowX, cfg.WindowY);
            }
        }

        _lastKnownLife = cfg.Life.KnownMax;
        _lastKnownMana = cfg.Mana.KnownMax;

        BackColor = Theme.Bg;
        Icon = AppIcon.Load();

        // The backdrop is one bitmap redrawn on resize, so it has to be told
        // to repaint - and double buffering, or a window this busy tears.
        DoubleBuffered = true;
        ResizeRedraw = true;

        _badge.SetBounds(10, 4, 26, 26);
        Controls.Add(_badge);
        Tips.On(_badge, Tips.Badge);
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        // Rows, written out, rather than a bag of controls packed until they
        // wrap. Wrapping put a spin box under a checkbox and a unit label at
        // the start of a line; saying which things belong on a line together
        // is the whole of the difference between tidy and ragged.
        Regroup(
            [[findAll, checkBtn], [howBtn], [updBtn, histBtn, upd], [diagBtn, logBtn]],
            [[numbersOnly], [mem], [ocrEngineLbl, _ocrEngine], [rescan], [pollLbl, _pollHz]],
            [[winLbl], [_window, clearBtn], [armKeyLbl, _hotkey],
             [sendLbl, method], [matchMode], [padKindLbl, padKind], [alsoKey],
             [postedNote], [testBtn]],
            [[sound, disarmedDing], [oftenLbl, gap, msLbl], [volLbl, vol, dbLbl]],
            [[shareBtn, applyBtn], [hide]]);

        Theme.Apply(this);

        // After the general styling, or it would paint over them.
        Theme.Primary(findAll, Theme.Accent);
        Theme.Primary(updBtn, Theme.Good);
        RefreshArmUi();

        _life.Accent = Theme.Bad;
        _mana.Accent = Theme.Accent;
        _arm.Font = Theme.Big;
        _status.Font = Theme.UiBold;
        _live.Font = Theme.Small;
        _pin.Font = Theme.UiBold;

        SetupTray();
        if (cfg.OverlayOn) ToggleOverlay(true);

        // Same again for the window itself: fit the contents rather than
        // assume them.
        // Everything above was laid out at a comfortable desktop size, which is
        // more room than this needs - it is a monitor, not a document. Scaling
        // the built window is the honest way to shrink it: every control keeps
        // its proportions and its relationship to its neighbours, where
        // retyping four hundred coordinates would not.
        Scale(new SizeF(UiScale, UiScale));

        // Scaling cannot know that "Export diagnostics" needs more room than
        // "Open log", so anything whose words no longer fit is grown to fit
        // them. Measured, not guessed at.
        Theme.FitText(this);
        _life.FitRows();
        _mana.FitRows();
        Relayout();

        // Fit the contents, then fit the screen. Whichever is smaller wins, and
        // what does not fit scrolls rather than falling off the bottom.
        int deepest = Controls.Cast<Control>().ToArray().Max(c => c.Bottom);
        var room = (Screen.FromControl(this) ?? Screen.PrimaryScreen!).WorkingArea;

        MinimumSize = new Size(S(760), S(420));

        int wantW = _cfg.WindowW > 0 ? _cfg.WindowW : ClientSize.Width;
        int wantH = _cfg.WindowH > 0 ? _cfg.WindowH : deepest + 12;

        ClientSize = new Size(Math.Clamp(wantW, MinimumSize.Width, room.Width - 40),
                              Math.Clamp(wantH, MinimumSize.Height, room.Height - 60));

        ResizeEnd += (_, _) =>
        {
            // Let go of the edge and it catches up at once rather than waiting
            // out the timer.
            _settle.Stop();
            if (WindowState != FormWindowState.Minimized)
            {
                Backdrop.Forget();
                Relayout();
                Invalidate();
            }

            if (WindowState != FormWindowState.Normal) return;
            _cfg.WindowW = ClientSize.Width;
            _cfg.WindowH = ClientSize.Height;
            Save();
        };

        // The groups reflow to the width they are given, so anything that
        // changes it lays them out again - but not on every message.
        //
        // Laying out means rebuilding the cards: five panels disposed and
        // remade, every control in the window re-parented, and the backdrop
        // redrawn at the new size. Windows sends a resize message for every
        // pixel of a drag, so dragging an edge was asking for all of that a
        // hundred times a second, which is exactly how the window came to feel
        // like it was wading.
        //
        // A tenth of a second of stillness is imperceptible when you are
        // dragging, and it turns a hundred rebuilds into one.
        _settle.Interval = 90;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (WindowState == FormWindowState.Minimized) return;
            Backdrop.Forget();
            Relayout();
            Invalidate();
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) return;
            _settle.Stop();
            _settle.Start();
        };

        Relayout();

        // Everything built above landed directly on the form, the only place
        // "Controls.Add" without a receiver could have meant before tabs
        // existed. Swept into the first tab now, in one move, rather than
        // rewriting every call site above to say so itself. The five cards
        // are not in this list - Group() already puts them straight into
        // _gamePage, since it runs again on every resize and "reparent once"
        // does not apply to it.
        foreach (var c in Controls.Cast<Control>().ToList()) _gamePage.Controls.Add(c);
        _gamePage.AutoScroll = true;
        _gamePage.UseVisualStyleBackColor = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(_gamePage);
        tabs.TabPages.Add(new TabPage("TBD Game #1"));
        tabs.TabPages.Add(new TabPage("TBD Game #2"));
        Controls.Add(tabs);
        Theme.Apply(this);

        // Re-applied: the walk above just re-themed every button as a plain
        // one, including these two - the exact "after the general styling,
        // or it would paint over them" problem the first call already knew
        // about, reintroduced by adding a second walk for the tab control.
        Theme.Primary(findAll, Theme.Accent);
        Theme.Primary(updBtn, Theme.Good);

        RefreshArmUi();
        _engine.Start();
    }

    /// <summary>
    /// Puts the lower half into named groups.
    ///
    /// It had grown a row at a time over fifty releases, so related things sat
    /// far apart and unrelated things sat together - the update checkbox beside
    /// the capture one, the ding volume beside the send method. Nobody could
    /// answer "where do I change how it reads?" by looking.
    ///
    /// Five groups, each a question somebody actually asks: getting started,
    /// what it reads from, when it is allowed to fire, whether it makes a
    /// sound, and moving a setup between machines. The headings do the work the
    /// tooltips were carrying alone.
    /// </summary>
    /// <summary>How much of the original size the window is drawn at.</summary>
    private const float UiScale = 0.92f;

    private static int S(int v) => (int)Math.Round(v * UiScale);

    private Control[][][]? _groups;
    private readonly List<Card> _groupCards = [];

    private bool _laying;

    /// <summary>
    /// Says something on the board beside the window.
    ///
    /// This used to be a MessageBox every time. A box stops the game, has to be
    /// dismissed before anything else can happen, and erases itself the moment
    /// it is - so the one occasion it said something that mattered, it said it
    /// mid-fight and then removed the evidence. And a box that appears often is
    /// a box people learn to click through without reading, which is precisely
    /// what happened to the setup warning.
    /// </summary>
    private void Told(NoticeBoard.Level level, string what, string detail = "")
    {
        if (InvokeRequired) { BeginInvoke(() => Told(level, what, detail)); return; }
        _notices.Say(level, what, detail);
    }

    private void Relayout()
    {
        if (_groups is null) return;

        // Laying out changes control sizes, which raises more resize events.
        // Without this the rebuild re-entered itself and tore the collection
        // apart underneath its own enumeration - "collection was modified".
        if (_laying) return;
        _laying = true;
        try
        {

        // The pair of pool cards share the width evenly, with the same margin
        // outside them as between them. They were two fixed 382-wide panels at
        // fixed coordinates, so any other window width left them off-centre
        // with a growing empty strip down one side.
        int Edge = S(12), Gap = S(10);

        // The board keeps a column of its own down the right, and everything
        // else lays out inside what is left. Narrow the window far enough and
        // it steps aside rather than squeezing the controls into strips.
        int boardW = ClientSize.Width >= S(1000) ? S(300) : 0;
        _notices.Visible = boardW > 0;
        if (boardW > 0)
            _notices.SetBounds(ClientSize.Width - Edge - boardW, S(44),
                               boardW, ClientSize.Height - S(56));

        int room = ClientSize.Width - (boardW > 0 ? boardW + Gap : 0);
        int half = (room - Edge * 2 - Gap) / 2;

        _life.SetBounds(Edge, _life.Top, half, _life.Height);
        _mana.SetBounds(Edge + half + Gap, _mana.Top, half, _mana.Height);

        // The arm button keeps its size; the status beside it takes the rest.
        _status.SetBounds(_arm.Right + S(14), _status.Top,
                          room - _arm.Right - S(26), _status.Height);
        _focus.SetBounds(_arm.Right + S(14), _focus.Top,
                         room - _arm.Right - S(26), _focus.Height);

        Regroup(_groups[0], _groups[1], _groups[2], _groups[3], _groups[4]);

        // The board used to reach all the way down to the bottom of the
        // window regardless of how tall everything beside it actually was -
        // which is what made a five-line log look like it was sitting in a
        // column built for fifty. Ending it level with the last row instead
        // means both columns share a bottom edge, which reads as the layout
        // rather than as the board having nothing left to say.
        if (boardW > 0)
            _notices.Height = Math.Max(S(120),
                Math.Min(ClientSize.Height - S(56), _live.Bottom - _notices.Top));
        }
        finally { _laying = false; }
    }

    private void Regroup(Control[][] setup, Control[][] reading, Control[][] firing,
                         Control[][] sound, Control[][] sharing)
    {
        _groups = [setup, reading, firing, sound, sharing];

        // Rebuilt from scratch each time, so the old cards go first - and the
        // controls have to come out of them before they are disposed, or they
        // are disposed along with them.
        foreach (var old in _groupCards)
        {
            while (old.Controls.Count > 0) old.Controls[0].Parent = this;
            Controls.Remove(old);
            old.Dispose();
        }
        _groupCards.Clear();

        int top = Math.Max(_arm.Bottom, _focus.Bottom) + S(14);
        int Gap = S(10), Edge = S(12);

        // Three columns where there is room for them, two where there is not.
        // A narrow window with three columns is three columns of wrapped
        // single words.
        int boardW = _notices.Visible ? _notices.Width + Gap : 0;
        int room = ClientSize.Width - boardW;
        int usable = room - Edge * 2;
        int columns = usable >= S(780) ? 3 : 2;
        int wide = (usable - Gap * (columns - 1)) / columns;

        var titles = new[] { "Getting started", "What it reads", "When it may fire",
                             "Sound", "Moving this setup" };
        var contents = new[] { setup, reading, firing, sound, sharing };

        int x = Edge, rowTop = top, rowBottom = top, column = 0;
        var row = new List<Card>();

        for (int i = 0; i < titles.Length; i++)
        {
            // The last card on a row takes the remaining pixels, so the right
            // edge lines up with the left one instead of leaving a ragged gap.
            bool last = column == columns - 1 || i == titles.Length - 1;
            int w = last ? room - Edge - x : wide;

            var card = Group(titles[i], x, rowTop, w, contents[i]);
            _groupCards.Add(card);
            row.Add(card);
            rowBottom = Math.Max(rowBottom, card.Bottom);

            if (++column < columns && i < titles.Length - 1)
            {
                x = card.Right + Gap;
            }
            else
            {
                // Every card on a row ends level with its neighbours. Cards of
                // three different heights side by side is what made the bottom
                // half look like it had been assembled from spare parts.
                foreach (var c in row) c.Height = rowBottom - c.Top;
                row.Clear();

                column = 0;
                x = Edge;
                rowTop = rowBottom + Gap;
            }
        }

        _live.SetBounds(Edge, rowBottom + Gap, ClientSize.Width - Edge * 2, S(20));
    }

    /// <summary>
    /// One titled group, filled left to right and wrapped, so a longer label
    /// pushes its neighbour along instead of landing on top of it.
    /// </summary>
    /// <summary>
    /// One titled group, a row at a time.
    ///
    /// Each row is written out at the call site, so things that belong
    /// together stay together and nothing wraps into a line of its own. A row
    /// that is too wide for the card still wraps rather than running off the
    /// edge, but that is a last resort rather than the layout.
    /// </summary>
    private Card Group(string title, int x, int y, int width, Control[][] rows)
    {
        var card = new Card { Text = title, Bounds = new Rectangle(x, y, width, S(40)) };
        _gamePage.Controls.Add(card);
        card.SendToBack();

        int pad = S(12), line = S(30);
        int cy = S(34);

        foreach (var row in rows)
        {
            int cx = pad;
            int tallest = 0;

            foreach (var item in row)
            {
                item.Parent = card;
                if (item is Label or CheckBox) item.AutoSize = true;
                item.PerformLayout();

                if (cx > pad && cx + item.Width > width - pad)
                {
                    cx = pad;
                    cy += line;
                    tallest = 0;
                }

                // Labels sit on the middle of the boxes they name rather than
                // the top of them, which is where they read as captions.
                int lift = item is Label ? (S(24) - item.Height) / 2 : 0;
                item.Location = new Point(cx, cy + lift);

                cx += item.Width + (item is Label ? S(6) : S(10));
                tallest = Math.Max(tallest, item.Height);
            }

            cy += Math.Max(line, tallest + S(6));
        }

        card.Height = cy + S(4);
        return card;
    }

    /// <summary>
    /// A caption carrying the same explanation as the field it names. The
    /// caption is the part you read, so it is the part the pointer lands on,
    /// and finding nothing there reads as nothing to find.
    /// </summary>
    private static Label Cap(Label l, params string[] tip)
    {
        Tips.On(l, tip);
        return l;
    }

    /// <summary>Puts this setup on the clipboard, and in a file beside the log.</summary>
    private void ShareSettings()
    {
        string text = SettingsShare.Export(_cfg, Version);
        string path = Path.Combine(AppConfig.Dir, $"QytOCR-settings-{Version}.txt");

        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.WriteAllText(path, text);
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Told(NoticeBoard.Level.Alert, "Could not write the settings out", ex.Message);
            return;
        }

        Log.Write($"settings exported to {path}");
        Told(NoticeBoard.Level.Info, "Settings copied to the clipboard",
            "Saved as " + path + ". "
            + "It carries every setting except the screen regions and your own "
            + "maxima - those belong to this machine and this character, and are "
            + "found again wherever it is loaded."
            + Environment.NewLine + Environment.NewLine
            + $" It only loads into v{Version}.");
    }

    /// <summary>Reads a shared block off the clipboard and applies it.</summary>
    private void ApplyShared()
    {
        string text = "";
        try { text = Clipboard.GetText(); } catch { /* nothing on it */ }

        if (string.IsNullOrWhiteSpace(text))
        {
            Told(NoticeBoard.Level.Warn, "Nothing to apply",
                 "Copy the exported settings text first - all of it, it is one line.");
            return;
        }

        string? why = SettingsShare.Import(text, _cfg, Version);
        if (why is not null)
        {
            Told(NoticeBoard.Level.Warn, "Those settings were not applied", why);
            return;
        }

        _cfg.SaveNow();
        Log.Write("settings imported from the clipboard");

        MessageBox.Show(this,
            "Settings applied. QytOCR will restart to pick them up - it comes back "
            + "disarmed, so arm it when you are ready.",
            "Apply shared settings", MessageBoxButtons.OK, MessageBoxIcon.Information);

        Application.Restart();
        Environment.Exit(0);
    }

    private static string Version =>
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Any settings edit: persist it and re-render the summary line.
    /// Without the refresh, ticking a globe on while armed left the status text
    /// stale and it looked like arming had been lost.</summary>
    private int _lastKnownLife, _lastKnownMana;

    private void Save()
    {
        // The life flask is what recovers energy shield, so the shield watcher
        // uses the same key and the same timing rather than a second set of
        // settings that could quietly disagree with it.
        _cfg.Shield.Key = _cfg.Life.Key;
        _cfg.Shield.HoldMs = _cfg.Life.HoldMs;
        _cfg.Shield.CooldownMs = _cfg.Life.CooldownMs;
        _cfg.Shield.PanicCooldownMs = _cfg.Life.PanicCooldownMs;
        _cfg.Shield.BurstCount = _cfg.Life.BurstCount;
        _cfg.Shield.BurstGapMs = _cfg.Life.BurstGapMs;
        // Panic is a rule about how hard you are being hit, not about which
        // pool is being hit, so the shield uses the same one as life.
        _cfg.Shield.PanicBelow = _cfg.Life.PanicBelow;
        _cfg.Shield.UberBelow = _cfg.Life.UberBelow;
        _cfg.Shield.FastDropPctPerSec = _cfg.Life.FastDropPctPerSec;
        _cfg.Shield.ConfirmFrames = _cfg.Life.ConfirmFrames;
        _cfg.Shield.IgnoreBelow = _cfg.Life.IgnoreBelow;
        _cfg.Shield.BlindGraceMs = _cfg.Life.BlindGraceMs;
        _cfg.Shield.RequireTextMs = _cfg.Life.RequireTextMs;

        _cfg.Save();
        _engine.SyncTextRegions();

        // Telling it your maximum is the whole basis of the memory search, so
        // changing it should start a new one rather than wait to be asked.
        if (_cfg.Life.KnownMax != _lastKnownLife || _cfg.Mana.KnownMax != _lastKnownMana)
        {
            _lastKnownLife = _cfg.Life.KnownMax;
            _lastKnownMana = _cfg.Mana.KnownMax;
            if (_cfg.UseMemory)
            {
                Log.Write($"memory: max changed to life {_lastKnownLife}, "
                          + $"mana {_lastKnownMana} - searching again");
                _engine.RescanMemory();
            }
        }

        RefreshArmUi();
    }

    /// <summary>Shows or hides the small always-on-top readout.</summary>
    private void ToggleOverlay(bool on)
    {
        if (on)
        {
            if (_overlay is null || _overlay.IsDisposed)
            {
                _overlay = new OverlayForm();
                // Remember where it was dropped, so it comes back there rather
                // than only being saved when the app closes.
                _overlay.ResetAsked += ResetOverlay;
                _overlay.RememberAsked += RememberSpot;
                _overlay.SlotChosen += which =>
                {
                    _cfg.Slot = Math.Clamp(which, 0, 2);
                    _cfg.SlotAuto = false;
                    Save();
                    ApplyOverlayOptions();
                    PlaceOverlay();
                };
                _overlay.SlotAutoChanged += on =>
                {
                    _cfg.SlotAuto = on;
                    Save();
                    ApplyOverlayOptions();
                };
                _overlay.OptionsChanged += (locked, through) =>
                {
                    if (locked != _cfg.OverlayLocked || through != _cfg.OverlayClickThrough)
                        Log.Write($"overlay: {(locked ? "locked" : "unlocked")}, click through "
                                  + $"{(through ? "on" : "off")}");
                    _cfg.OverlayLocked = locked;
                    _cfg.OverlayClickThrough = through;

                    // Now, not on the next timer tick. An update can arrive in
                    // between, and a setting changed a moment before a restart is
                    // exactly the one that has to survive it.
                    _cfg.SaveNow();
                };
                _overlay.ManaShownChanged += show =>
                {
                    _cfg.OverlayShowMana = show;
                    Save();
                };
                _overlay.Moved += () =>
                {
                    if (_overlay is not { IsDisposed: false }) return;
                    _cfg.OverlayX = _overlay.Location.X;
                    _cfg.OverlayY = _overlay.Location.Y;

                    // While following, a drag is choosing where it sits
                    // relative to the bar rather than on the screen.
                    // A drag is how a position is set, so it lands in whichever
                    // of the three is currently chosen - along with where the
                    // character was standing at the time, which is what tells
                    // the three apart later.
                    int slot = Math.Clamp(_cfg.Slot, 0, 2);
                    _cfg.SlotX[slot] = _overlay.Location.X;
                    _cfg.SlotY[slot] = _overlay.Location.Y;

                    var here = _engine.CharacterBar;
                    if (here.Width > 0 && _cfg.SlotBarX.Length == 3)
                        _cfg.SlotBarX[slot] = here.X + here.Width / 2;

                    _cfg.Save();
                };
            }

            ApplyOverlayOptions();
            PlaceOverlay();
            _overlay.SetArmed(_engine.Armed);
            _overlay.Show();
            _overlay.BringToFront();
        }
        else
        {
            if (_overlay is { IsDisposed: false })
            {
                _cfg.OverlayX = _overlay.Location.X;
                _cfg.OverlayY = _overlay.Location.Y;
                _overlay.Hide();
            }
        }

        _cfg.OverlayOn = on;
        _pin.BackColor = on ? Color.FromArgb(200, 60, 60) : SystemColors.Control;
        _pin.ForeColor = on ? Color.White : SystemColors.ControlText;
        Save();
    }

    /// <summary>
    /// Puts the overlay where it was left, unless that is somewhere it cannot
    /// be seen. A window dragged mostly off a screen, or onto a monitor that is
    /// no longer there, is indistinguishable from one that has vanished.
    /// </summary>
    private void PlaceOverlay(bool forceDefault = false)
    {
        if (_overlay is null || _overlay.IsDisposed) return;

        // Snapped, it sits directly above the game's own life numbers. That box
        // is a fixed part of the HUD and already tracked, so it follows the
        // readout it is describing rather than a remembered screen position
        // that is wrong the moment a window moves or a monitor changes.
        // Locked, it goes back exactly where it was locked - not to whichever
        // of the three remembered spots happens to be selected.
        if (!forceDefault && !_cfg.OverlaySnap && _cfg.OverlayLocked
            && _cfg.OverlayX >= 0 && _cfg.OverlayY >= 0
            && OnAScreen(new Point(_cfg.OverlayX, _cfg.OverlayY)))
        {
            _overlay.Location = new Point(_cfg.OverlayX, _cfg.OverlayY);
            return;
        }

        if (!forceDefault && !_cfg.OverlaySnap)
        {
            int slot = Math.Clamp(_cfg.Slot, 0, 2);
            if (_cfg.SlotX.Length == 3 && _cfg.SlotY.Length == 3
                && _cfg.SlotX[slot] >= 0 && _cfg.SlotY[slot] >= 0)
            {
                var at = new Point(_cfg.SlotX[slot], _cfg.SlotY[slot]);
                if (OnAScreen(at)) { _overlay.Location = at; return; }
            }
        }

        if (!forceDefault && _cfg.OverlaySnap && _cfg.Life.TextRegion.IsValid)
        {
            var box = _cfg.Life.TextRegion.ToRect();
            var at = new Point(box.X, box.Y - _overlay.Height - 6);
            if (at.Y < 0) at.Y = box.Bottom + 6;
            _overlay.Location = at;
            return;
        }

        var wanted = new Rectangle(_cfg.OverlayX, _cfg.OverlayY,
                                   _overlay.Width, _overlay.Height);

        bool visible = !forceDefault && _cfg.OverlayX >= 0 && _cfg.OverlayY >= 0
            && Screen.AllScreens.Any(sc =>
            {
                var shown = Rectangle.Intersect(sc.WorkingArea, wanted);
                // Most of it has to be on a screen, not merely a corner.
                return shown.Width >= wanted.Width / 2 && shown.Height >= wanted.Height / 2;
            });

        _overlay.Location = visible
            ? new Point(_cfg.OverlayX, _cfg.OverlayY)
            : new Point(Screen.PrimaryScreen!.WorkingArea.Right - _overlay.Width - 20, 20);

        if (!visible)
        {
            _cfg.OverlayX = _overlay.Location.X;
            _cfg.OverlayY = _overlay.Location.Y;
        }
    }

    /// <summary>Brings the overlay back to a known spot, however it was lost.</summary>
    /// <summary>
    /// The way back from anything done to the readout.
    ///
    /// Click-through is the one setting that can hide its own undo: once the
    /// readout ignores the mouse, its menu cannot be opened again. So this
    /// clears it, along with the lock and the position.
    /// </summary>
    /// <summary>
    /// Puts the readout's own settings onto it.
    ///
    /// Three places did this, each slightly differently, and the one that runs
    /// every time the readout is shown had lost the lock and never applied
    /// click-through at all - so both were switched off again the moment
    /// anything toggled the readout.
    /// </summary>
    /// <summary>
    /// Saves where the readout is now, for wherever the character is now.
    ///
    /// Naming the three Left, Middle and Right made setting them up a puzzle:
    /// you had to work out which name went with which layout, in advance, and
    /// pick the right one before dragging. Nobody should have to. The character
    /// is standing somewhere when you press this, and that is what identifies
    /// the spot - so it goes to whichever of the three already belongs to that
    /// position, or to a spare one if none does.
    /// </summary>
    private void RememberSpot()
    {
        if (_overlay is not { IsDisposed: false }) return;

        if (Native.FindWindowRect(_cfg.WindowMatch) is not { } game)
        {
            Alert("The game is not on screen");
            return;
        }

        // What the screen looks like right now, which is what identifies this
        // layout. Nothing has to be found: the panels are either open or they
        // are not, and these points are where they open.
        var look = ScreenSignature.Sample(game);

        // The spot this layout already owns, if it has one.
        int slot = -1;
        int nearest = int.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            if (_cfg.SlotLook.Length != 3 || _cfg.SlotLook[i].Length == 0) continue;
            int away = ScreenSignature.Distance(_cfg.SlotLook[i], look);
            if (away < nearest) { nearest = away; if (away < 14) slot = i; }
        }

        // Otherwise an empty one, and failing that the one it is least unlike.
        for (int i = 0; i < 3 && slot < 0; i++)
            if (_cfg.SlotX[i] < 0) slot = i;

        if (slot < 0)
            for (int i = 0, best = int.MaxValue; i < 3; i++)
            {
                if (_cfg.SlotLook[i].Length == 0) continue;
                int away = ScreenSignature.Distance(_cfg.SlotLook[i], look);
                if (away < best) { best = away; slot = i; }
            }

        if (slot < 0) slot = Math.Clamp(_cfg.Slot, 0, 2);

        _cfg.Slot = slot;
        _cfg.SlotX[slot] = _overlay.Location.X;
        _cfg.SlotY[slot] = _overlay.Location.Y;
        _cfg.SlotLook[slot] = look;
        _cfg.SlotAuto = true;
        Save();

        _overlay.Slot = slot;

        int set = _cfg.SlotLook.Count(v => v.Length > 0);
        Alert(set >= 3 ? "Saved. All three are set"
                       : $"Saved {set} of 3 - open a panel and save another");

        Log.Write($"overlay: spot {slot + 1} saved at {_overlay.Location}, "
                  + $"{set} of 3 set");
    }

    /// <summary>Says something on the readout, and takes it away again.</summary>
    private void Alert(string what)
    {
        if (_overlay is not { IsDisposed: false }) return;
        _overlay.SetAlert(what);

        var clear = new System.Windows.Forms.Timer { Interval = 5000 };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            clear.Dispose();
            if (_overlay is { IsDisposed: false }) _overlay.SetAlert("");
        };
        clear.Start();
    }

    /// <summary>Whether a remembered spot is still on a screen that exists.</summary>
    private bool OnAScreen(Point at)
    {
        if (_overlay is not { IsDisposed: false }) return false;
        var box = new Rectangle(at, _overlay.Size);

        return Screen.AllScreens.Any(sc =>
        {
            var shown = Rectangle.Intersect(sc.WorkingArea, box);
            return shown.Width >= box.Width / 2 && shown.Height >= box.Height / 2;
        });
    }

    /// <summary>
    /// Picks one of the three by where the character is standing.
    ///
    /// His own floating bar moves when a panel slides him sideways, so it
    /// already says which layout is up without this needing to know anything
    /// about the game's windows. Left third, middle, right third.
    /// </summary>
    private long _lookedAtMs;

    /// <summary>
    /// Moves the readout to whichever saved spot this layout belongs to.
    ///
    /// It compares what the screen looks like now against what it looked like
    /// when each spot was saved. Opening a panel changes those points a great
    /// deal and walking around changes them barely at all, so the comparison is
    /// not a close-run thing.
    /// </summary>
    private void FollowPanels()
    {
        // Locked means nothing moves it - not you by accident, and not this
        // either. A lock that only stopped the mouse left the readout free to
        // be carried off to a spot remembered from some other evening.
        if (!_cfg.SlotAuto || _cfg.OverlaySnap || _cfg.OverlayLocked) return;

        // Three or four times a second. This is a screen capture, and the
        // panels are not opened faster than that.
        long now = Environment.TickCount64;
        if (now - _lookedAtMs < 300) return;
        _lookedAtMs = now;

        if (Native.FindWindowRect(_cfg.WindowMatch) is not { } game) return;

        var look = ScreenSignature.Sample(game);
        int wanted = -1, best = int.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            if (_cfg.SlotLook.Length != 3 || _cfg.SlotLook[i].Length == 0) continue;
            if (_cfg.SlotX[i] < 0) continue;

            int away = ScreenSignature.Distance(_cfg.SlotLook[i], look);
            if (away < best) { best = away; wanted = i; }
        }

        if (wanted < 0 || wanted == _cfg.Slot) return;

        _cfg.Slot = wanted;
        if (_overlay is { IsDisposed: false }) _overlay.Slot = wanted;
        Save();
        PlaceOverlay();
    }

    private void ApplyOverlayOptions()
    {
        if (_overlay is not { IsDisposed: false }) return;

        // Anchored to something in the game counts as locked, and so does
        // asking for it.
        _overlay.Locked = _cfg.OverlaySnap || _cfg.OverlayLocked;
        _overlay.ClickThrough = _cfg.OverlayClickThrough;
        _overlay.Slot = Math.Clamp(_cfg.Slot, 0, 2);
        _overlay.SlotAuto = _cfg.SlotAuto;
        _overlay.ShowMana = _cfg.OverlayShowMana;
    }

    private void ResetOverlay()
    {
        // Click-through goes, because a readout you cannot click is one you
        // cannot get back into. The lock stays: resetting is "bring it where I
        // can see it", and it is one stray right-click on the P away - which is
        // no reason to throw away a setting somebody chose on purpose.
        Log.Write("overlay: reset - brought back into view, lock kept");
        _cfg.OverlayClickThrough = false;
        _cfg.FollowOffsetX = int.MinValue;
        _cfg.FollowOffsetY = int.MinValue;

        ToggleOverlay(true);
        if (_overlay is { IsDisposed: false })
        {
            ApplyOverlayOptions();
        }

        PlaceOverlay(forceDefault: true);
        _overlay?.BringToFront();
        Save();
    }

    private void OnSampled(GlobeReading life, GlobeReading mana, GlobeReading shield,
                           bool focused)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_overlay is { IsDisposed: false })
                {
                    // Nothing true to say while a shop or the passive tree
                    // covers the HUD, and nothing can fire either - so getting
                    // out of the way says something rather than hides it.
                    // Asked of the engine rather than read off a note.
                    //
                    // The note it used to look for is only written when nothing
                    // can read the pool at all, so as soon as memory locked on
                    // - reading happily through a shop or the passive tree -
                    // the note stopped appearing and the overlay stopped
                    // hiding. The tickbox has meant nothing since.
                    bool blind = !_engine.HudVisible;
                    bool wanted = !_cfg.OverlayAutoHide || (!blind && focused);

                    // Following the bar means sharing its fate: when the game
                    // stops drawing it, there is nothing to sit under.

                    if (_cfg.OverlayOn && wanted != _overlay.Visible)
                    {
                        if (wanted) _overlay.Show();
                        else
                        {
                            _overlay.Hide();
                            // Which of the two reasons actually did it - "auto
                            // hide" covers both a menu over the HUD and the
                            // game losing focus, and only the first of those is
                            // in the checkbox's own tooltip. Logged so the next
                            // "why didn't it hide" has an answer instead of a
                            // guess.
                            string why = !focused ? "the game is not the focused window"
                                       : "the HUD is covered";
                            Log.Write($"overlay: hiding - {why}");
                        }
                    }

                    _engine.OverlayBounds = _overlay.Visible
                        ? Rectangle.Inflate(_overlay.Bounds, 8, 8)
                        : Rectangle.Empty;

                    if (_overlay.Visible)
                    {
                        _overlay.LifeWatched = _cfg.Life.Enabled;
                        _overlay.ManaWatched = _cfg.Mana.Enabled;
                        _overlay.SetKeys(_cfg.Life.Enabled ? _cfg.Life.Key.ToUpperInvariant() : "",
                                         _cfg.Mana.Enabled ? _cfg.Mana.Key.ToUpperInvariant() : "");
                        _overlay.Show(life, mana, _cfg.Life.Threshold, _cfg.Mana.Threshold);
                        _overlay.SetFightCount(_engine.FiresThisFightFor("Life"),
                                               _engine.FiresThisFightFor("Mana"),
                                               _engine.InCombat);
                        if (_cfg.OverlaySnap) PlaceOverlay();
                        else FollowPanels();
                    }
                }

                _life.Update(life);
                _mana.Update(mana);

                _life.UpdateShield(shield);

                // Memory sits idle until both maxima are known, and the panel
                // that is missing one is the place to say so.
                bool waiting = _cfg.UseMemory && !_engine.MemoryFound;
                _life.NeedMaxForMemory(waiting);
                _mana.NeedMaxForMemory(waiting);
                _focus.Text = focused
                    ? "game window focused — firing allowed"
                    : "not firing: focused window does not match";
                _focus.ForeColor = focused
                    ? Color.FromArgb(0, 120, 0)
                    : SystemColors.GrayText;
                string covering = CoveredGlobes();
                _live.Text = covering.Length > 0
                    ? $"This window is covering the {covering} globe - move it, or the "
                      + "capture reads QytOCR instead of the globe."
                    : $"focused window: \"{_engine.ForegroundTitle}\"   "
                      + $"polls/sec: {_engine.ActualHz}   "
                      + $"work per poll: {_engine.LastPollMs:0.0} ms   "
                      + $"memory: {_engine.MemoryStatus}";
                _live.ForeColor = covering.Length > 0
                    ? Color.FromArgb(190, 60, 0)
                    : SystemColors.GrayText;
            });
        }
        catch (ObjectDisposedException) { /* closing */ }
    }

    private void RefreshArmUi()
    {
        bool on = _engine.Armed;

        // The per-globe line describes the last press, or the last time it
        // would have pressed. Arming or disarming makes either one stale.
        _life.ClearStatus();
        _mana.ClearStatus();
        if (_overlay is { IsDisposed: false }) _overlay.SetArmed(on);
        // The one bold thing in the window, and the only one that shouts. Armed
        // is filled and unmistakable from the corner of an eye; disarmed is an
        // outline, quiet but not hidden. Everything else stays out of its way.
        _arm.Text = on ? "Armed" : "Disarmed";
        _arm.BackColor = on ? Theme.Armed : Theme.Card;
        _arm.ForeColor = on ? Color.FromArgb(255, 238, 230) : Theme.Dim;
        _arm.FlatAppearance.BorderSize = on ? 0 : 2;
        _arm.FlatAppearance.BorderColor = Theme.Line;
        _arm.FlatAppearance.MouseOverBackColor =
            on ? ControlPaint.Light(Theme.Armed, 0.15f) : Theme.Raised;

        var watching = new List<string>();
        if (_cfg.Life.Enabled) watching.Add($"Life <{_cfg.Life.Threshold:P0} → {_cfg.Life.Key.ToUpperInvariant()}");
        if (_cfg.Mana.Enabled) watching.Add($"Mana <{_cfg.Mana.Threshold:P0} → {_cfg.Mana.Key.ToUpperInvariant()}");
        _status.Text = watching.Count == 0
            ? "Nothing is being watched."
            : string.Join("      ", watching);

        _tray.Text = on ? "QytOCR – armed" : "QytOCR – disarmed";
    }

    // ---- tray ------------------------------------------------------------

    private void SetupTray()
    {
        _tray.Icon = SystemIcons.Shield;
        _tray.Visible = true;
        _tray.Text = "QytOCR";
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Click += (_, e) =>
        {
            // One click too. Hunting for the second click of a double is not a
            // thing anybody should have to do to get a window back.
            if (e is MouseEventArgs { Button: MouseButtons.Left }) RestoreFromTray();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Arm / disarm", null, (_, _) => _engine.Toggle());
        menu.Items.Add("Bring overlay back", null, (_, _) => ResetOverlay());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            // Chosen deliberately from the tray, so it is not asked about again.
            _reallyQuitting = true;
            _tray.Visible = false;
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Brings the window back, properly.
    ///
    /// Show and Activate ask politely, and Windows refuses a background process
    /// the foreground - it flashes the taskbar instead, which behind a
    /// fullscreen game is nothing at all. Taking it is the only thing that
    /// actually puts the window in front of somebody.
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        Native.ForceForeground(Handle);
    }

    // ---- hotkey ----------------------------------------------------------

    private void RegisterArmHotkey()
    {
        if (_hotkeyRegistered)
        {
            Native.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
        // F1..F12 are VK 0x70..0x7B.
        if (!int.TryParse(_cfg.ArmHotkey.TrimStart('F', 'f'), out int n) || n is < 1 or > 12)
            n = 8;
        uint vk = (uint)(0x6F + n);
        _hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, 0, vk);
        if (!_hotkeyRegistered)
            Log.Write($"could not register {_cfg.ArmHotkey} — another app owns it");

        // Ctrl+/ for setup, because the moment you need it is the moment you
        // are in the game and cannot reach the button - and alt-tabbing to
        // press it is itself a reason the numbers cannot be read.
        if (!_setupHotkeyRegistered)
        {
            _setupHotkeyRegistered =
                Native.RegisterHotKey(Handle, SetupHotkeyId, MOD_CONTROL, VK_OEM_2);
            if (!_setupHotkeyRegistered)
                Log.Write("could not register Ctrl+/ for setup - another app owns it");
        }
    }

    /// <summary>
    /// The scene behind the panels.
    ///
    /// Painted here rather than as a control, because every card sits on top at
    /// full opacity and only the margins show - which is exactly as much flair
    /// as something read mid-fight should carry.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Backdrop.Draw(e.Graphics, ClientRectangle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ExcludeFromCapture(Handle, _cfg.HideFromCapture);
        RegisterArmHotkey();
    }

    protected override void WndProc(ref Message m)
    {
        // A second copy of P02 being started, asking this one to show itself.
        if (m.Msg == Native.WM_QYTOCR_SHOW)
        {
            Log.Write($"another launch asked for the window - bringing it to the front "
                      + $"(visible={Visible}, state={WindowState}, handle valid={IsHandleCreated})");
            BeginInvoke(RestoreFromTray);
        }

        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            _engine.Toggle();
        else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == SetupHotkeyId)
        {
            Log.Write("setup: asked for with Ctrl+/");
            BeginInvoke(FixSetup);
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Says which settings this update changed, and why.
    ///
    /// Changing settings someone chose deliberately is only acceptable if they
    /// are told. It runs from OnShown rather than the constructor: there is no
    /// window handle to show a dialog over until the form is up.
    /// </summary>
    private void ReportRepairs()
    {
        if (_cfg.Repairs.Count == 0) return;
        Told(NoticeBoard.Level.Info, "Some settings were changed by this update",
             string.Join("  ", _cfg.Repairs));
        _cfg.Repairs.Clear();
    }

    /// <summary>
    /// Sets the numbers up on its own the first time, if they are not set and
    /// the game is there to look at. It is one button, but it is also the one
    /// step everything else depends on, and leaving it to be discovered means
    /// running on the globe pixels - which cannot tell life from shield and
    /// read a poisoned globe as empty.
    /// </summary>
    private void FirstRunSetup()
    {
        if (!_engine.TextAvailable) return;
        if (_cfg.Life.TextRegion.IsValid || _cfg.Mana.TextRegion.IsValid) return;
        if (Native.FindWindowRect(_cfg.WindowMatch) is null) return;

        Log.Write("first run: no numbers set and the game is up - finding them");
        string result = _engine.FindAllNumbers();
        Save();
        _life.RefreshFromConfig();
        _mana.RefreshFromConfig();

        Told(NoticeBoard.Level.Info, "The numbers were not set up, so they were found "
             + "for you", result);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ReportRepairs();
        FirstRunSetup();

        // Said once, on the way in, and only when something is actually broken.
        // Every session that went wrong went wrong from the first minute, and
        // nobody was told until they asked.
        if (!_cfg.Life.TextRegion.IsValid && !_cfg.Mana.TextRegion.IsValid
            && _cfg.Life.KnownMax <= 0)
        {
            using var how = new HowToForm();
            how.ShowDialog(this);
        }

        // A fault has to still be a fault before anyone is interrupted.
        //
        // This used to be one look, eight seconds after the window opened, and
        // it was wrong nearly every time: eight seconds in, the game is often
        // still on its login screen, no life number is drawn anywhere, and the
        // check duly announced that nothing was coming back and setup should be
        // run again - on an install whose setup was saved, correct, and about
        // to start working perfectly a few seconds later.
        //
        // So it watches instead of glancing. Only a fault that survives a full
        // minute of looking is real enough to stop someone with a box, and it
        // is said once.
        if (_cfg.WarnAtLaunch)
        {
            int looks = 0;
            var settle = new System.Windows.Forms.Timer { Interval = 6000 };
            settle.Tick += (_, _) =>
            {
                if (IsDisposed) { settle.Stop(); settle.Dispose(); return; }

                var wrong = SelfCheck.Run(_cfg, _engine, Version);
                if (!wrong.Any(f => f.Stops))
                {
                    // It sorted itself out, which is the usual ending.
                    settle.Stop();
                    settle.Dispose();
                    return;
                }

                if (++looks < 10) return;

                settle.Stop();
                settle.Dispose();

                Told(NoticeBoard.Level.Alert, "This has been wrong for a minute",
                     SelfCheck.Describe(wrong));
            };
            settle.Start();
        }
        // With unattended installs on, the launch check has nothing to ask
        // about: the countdown below handles it a few seconds later, in one
        // place, and having waited for a fight to end.
        if (_cfg.CheckUpdatesOnStart)
            _ = Updater.CheckAsync(this, silent: true, beforeExit: _cfg.SaveNow,
                                   ui: !_cfg.AutoInstall);
        if (_cfg.ResumeArmed)
        {
            _cfg.ResumeArmed = false;
            _cfg.SaveNow();
            Log.Write("armed again after an update restart");
            _engine.Toggle();
        }

        _critical.Start();
        _watch.Start();
        if (_cfg.StartMinimised) Hide();
    }

    /// <summary>
    /// Counts a critical update down in the overlay and then stops the session.
    ///
    /// A message box behind a fullscreen game is not a warning, it is a thing
    /// discovered afterwards - and the releases this is for are the ones that
    /// were letting somebody die while the app watched. Thirty seconds of
    /// notice on the overlay, then Escape into the game's own menu so play
    /// actually stops, and this window brought forward with the update ready.
    ///
    /// Never mid-fight. Pausing someone who is being hit is its own way of
    /// getting them killed, so the countdown holds at zero until they are out
    /// of combat.
    /// </summary>
    private void OnCriticalTick()
    {
        if (_criticalDone) return;

        // Two kinds of interruption, the same countdown. A critical release
        // stops the game and says why, because staying behind on it is how
        // somebody dies. An ordinary one, only a release or three ahead, is
        // small enough to simply take: notice on the overlay, then it swaps
        // itself and comes back. Both wait for the fight to end first.
        bool critical = Updater.Critical is not null;
        bool ordinary = !critical && _cfg.AutoInstall && Updater.Pending is not null
                        && Updater.Behind <= _cfg.AutoInstallMaxBehind;

        if (!critical && !ordinary)
        {
            _criticalLeft = 0;
            return;
        }

        // A fresh countdown from whichever kind was found. Thirty seconds to
        // stop and read something that can kill you; ten to notice a restart
        // that takes a couple of seconds and puts everything back.
        if (_criticalLeft <= 0) _criticalLeft = critical ? 31 : 11;

        _criticalLeft--;

        string what = Updater.Critical ?? Updater.Pending ?? "";

        if (_criticalLeft > 0)
        {
            _overlay?.SetAlert(critical
                ? $"UPDATE {what} - pausing in {_criticalLeft}s"
                : $"UPDATE {what} - restarting in {_criticalLeft}s");
            return;
        }

        if (_engine.InCombat)
        {
            _overlay?.SetAlert($"UPDATE {what} - waiting until you are safe");
            return;
        }

        _criticalDone = true;
        _critical.Stop();
        _overlay?.SetAlert("");

        // Come back the way it was left. Only here - every ordinary launch
        // starts disarmed on purpose.
        _cfg.ResumeArmed = _engine.Armed;
        _cfg.SaveNow();

        // Every update pauses the game, not only the critical ones.
        //
        // An unattended install restarts the app, and a restart takes the
        // readout, the memory lock and the armed state with it for a few
        // seconds. Doing that silently behind a live fight is the same hazard
        // as doing it behind a critical one - it is only the reason for
        // updating that differs, not what the update does to you mid-pack.
        PauseGame();

        if (!critical)
        {
            Log.Write($"update: taking {what} unattended, armed={_cfg.ResumeArmed}");
            _ = Updater.CheckAsync(this, silent: true, beforeExit: _cfg.SaveNow,
                                   ui: false, install: true);
            return;
        }

        Log.Write($"update: pausing the session for critical release {Updater.Critical}");

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();

        // Activate() asks politely, and Windows refuses a background process
        // the foreground - it flashes the taskbar instead, which behind a
        // fullscreen game is nothing at all. Take it properly, and sit above
        // the game until the warning has been read.
        Native.ForceForeground(Handle);
        bool wasTop = TopMost;
        TopMost = true;

        // Told, not asked. A dialog with an OK button is a prompt however it is
        // worded, and a prompt behind a paused game waits for somebody to come
        // back to the keyboard - which is exactly the state this was supposed to
        // stop. It says what it is doing on the way past instead.
        _tray.BalloonTipTitle = $"Updating to {Updater.Critical}";
        _tray.BalloonTipText = Updater.CriticalWhy + Environment.NewLine
                               + "Your game is paused; QytOCR will be back in a moment.";
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        try { _tray.ShowBalloonTip(6000); } catch { /* notifications may be off */ }

        Log.Write($"update: {Updater.Critical} - {Updater.CriticalWhy}");

        // It installs itself from here. A release that fixes a way to die
        // quietly is the one that most needs installing, not the one that most
        // needs a second dialog - and the previous one was leaving people
        // paused, warned, and still on the broken version.
        _ = Updater.CheckAsync(this, silent: true, beforeExit: _cfg.SaveNow,
                               ui: false, install: true);
    }

    /// <summary>
    /// Pauses the game and checks that it actually paused.
    ///
    /// Sending Escape and hoping was never verification. The key can land in
    /// the wrong window, arrive while a panel is open and close that instead,
    /// or be swallowed entirely - and the update then went ahead on a character
    /// standing in a pack, which is the one thing the pause exists to prevent.
    ///
    /// The game says so itself, in large letters across the middle of the
    /// screen, so that is what gets read. And it is read BEFORE anything is
    /// sent as well as after: pressing Escape at an already-paused game
    /// un-pauses it, which would be a perfect way to cause the very thing being
    /// guarded against.
    /// </summary>
    private bool PauseGame()
    {
        if (_engine.GameWindow == 0) return false;
        if (Native.FindWindowRect(_cfg.WindowMatch) is not { } game) return false;

        if (LooksPaused(game))
        {
            Log.Write("update: the game is already paused");
            return true;
        }

        for (int go = 1; go <= 3; go++)
        {
            KeySender.PostTo(_engine.GameWindow, "Escape", 70);
            KeySender.Tap("Escape", 70);

            // Long enough for the menu to draw. The check is cheap and the
            // alternative is updating underneath a live character.
            for (int waited = 0; waited < 12; waited++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
                if (!LooksPaused(game)) continue;

                Log.Write($"update: the game is paused - confirmed on screen"
                          + (go > 1 ? $" after {go} tries" : ""));
                return true;
            }

            Log.Write($"update: sent Escape {go} time(s) and the game does not say it is "
                      + "paused");
        }

        // Said out loud rather than assumed either way. Whoever reads the log
        // afterwards deserves to know the update went in live.
        Log.Write("update: could not confirm the game paused - going ahead anyway, "
                  + "because staying on a version that needs replacing is its own risk");
        Told(NoticeBoard.Level.Warn, "Could not confirm the game paused before updating",
             "Escape was sent three times and the words GAME PAUSED never appeared. "
             + "The update went ahead.");
        return false;
    }

    /// <summary>Whether the game is showing its pause screen right now.</summary>
    private bool LooksPaused(Rectangle game)
    {
        // The banner sits across the middle, about a fifth of the way down.
        var band = new Rectangle(
            game.Left + game.Width * 3 / 10,
            game.Top + game.Height / 12,
            game.Width * 4 / 10,
            game.Height / 5);

        string seen;
        try { seen = _engine.ProbeText(band, out var shot); shot?.Dispose(); }
        catch { return false; }

        // Loosely, because it is large text over whatever the screen was
        // showing and a letter or two will be wrong.
        // The whole blob first - large letters often run together - then word
        // by word, because a banner read as "GAME PAUS ED" is still a banner.
        if (TextOcr.HasLabel(seen, "PAUSED")) return true;

        foreach (string word in seen.Split([' ', (char)10, (char)13, (char)9],
                                           StringSplitOptions.RemoveEmptyEntries))
            if (word.Length >= 5 && TextOcr.HasLabel(word, "PAUSED")) return true;

        return false;
    }

    /// <summary>
    /// Sends each switched-on globe's key once, ignoring arm state and the
    /// window match, so a keybind that never reaches the game shows up as a
    /// setup problem rather than a detection one.
    /// </summary>
    private void TestKeys()
    {
        // Whatever it would actually press. Testing a keyboard key on a setup
        // that presses a controller proves nothing, and proving nothing is
        // worse than not testing at all - it is the button people press to
        // find out whether this reaches the game.
        string What(WatcherConfig c) => _cfg.UseController ? c.PadButton : c.Key;

        var keys = new List<string>();
        if (_cfg.Life.Enabled && What(_cfg.Life).Length > 0) keys.Add(What(_cfg.Life));
        if (_cfg.Mana.Enabled && What(_cfg.Mana).Length > 0) keys.Add(What(_cfg.Mana));
        if (keys.Count == 0)
        {
            Told(NoticeBoard.Level.Warn, _cfg.UseController
                     ? "Pick a controller button first."
                     : "Switch on a globe first.");
            return;
        }

        _status.Text = $"clicking into the game… sending {string.Join(" and ", keys)} in 3s";
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (_cfg.UseController)
            {
                foreach (var c in new[] { _cfg.Life, _cfg.Mana })
                    if (c.Enabled && c.PadButton.Length > 0)
                        Gamepad.Press(c.PadButton, c.HoldMs);
            }
            else
            {
                if (_cfg.Life.Enabled) _engine.TestKey(_cfg.Life.Key, _cfg.Life.HoldMs);
                if (_cfg.Mana.Enabled) _engine.TestKey(_cfg.Mana.Key, _cfg.Mana.HoldMs);
            }
            Log.Write($"test keys sent: {string.Join(", ", keys)}");
            BeginInvoke(RefreshArmUi);
        };
        t.Start();
    }

    /// <summary>
    /// Writes a shareable bundle: a readable report, the recent log, the
    /// config, and what the detector currently sees in each globe.
    /// </summary>
    /// <summary>
    /// One button for the whole setup: hide, look at the game, find every stat
    /// line by its label, and point the watchers at them.
    /// </summary>
    /// <summary>
    /// Does the whole setup, in order, and says in one paragraph what happened.
    ///
    /// There were four buttons and an order to press them in, and getting it
    /// wrong left the app reading the globe pixels and saying so in language
    /// only its author understood. Nobody should have to know that the numbers
    /// must be found before the maximum can fill in before the memory search
    /// has anything to look for. One button does it in the right order.
    /// </summary>
    private void FixSetup()
    {
        if (Native.FindWindowRect(_cfg.WindowMatch) is null)
        {
            Told(NoticeBoard.Level.Warn, "The game does not seem to be on screen",
                "Start Path of Exile 2, stand somewhere safe with your life and mana "
                + "numbers showing, then press this again.");
            return;
        }

        // The numbers first: everything else is built on them.
        _cfg.Life.UseText = true;
        _cfg.Mana.UseText = true;

        Hide();
        Thread.Sleep(350);
        string found;

        try { found = _engine.FindAllNumbers(); }
        catch { Show(); throw; }

        // Then the globes, where one is missing. Same code the automatic
        // repair runs, so the button and the repair cannot drift apart.
        _engine.FindGlobes();

        Show();

        Save();
        _life.RefreshFromConfig();
        _mana.RefreshFromConfig();

        // Then memory, which needs both maxima to have anything to search for.
        // Give it a moment before the verdict, or it is always reported as
        // still looking - it is asked to search several gigabytes.
        if (_cfg.UseMemory)
        {
            _engine.RescanMemory();
            for (int waited = 0; waited < 60 && !_engine.MemoryLocked; waited++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
        }

        // Whether life is set up is decided by reading it, not by a region
        // being non-empty. It told him it was reading his life from the numbers
        // while it had found only mana, because a region left over from before
        // counted as success.
        string lifeSays = "";
        bool lifeOk = false;
        if (_cfg.Life.TextRegion.IsValid)
        {
            lifeSays = _engine.ProbeText(_cfg.Life.TextRegion.ToRect(), out var pic);
            pic?.Dispose();
            lifeOk = TextOcr.TryParse(lifeSays, out _, out int lifeMax) && lifeMax > 0;

            // A maximum typed on some earlier evening is the single most
            // common thing left wrong, and it is wrong the moment you level.
            if (lifeOk && _cfg.Life.KnownMax != lifeMax)
            {
                _cfg.Life.KnownMax = lifeMax;
                _life.RefreshFromConfig();
                Log.Write($"setup: maximum life set to {lifeMax} from the numbers");
            }
        }

        var said = new System.Text.StringBuilder();

        // Memory counts. It is the better of the two sources, and reporting
        // "not protecting you" while it reads your life exactly is the app
        // calling itself broken in front of its own working readout.
        bool memoryOk = _cfg.UseMemory && _engine.MemoryLocked;

        said.AppendLine(memoryOk
            ? "Set up. It is reading your life straight from the game, which is exact "
              + "and immediate."
            : lifeOk
                ? $"Set up. It is reading your life as {lifeSays.Trim()} from the numbers "
                  + "beside the globe, which is exact."
                : "It could NOT read your life, so it is not protecting you yet.");
        said.AppendLine();
        said.AppendLine(found);
        said.AppendLine();

        if (lifeOk || memoryOk)
        {
            said.AppendLine($"It will press \"{_cfg.Life.Key}\" when life falls below "
                            + $"{_cfg.Life.Threshold:P0}. Nothing is sent until you arm it.");
            said.AppendLine();
            said.AppendLine("Your maximum fills itself in and keeps up as you level, so "
                            + "there is nothing to type. If it ever stops reading, this "
                            + "button is the whole of the fix - and it also runs itself "
                            + "after twenty seconds of not reading anything.");
        }
        else
        {
            said.AppendLine("The one thing that usually causes this: the numbers are only "
                            + "drawn while you are actually playing. Not on the death "
                            + "screen, not in a menu, and not while the game is paused. "
                            + "Stand in a town with them visible and press it again.");
        }

        // The same check the button beside it runs, so setup finishes by saying
        // whether it worked rather than what it did.
        var left = SelfCheck.Run(_cfg, _engine, Version);
        if (left.Count > 0)
        {
            said.AppendLine();
            said.AppendLine(SelfCheck.Describe(left));
        }

        Told((lifeOk || memoryOk) && !left.Any(f => f.Stops)
                 ? NoticeBoard.Level.Info : NoticeBoard.Level.Alert,
             "Setup ran", said.ToString().TrimEnd());
    }

    private void FindAllNumbers()
    {
        Hide();
        Thread.Sleep(350);
        string result;
        try { result = _engine.FindAllNumbers(); }
        finally { Show(); }

        Save();
        _life.RefreshFromConfig();
        _mana.RefreshFromConfig();

        bool trouble = result.Contains("WARNING") || result.Contains("could not")
                       || result.Contains("Could not");
        Told(NoticeBoard.Level.Info, "Looked for the numbers", result);
    }

    private void ExportDiagnostics()
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            string zip = Diagnostics.Export(_cfg, _engine, this);
            Cursor = Cursors.Default;

            string msg = "Diagnostics written to:" + Environment.NewLine + zip
                + Environment.NewLine + Environment.NewLine
                + "The .txt beside it is the same report in plain text, ready to paste. "
                + "Your update token is not included."
                + Environment.NewLine + Environment.NewLine
                + "Open the folder now?";
            if (MessageBox.Show(this, msg, "Export diagnostics",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            Log.Write($"diagnostics export failed: {ex}");
            MessageBox.Show(this, ex.Message, "Export diagnostics",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Capture reads the screen, so a QytOCR window sitting over a globe is read
    /// as the globe. Saying so beats the old fix of hiding the window from
    /// every capture on the system.
    /// </summary>
    private string CoveredGlobes()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || _cfg.HideFromCapture)
            return string.Empty;

        var names = new List<string>();
        if (_cfg.Life.Enabled && _cfg.Life.Region.IsValid
            && Bounds.IntersectsWith(_cfg.Life.Region.ToRect())) names.Add("Life");
        if (_cfg.Mana.Enabled && _cfg.Mana.Region.IsValid
            && Bounds.IntersectsWith(_cfg.Mana.Region.ToRect())) names.Add("Mana");
        return string.Join(" and ", names);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button used to hide to the tray, always, without a word - which
        // is indistinguishable from a program that ignored you, and sent people
        // hunting the tray for something they thought they had shut. It asks
        // now, and remembers the answer if you say so.
        if (e.CloseReason == CloseReason.UserClosing && !_reallyQuitting)
        {
            string want = _cfg.OnClose;

            if (want == "ask")
            {
                var box = new TaskDialogPage
                {
                    Caption = "QytOCR",
                    Heading = "Close QytOCR, or leave it running?",
                    Text = "Left running it keeps watching and can still fire. Closed, it "
                           + "does nothing at all until you start it again.",
                    Icon = TaskDialogIcon.Information,
                    AllowCancel = true,
                    Verification = new TaskDialogVerificationCheckBox("Always do this"),
                    Buttons =
                    {
                        new TaskDialogButton("Leave it running") { Tag = "hide" },
                        new TaskDialogButton("Close it") { Tag = "close" },
                        TaskDialogButton.Cancel,
                    },
                };

                var chose = TaskDialog.ShowDialog(this, box);
                string? tag = chose.Tag as string;

                if (tag is null) { e.Cancel = true; return; }
                if (box.Verification.Checked)
                {
                    _cfg.OnClose = tag;
                    _cfg.SaveNow();
                    Log.Write($"close: the X button will {tag} from now on");
                }

                want = tag;
            }

            if (want != "close")
            {
                e.Cancel = true;
                Hide();
                return;
            }
        }
        _engine.Dispose();
        if (WindowState == FormWindowState.Normal)
        {
            _cfg.WindowX = Location.X;
            _cfg.WindowY = Location.Y;
        }
        if (_overlay is { IsDisposed: false })
        {
            _cfg.OverlayX = _overlay.Location.X;
            _cfg.OverlayY = _overlay.Location.Y;
            _overlay.Dispose();
        }
        _cfg.SaveNow();
        if (_hotkeyRegistered) Native.UnregisterHotKey(Handle, HotkeyId);
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}
