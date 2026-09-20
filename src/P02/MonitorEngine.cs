using System.Diagnostics;

namespace P02;

/// <summary>One globe's latest sample. <paramref name="Ok"/> false means there
/// is no reading, and Note says why.</summary>
public sealed record GlobeReading(string Name, double Fraction, bool Ok,
                                  string Note = "", bool FromText = false,
                                  string TextRaw = "");

public sealed class MonitorEngine : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly KeyPresser _keys = new();
    private readonly TextOcr _ocr = new();
    private readonly GameMemory _mem = new();
    private readonly Chime _chime;
    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>Master switch. Nothing is ever sent while this is false.</summary>
    public bool Armed { get; private set; }

    /// <summary>Polls actually completed in the last second.</summary>
    public int ActualHz { get; private set; }

    /// <summary>Milliseconds of work in the last poll, excluding the sleep.</summary>
    public double LastPollMs { get; private set; }

    /// <summary>Keys sent since this fight started.</summary>
    public int FiresThisFight { get; private set; }

    /// <summary>
    /// Presses this fight, per pool.
    ///
    /// One combined number could not answer the question anyone actually has
    /// mid-fight, which is not "how many flasks" but "how many of MY life
    /// flasks". Life and mana empty at different rates and for different
    /// reasons, and a single tally hid a mana flask firing three times a second
    /// behind a total that looked like a busy fight.
    /// </summary>
    private readonly Dictionary<string, int> _firesByPool = new()
    {
        ["Life"] = 0, ["Mana"] = 0, ["Shield"] = 0,
    };

    public int FiresThisFightFor(string pool)
    {
        lock (_firesByPool) return _firesByPool.TryGetValue(pool, out int n) ? n : 0;
    }

    /// <summary>Life has dropped recently enough to still count as fighting.</summary>
    public bool InCombat { get; private set; }

    /// <summary>How old the life reading being acted on is, in milliseconds.</summary>
    public long ReadingAgeMs { get; private set; }

    /// <summary>Title of whatever window currently has focus, for the UI.</summary>
    public string ForegroundTitle { get; private set; } = "";

    public event Action<GlobeReading, GlobeReading, GlobeReading, bool>? Sampled;
    public event Action<string, double>? Fired;

    /// <summary>Globe name, whether the last press moved the globe, and how
    /// many in a row have not.</summary>
    public event Action<string, bool, int>? EffectChecked;

    /// <summary>The trigger was crossed while disarmed - what would have happened.</summary>
    public event Action<string, double>? WouldFire;

    /// <summary>A pool's maximum changed and has been adopted: name, old, new.</summary>
    public event Action<string, int, int>? MaxAdopted;

    /// <summary>A watched globe has been reading nothing for a while: the box
    /// is not on the globe. Bool says whether it is currently blind.</summary>
    public event Action<string, bool>? Blind;
    public event Action<bool>? ArmedChanged;

    public MonitorEngine(AppConfig cfg)
    {
        _cfg = cfg;
        _chime = new Chime(cfg.SoundGainDb);
        SyncTextRegions();
        _mem.ProcessName = cfg.GameProcess;
        if (cfg.UseMemory) _mem.Start();
        _ocr.EngineChoice = cfg.OcrEngine;
    }

    /// <summary>Switches which engine reads the live numbers, at once - no
    /// restart, since the next reading just asks the new one instead.</summary>
    public void SetOcrEngine(string choice) => _ocr.EngineChoice = choice;

    /// <summary>Why the chosen OCR engine fell back to Windows, if it did.</summary>
    public string OcrEngineWhy => _ocr.EngineWhy;

    /// <summary>What the memory reader is doing, for the UI.</summary>
    public string MemoryStatus =>
        !_cfg.UseMemory ? "off" : _mem.Status;

    public bool MemoryFound => _cfg.UseMemory && _mem.Found;

    public bool MemoryEnabled => _cfg.UseMemory;

    /// <summary>Turns memory reading on or off at runtime.</summary>
    public void SetMemory(bool on)
    {
        _cfg.UseMemory = on;
        _mem.ProcessName = _cfg.GameProcess;
        if (on) _mem.Start(); else _mem.Rescan();
    }

    /// <summary>Forces a fresh search.</summary>
    public void RescanMemory() => _mem.Rescan();

    /// <summary>
    /// Finds a globe for any pool that has lost one.
    ///
    /// Lifted out of the setup button so the automatic repair can do it too.
    /// A region is only replaced when there is nothing usable there, because a
    /// globe somebody has positioned by hand is better than one found by
    /// guessing at the corner it usually lives in.
    /// </summary>
    public void FindGlobes()
    {
        if (Native.FindWindowRect(_cfg.WindowMatch) is not { } area) return;

        foreach (var (cfg, blue, what) in new[]
                 { (_cfg.Life, false, "life"), (_cfg.Mana, true, "mana") })
        {
            if (cfg.Region.IsValid) continue;

            int gw = (int)(area.Width * 0.25);
            int gh = (int)(area.Height * 0.36);
            var look = blue
                ? new Rectangle(area.Right - gw, area.Bottom - gh, gw, gh)
                : new Rectangle(area.Left, area.Bottom - gh, gw, gh);

            var globe = OrbDetector.AutoLocate(look, blue, margin: 18, minV: 45);
            if (globe is null) continue;

            cfg.Region = Box.From(globe.Value);
            cfg.FullRow = 0;
            cfg.EmptyRow = 0;
            Log.Write($"setup: {what} globe found at {globe.Value}");
        }
    }


    /// <summary>True when Windows can do OCR at all.</summary>
    public bool TextAvailable => _ocr.Available;

    public string TextUnavailable => _ocr.Unavailable;

    /// <summary>Pushes the configured text regions into the reader.</summary>
    public void SyncTextRegions()
    {
        _ocr.Configure("Life", _cfg.Life.UseText && _cfg.Life.Enabled
                               && _cfg.Life.TextRegion.IsValid
            ? _cfg.Life.TextRegion.ToRect() : null,
            _cfg.Life.TextLabel, _cfg.Life.KnownMax);
        _ocr.Configure("Shield", _cfg.Shield.UseText && _cfg.Shield.Enabled
                                 && _cfg.Shield.TextRegion.IsValid
            ? _cfg.Shield.TextRegion.ToRect() : null,
            _cfg.Shield.TextLabel, _cfg.Shield.KnownMax);
        _ocr.Configure("Mana", _cfg.Mana.UseText && _cfg.Mana.Enabled
                               && _cfg.Mana.TextRegion.IsValid
            ? _cfg.Mana.TextRegion.ToRect() : null,
            _cfg.Mana.TextLabel, _cfg.Mana.KnownMax);
    }

    /// <summary>
    /// Levelling and gear move a pool's maximum, and a stated one that has gone
    /// stale refuses every reading in silence. The numbers on screen already
    /// carry the true maximum, so when they have insisted on a different one
    /// for long enough to rule out a misread, take it: update the setting,
    /// point the search at the new value and say so.
    /// </summary>
    /// <summary>
    /// Sweeps a corner in overlapping bands, stopping when it has what it came
    /// for. Bottom first, because that is where the stat block sits and the
    /// chat is above it.
    /// </summary>
    private Dictionary<string, Rectangle> Bands(Rectangle window, Rectangle corner,
                                                string[] labels)
    {
        var found = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);

        int band = Math.Max(110, (int)(window.Height * 0.09));
        int step = band / 2;

        for (int bottom = corner.Bottom; bottom > corner.Top; bottom -= step)
        {
            int top = Math.Max(corner.Top, bottom - band);
            var strip = new Rectangle(corner.X, top, corner.Width, bottom - top);
            if (strip.Height < 24) continue;

            foreach (var (k, v) in _ocr.FindLabelled(strip, labels))
                if (!found.ContainsKey(k)) found[k] = v;

            if (labels.All(found.ContainsKey)) break;
        }

        // Life, Shield and Ward are one stacked block - same left edge, same
        // width, evenly spaced - so finding any of them locates the others.
        // Life is the one that keeps not coming back, and it is the one that
        // matters, so it is placed from Shield when its own word will not read.
        //
        // Across every band rather than inside one: the two lines can easily
        // fall either side of a band edge, and then neither knows about the
        // other.
        if (labels.Contains("Life") && !found.ContainsKey("Life")
            && found.TryGetValue("Shield", out var shield))
        {
            var life = new Rectangle(shield.X, shield.Y - shield.Height,
                                     shield.Width, shield.Height);
            if (life.Y >= corner.Top)
            {
                found["Life"] = life;
                Log.Write($"placed Life at {life} - one line above Shield, which was found; "
                          + "its own word did not come back readable");
            }
        }

        return found;
    }

    /// <summary>Where our own window is, so we never read ourselves.</summary>
    public Rectangle OwnWindow { get; set; }

    /// <summary>Where the readout is, so its own bars are never mistaken for the game's.</summary>
    public Rectangle OverlayBounds { get; set; }

    private static bool Overlaps(Rectangle a, Rectangle b) =>
        a.Width > 0 && a.Height > 0 && a.IntersectsWith(b);

    private static bool Overlaps(Rectangle a, Rectangle[] any)
    {
        foreach (var b in any) if (Overlaps(a, b)) return true;
        return false;
    }

    /// <summary>
    /// Where the life and mana numbers live: the bottom corners, the same
    /// bands the search itself looks in.
    /// </summary>
    private static Rectangle[] NumberCorners(Rectangle game)
    {
        int w = Math.Max(320, (int)(game.Width * 0.28));
        int h = Math.Max(200, (int)(game.Height * 0.40));
        return
        [
            new Rectangle(game.Left, game.Bottom - h, w, h),
            new Rectangle(game.Right - w, game.Bottom - h, w, h),
        ];
    }

    private int _refinding;
    private long _refoundAtMs = long.MinValue / 2;
    private long _textOkAtMs;

    /// <summary>
    /// Puts the numbers boxes back when they have stopped landing on anything.
    ///
    /// A box is a rectangle on a screen, and screens change: a resolution, a
    /// HUD scale, a different monitor. When it stops covering the numbers it
    /// does not fail loudly - it simply never reads again, and everything
    /// downstream quietly goes back to the globe pixels, which cannot tell a
    /// dead character from a full one. That is what a box sitting 150 pixels
    /// above the life line did, and it sat there through a death.
    ///
    /// Nothing here is a guess: the same label search that set the box in the
    /// first place is run again, and it either finds the words or changes
    /// nothing.
    /// </summary>
    private void RefindLostNumbers(long now, bool focused, nint gameWnd, Rectangle game)
    {
        if (!_ocr.Available) return;

        // Not "only while the game has focus". Somebody with a broken setup is
        // looking at this window, which means the game does not have focus,
        // which means the one thing that would repair it never ran. Reading the
        // screen does not need focus - it needs the game to be drawn and not
        // covered by us.
        if (gameWnd == 0) return;
        // Only the corners matter, not the whole game window.
        //
        // This used to refuse to repair anything while our own window overlapped
        // the game at all - and the app is a window somebody has open on top of
        // the game while they read it, so that is nearly always. The result was
        // a setup that could not fix itself precisely while being watched: the
        // log filled with the life box reading "071 (Q 71" and the mana box
        // reading "Spirit 0/90" for minutes on end, with the repair standing by
        // and never once running.
        //
        // What actually stops a re-find is our window sitting over the numbers,
        // which live in the bottom corners. Anywhere else on the screen is none
        // of its business.
        if (Overlaps(OwnWindow, NumberCorners(game))) return;

        // Never set up at all is exactly as worth repairing as set up and
        // broken - more so, since nothing has ever worked.
        bool anythingToDo = _cfg.Life.UseText || _cfg.Mana.UseText;
        if (!anythingToDo) return;

        // Reading something is not the same as reading it correctly.
        //
        // The repair below only ever ran when nothing at all came back, which
        // left the worse fault untouched: a box that reads perfectly most of
        // the time and comes back as "1,49 6/1149 6s" the rest of it. That
        // looks like a working setup by every measure here, and it is the one
        // that emptied a flask belt at full life.
        //
        // A box that garbles a quarter of what it sees is a box in the wrong
        // place or too tight around the digits, and it is repaired on the same
        // footing as one that reads nothing.
        foreach (string pool in new[] { "Life", "Mana" })
        {
            _ocr.GarbleRate(pool, out int garbled, out int attempts);
            if (attempts < 12 || garbled * 4 < attempts) continue;

            if (now - _refoundAtMs < 60000) return;
            _refoundAtMs = now;
            _ocr.ForgetGarble(pool);
            Log.Write($"{pool}: {garbled} of the last {attempts} reads came back garbled "
                      + "- finding the numbers again");
            RefindNow();
            return;
        }

        bool reading = (_ocr.TryGet("Life", out var l) && _ocr.NowMs - l.AtMs < 5000)
                       || (_ocr.TryGet("Mana", out var m) && _ocr.NowMs - m.AtMs < 5000);
        if (reading) { _textOkAtMs = now; return; }

        if (_textOkAtMs == 0) { _textOkAtMs = now; return; }
        if (now - _textOkAtMs < 20000) return;
        if (now - _refoundAtMs < 60000) return;

        _refoundAtMs = now;
        _textOkAtMs = now;

        Log.Write("numbers: nothing read for 20 seconds - looking for the lines again");
        RefindNow();
    }

    /// <summary>
    /// Finds the number lines again, off the poll thread.
    ///
    /// This searches whole corners of the screen at several magnifications and
    /// takes seconds, and it was once running inside the poll loop - so every
    /// reading, memory included, stopped dead for as long as it took. On a
    /// machine where the numbers need finding often, that is the life value
    /// updating once every second or three.
    /// </summary>
    private void RefindNow()
    {
        if (Interlocked.Exchange(ref _refinding, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                string what = FindAllNumbers();

                // The rest of what setup does, without the button or the box.
                //
                // Repairing only the numbers was repairing the part that was
                // easiest to name. A globe region left behind by a resolution
                // change, or a memory search still hunting the previous
                // character's maximum, are the same fault wearing different
                // clothes, and both of them used to wait for somebody to think
                // of pressing a button.
                FindGlobes();

                // Every caller here is chasing a bad NUMBERS box - a garbled
                // read, a stale line, one that disagrees with a memory lock
                // that has already proven itself. A structured lock is not a
                // guess sharing the blame; tearing it up on the same trip cost
                // the only working reading for the several seconds memory took
                // to re-tie two candidates and pick one again, and every one of
                // those seconds fell back to the very numbers just declared
                // unreliable - "numbers not trusted" once every twenty seconds,
                // forever, while nothing was actually wrong with memory at all.
                if (_cfg.UseMemory && !_mem.Structured) _mem.Rescan();
                Log.Write($"numbers: {what.Replace(Environment.NewLine, " / ")}");
            }
            catch (Exception ex)
            {
                Log.Write($"numbers: looking again failed - {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _refinding, 0);
            }
        });
    }

    /// <summary>
    /// Picks the crop that reads reliably, not merely the one that reads.
    ///
    /// Tried as found first, then with a little more room each time. Whichever
    /// gets the most clean reads out of five wins, and anything that manages
    /// all five is taken immediately.
    /// </summary>
    private Rectangle Settle(Rectangle box, string label)
    {
        Rectangle best = box;
        int bestScore = -1;

        // Up and down as well as wider.
        //
        // Padding alone could never fix the fault that actually happens: mana,
        // spirit and rage are stacked one under the other in the same corner,
        // and a box a line too low reads "Spirit 0/130" forever. Growing it
        // only swallowed more of the neighbour. The log shows that box being
        // re-found every minute, all evening, and landing on Spirit every time,
        // because nothing in the repair could move it to another line.
        //
        // A line is about the height of the box, so the box is offered its own
        // line and the two either side of it, and whichever crop actually reads
        // this stat wins. A crop that reads nothing scores nothing, so the
        // original is kept when none of them is better.
        int line = Math.Max(12, box.Height);

        foreach (int dy in new[] { 0, -line, line, -line / 2, line / 2, -line * 2 })
        {
            foreach (int pad in new[] { 0, 4, 9, 15 })
            {
                var tryBox = Rectangle.Inflate(
                    box with { Y = box.Y + dy }, pad, pad / 2);
                if (tryBox.Width <= 0 || tryBox.Height <= 0) continue;

                int clean = 0;
                for (int i = 0; i < 5; i++)
                    if (_ocr.VerifyRegion(tryBox, label, out _, out _, out _)) clean++;

                if (clean > bestScore)
                {
                    bestScore = clean;
                    best = tryBox;
                }

                if (clean == 5) goto settled;
            }
        }

        settled:
        if (bestScore <= 0)
            Log.Write($"setup: none of the crops tried read {label}'s numbers - keeping "
                      + $"{best} and letting it be re-read as you play");
        else if (bestScore < 5)
            Log.Write($"setup: {label}'s numbers read cleanly {bestScore} times out of 5 "
                      + $"at {best} - the best of the crops tried");
        else if (best != box)
            Log.Write($"setup: {label}'s box moved to {best} - it read every time there");

        return best;
    }

    private long st_LastArgueMs;

    private void AdoptChangedMax(string name, WatcherConfig c)
    {
        long now = _ocr.NowMs;
        if (!c.UseText || !c.TextRegion.IsValid) return;

        // Nothing entered: learn it. This is the whole chain - the numbers give
        // the maximum, the maximum lets the memory search find you, and nobody
        // types anything.
        // Whatever the numbers have settled on, whether or not something is
        // stored. A stored maximum that is only replaced when someone notices
        // is a stored maximum that is wrong for the whole of a level.
        int seen = _ocr.StableMaxOf(name);
        if (seen <= 0 || seen == c.KnownMax) return;

        // Two things were adopting maxima and neither would yield.
        //
        // The log shows them trading the same setting back and forth ten
        // milliseconds apart, five times a minute, forever:
        //
        //     Mana: maximum 187 refreshed from memory (was 100)
        //     Mana: maximum changed from 187 to 100 - adopted
        //
        // Every one of those adoptions threw the memory address away and
        // started the search again, which is why memory could never settle.
        // The mana box was reading rubbish - it had returned "IVI" a moment
        // earlier - and memory was reading 187 out of the game consistently.
        //
        // So memory wins where it has actually locked on. It is the better
        // source; letting the worse one overrule it, repeatedly, was never
        // going to end.
        if (_mem.Structured && _mem.TryGet(out var known))
        {
            int fromMemory = name switch
            {
                "Life" => known.MaxHp,
                "Shield" => known.MaxEs,
                _ => known.MaxMp,
            };

            if (fromMemory > 0 && fromMemory != seen)
            {
                if (now - st_LastArgueMs > 30000)
                {
                    st_LastArgueMs = now;
                    Log.Write($"{name}: the numbers say the maximum is {seen} and memory "
                              + $"says {fromMemory} - keeping memory's, and leaving the "
                              + "box alone until it agrees");
                }
                return;
            }
        }

        int was = c.KnownMax;
        c.KnownMax = seen;
        Log.Write(was == 0
            ? $"{name}: maximum read as {seen} - filled in"
            : $"{name}: maximum changed from {was} to {seen} - adopted");

        SyncTextRegions();
        if (_cfg.UseMemory) _mem.Rescan();
        MaxAdopted?.Invoke(name, was, seen);
    }

    /// <summary>A maximum the numbers keep showing that disagrees with yours.</summary>
    public int SuggestedMax(string name) => _ocr.SuggestedMax(name);

    /// <summary>
    /// Sets up every stat line it can find in one go: the numbers in the game's
    /// bottom corners, located by their own labels.
    /// </summary>
    public string FindAllNumbers()
    {
        if (!_ocr.Available) return "Windows OCR is not available on this machine.";

        var area = Native.FindWindowRect(_cfg.WindowMatch)
                   ?? (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;

        // Life, shield and ward sit in one corner and mana in the other, so
        // look along the bottom of the game rather than at all of it.
        int w = Math.Max(320, (int)(area.Width * 0.28));
        int h = Math.Max(200, (int)(area.Height * 0.40));
        var left = new Rectangle(area.Left, area.Bottom - h, w, h);
        var right = new Rectangle(area.Right - w, area.Bottom - h, w, h);

        // In bands, from the bottom up, rather than as one tall corner.
        //
        // The chat window lives in the same corner as life, shield and ward,
        // and it is full of text. Handed the whole corner at once, the engine
        // came back with three lines of somebody selling an ascendancy and
        // nothing else - the stat lines are small and pale and simply lost
        // among it. A band a few lines tall cannot be drowned that way.
        var hits = Bands(area, left, ["Life", "Shield"]);
        foreach (var (k, v) in Bands(area, right, ["Mana"])) hits[k] = v;

        // Some layouts put them all together; if mana was not on the right,
        // look where life was.
        if (!hits.ContainsKey("Mana"))
            foreach (var (k, v) in Bands(area, left, ["Mana"])) hits[k] = v;

        // Still no life. Its word will not read on some machines at all, and
        // its neighbour's did not either, so fall back to the shape of the
        // block: in that corner, stacked pairs of numbers sharing a left edge
        // are life, shield and ward, in that order. Nothing else down there is
        // written that way.
        if (!hits.ContainsKey("Life"))
        {
            var stack = _ocr.FindStackedPairs(left);
            if (stack.Count > 0)
            {
                hits["Life"] = stack[0];
                Log.Write($"placed Life at {stack[0]} - the top of {stack.Count} stacked "
                          + "number pairs in that corner; no label could be read");
            }
        }

        var done = new List<string>();
        foreach (var (label, cfg) in new[]
                 { ("Life", _cfg.Life), ("Mana", _cfg.Mana), ("Shield", _cfg.Shield) })
        {
            if (!hits.TryGetValue(label, out var r)) continue;
            cfg.TextRegion = Box.From(r);
            cfg.TextLabel = label;
            cfg.UseText = true;
            done.Add(label);
        }

        SyncTextRegions();
        if (done.Count == 0)
            return "Could not find the numbers. Is the game on screen, with life and mana "
                 + "showing? They are only drawn during play.";

        // Reading each one back is the only way to know it landed on the right
        // line. A box a few pixels out lands on the line below, and life
        // reading the shield value looks perfectly healthy until it kills you.
        var report = new List<string>();
        var seen = new Dictionary<string, int>();

        // Taken before anything is adopted, so a change of character shows.
        int lifeBefore = _cfg.Life.KnownMax;

        foreach (var (label, cfg) in new[]
                 { ("Life", _cfg.Life), ("Mana", _cfg.Mana), ("Shield", _cfg.Shield) })
        {
            if (!done.Contains(label)) continue;

            // A box that reads once is not a box that reads.
            //
            // This is what was behind the flask belt being emptied at full
            // life: a crop a few pixels too tight round the digits, which reads
            // "1,496/1,496" perfectly most of the time and "1,49 6/1149 6s" the
            // rest of it. One successful read was enough to accept it, and the
            // failures then arrived mid-fight, steadily, looking exactly like a
            // character at six life.
            //
            // So it is asked several times, and given room if it stumbles. A
            // slightly larger box costs nothing - the line is found by its own
            // words and numbers, not by the edges of the rectangle.
            cfg.TextRegion = Box.From(Settle(cfg.TextRegion.ToRect(), label));

            if (!_ocr.VerifyRegion(cfg.TextRegion.ToRect(), label,
                                   out int cur, out int max, out string raw))
            {
                // The box is kept. The search had already read the whole line
                // out of that exact rectangle - "Shield 3,208/3,342" - and this
                // second, weaker read is only being asked to agree. Throwing
                // the box away because a re-read came back short is the same
                // mistake as letting the globe pixels veto the numbers: a worse
                // check discarding a better result, and it left Life and Shield
                // with no box at all after both had just been found.
                report.Add($"{label}: box set, but reading it back gave"
                           + (raw.Length > 0 ? $" only \"{raw}\"" : " nothing")
                           + " - it will be re-read as you play");
                continue;
            }

            seen[label] = max;
            report.Add($"{label}: {cur:N0}/{max:N0}");

            // Adopt it. All of them, not just life.
            //
            // Setup read "Mana 117/117" off the screen, put it in the report,
            // and threw the number away - because only life's maximum was ever
            // taken from here. So a new character showed 138 life correctly
            // beside a maximum mana of 581 belonging to the last one, and the
            // memory search went hunting a number that no longer existed
            // anywhere in the game. That is the same staleness that stopped
            // memory working for days, in a second place.
            if (cfg.KnownMax != max)
            {
                Log.Write(cfg.KnownMax == 0
                    ? $"setup: maximum {label.ToLowerInvariant()} set to {max} from the numbers"
                    : $"setup: maximum {label.ToLowerInvariant()} {cfg.KnownMax} is out of "
                      + $"date - the numbers say {max}");
                cfg.KnownMax = max;
                MaxAdopted?.Invoke(label, 0, max);
            }
        }

        // A different life total is a different character, and the maxima that
        // were not re-read belong to whoever it was before. A stored number
        // nothing has confirmed is worse than no number at all: it is what the
        // memory search hunts for, and it will never be found.
        if (seen.TryGetValue("Life", out int nowLife) && lifeBefore != 0
            && nowLife != lifeBefore)
        {
            foreach (var (label, cfg) in new[]
                     { ("Mana", _cfg.Mana), ("Shield", _cfg.Shield) })
            {
                if (seen.ContainsKey(label) || cfg.KnownMax == 0) continue;
                Log.Write($"setup: this is a different character - forgetting the stored "
                          + $"maximum {label.ToLowerInvariant()} of {cfg.KnownMax}");
                cfg.KnownMax = 0;
                report.Add($"{label}: stored maximum was the last character's - cleared");
            }
        }


        // Two stats reading the same numbers means one box is on the other's
        // line. Life reading shield is the dangerous direction.
        // Two stats reading the same numbers means one box is on the other's
        // line. Life reading shield is the dangerous direction - but the common
        // case is a character with no energy shield at all, where the shield
        // line reads 0/0, is refused as implausible, and the search settles on
        // life instead. Telling somebody to go and drag a box for a stat they
        // do not have is no kind of answer, so the duplicate is simply dropped.
        if (seen.TryGetValue("Life", out int lifeMax)
            && seen.TryGetValue("Shield", out int shieldMax)
            && lifeMax == shieldMax)
        {
            _cfg.Shield.TextRegion = new Box();
            seen.Remove("Shield");
            report.RemoveAll(line => line.StartsWith("Shield:", StringComparison.Ordinal));
            report.Add("Shield: it is reading your life, not a shield - dropped. If you do "
                       + "have energy shield, use Numbers... on the shield row.");
            Log.Write("setup: shield box was on the life line - dropped");
        }

        foreach (var (a, b) in new[] { ("Life", "Mana"), ("Mana", "Shield") })
        {
            if (!seen.TryGetValue(a, out int x) || !seen.TryGetValue(b, out int y)) continue;
            if (x != y) continue;
            report.Add($"WARNING: {a} and {b} are reading the same numbers - one box is on "
                       + "the other's line. Use Numbers... on that panel and drag it yourself.");
        }

        SyncTextRegions();
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>Reads a region once, for the setup button.</summary>
    public string ProbeText(Rectangle r, out Bitmap? shot) => _ocr.ProbeOnce(r, out shot);

    /// <summary>Loudest boost the ding can take without clipping, in dB.</summary>
    public static int MaxGainDb => Chime.MaxGainDb;

    /// <summary>Rebuilds the ding at a new level and plays it once.</summary>
    public void SetSoundGain(int db)
    {
        _chime.GainDb = db;
        _chime.Play(0);
    }

    public void SetArmed(bool value)
    {
        if (Armed == value) return;
        Armed = value;
        Log.Write(value ? "ARMED" : "DISARMED");
        ArmedChanged?.Invoke(value);
    }

    public void Toggle() => SetArmed(!Armed);

    /// <summary>Fires a key straight away, ignoring arm state, so a keybind can
    /// be proven to reach the game.</summary>
    public void TestKey(string key, int holdMs) =>
        _keys.Send(key, holdMs, 1, 40, PostingKeys, GameWindow);

    /// <summary>Posting to the window rather than injecting.</summary>
    private bool PostingKeys =>
        _cfg.InputMethod.Equals("postmessage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Handle of the game window, looked up rarely and cached.</summary>
    /// <summary>The game's window, or 0 when it is not up.</summary>
    internal nint GameWindow
    {
        get
        {
            if (_gameWindow != 0 && Native.IsWindowVisible(_gameWindow)) return _gameWindow;
            _gameWindow = Native.FindWindowHandle(_cfg.WindowMatch);
            return _gameWindow;
        }
    }

    private nint _gameWindow;

    public void Start()
    {
        if (_task is not null) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(1000); } catch { /* shutting down */ }
        _task = null;
    }

    private sealed class State
    {
        public readonly ScreenCapture Cap = new();
        public int Below;
        public long LastFireMs = long.MinValue / 2;

        // Short history, so we can tell a slow bleed from a hit that is about
        // to kill us and react differently to each.
        private readonly Queue<(long Ms, double Frac)> _hist = new();

        /// <summary>The highest reading in the last third of a second.</summary>
        public double RecentHigh
        {
            get
            {
                double top = 0;
                foreach (var (_, f) in _hist) top = Math.Max(top, f);
                return top;
            }
        }

        public void Push(long ms, double frac)
        {
            _hist.Enqueue((ms, frac));
            while (_hist.Count > 0 && ms - _hist.Peek().Ms > 350) _hist.Dequeue();
        }

        /// <summary>How fast the globe is emptying, in percent per second.
        /// Positive means falling.</summary>
        public double DropPctPerSec(long ms, double frac)
        {
            if (_hist.Count == 0) return 0;
            var (oldMs, oldFrac) = _hist.Peek();
            long dt = ms - oldMs;
            if (dt < 40) return 0;
            return (oldFrac - frac) * 100.0 * 1000.0 / dt;
        }

        public void Reset() => _hist.Clear();

        // --- effect verification ---
        public bool Verifying;
        public long FireMs;
        public double FracAtFire;
        public double MaxSinceFire;
        public int NoEffect;
        public long LastWouldFireMs = long.MinValue / 2;
        public long BlindSinceMs;
        public long LastGoodMs = long.MinValue / 2;
        public long LastDisagreeMs = long.MinValue / 2;
        public long LastMemBadMs = long.MinValue / 2;
        public long NoGoodSourceSinceMs;
        public bool HadGoodSource;
        public double LastGoodFrac;
        /// <summary>
        /// The last reading that made it all the way past the trust gate,
        /// not merely "came from an exact source" the way LastGoodFrac does -
        /// "numbers," is an exact source and can still be untrusted, so using
        /// LastGoodFrac to bridge a safety net through a hold would let an
        /// untrusted reading vouch for itself. Set only where a reading is
        /// actually about to be acted on.
        /// </summary>
        public double LastTrustedFrac;
        public long LastTrustedAtMs = long.MinValue / 2;
        /// <summary>
        /// Presses sent by the emergency net since it was last above its
        /// floor. Was a bool - one press, never again until recovered.
        /// Below the floor, still falling, that one press is not always
        /// enough; it can now fire again as long as life has not turned
        /// around, so this counts rather than flags.
        /// </summary>
        public int UberUsed;
        public double NetHeldAt = -1;
        public long ZeroSinceMs;
        /// <summary>Presses sent by the last-ditch net, capped at three.</summary>
        public int LastDitchUsed;
        public long BackoffUntilMs;
        public bool Blind;

        // Per pool, not per app. These were three single fields shared by life,
        // mana and shield: life would confirm its address and mana, sampled a
        // moment later with its own maximum, would immediately unconfirm it -
        // and each pool's disagreement timer reset the others'. Anyone who
        // switched mana on had the two of them cancelling each other out all
        // session.
        public int MemGeneration = -1;
        public bool MemConfirmed;
        public double DisOcrFirst, DisOcrLast, DisMemFirst, DisMemLast;
        public long LastOcrAtMs;
        public int LastOcrCur;
        public int LastOcrMax;
        public int OcrRepeats;
        public long LastBoxDoubtMs = long.MinValue / 2;
        public bool GlobeCovering;
        public long MemDisagreeSinceMs;
        public int LastMemCur = -1;
        public long MemSameSinceMs;

        /// <summary>Whether this address has ever been seen to change.</summary>
        public bool MemMoved;
        public int MemFirstCur = -1;
    }

    private void Loop(CancellationToken ct)
    {
        _started = Environment.TickCount64;

        var life = new State();
        var mana = new State();
        var shield = new State();
        var clock = Stopwatch.StartNew();

        long lastUiMs = 0, hzWindowMs = 0, lastLogMs = 0;
        int polls = 0;

        double lastLife = -1;
        long lastDropMs = long.MinValue / 2;
        long gameGoneAtMs = long.MinValue / 2;

        // Without this the scheduler rounds every sleep up to ~15 ms, which
        // caps the loop near 60 Hz no matter what poll rate is asked for.
        Native.timeBeginPeriod(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long t0 = clock.ElapsedMilliseconds;
                bool focused = WindowFocused();

                // One desktop-wide window enumeration per tick, not up to
                // three: memory wanted the handle, the bar-follow and the
                // numbers-repair check each wanted the rect, and each used to
                // ask EnumWindows for it separately - at up to 250 times a
                // second whether or not the game was even running.
                nint gameWnd = Native.FindWindow(_cfg.WindowMatch, out Rectangle gameRect);
                bool gameRunning = gameWnd != 0;

                if (gameRunning) gameGoneAtMs = long.MinValue / 2;
                else if (gameGoneAtMs == long.MinValue / 2) gameGoneAtMs = t0;

                // Both maxima go to the search every pass, whether or not that
                // globe is switched on. Life alone does not identify the
                // structure - the heap is full of pairs - and mana being off is
                // no reason to withhold what we know about it.
                if (_cfg.UseMemory)
                {
                    // The process behind the window we already found, so a
                    // standalone or Epic install is attached to as readily as
                    // the Steam one.
                    if (gameWnd != 0)
                    {
                        Native.GetWindowThreadProcessId(gameWnd, out uint gamePid);
                        if (gamePid != 0) _mem.PreferredPid = (int)gamePid;
                    }

                    if (_cfg.Shield.Enabled && _cfg.Shield.KnownMax <= 0
                        && _mem.TryGet(out var es) && es.MaxEs > 0)
                    {
                        _cfg.Shield.KnownMax = es.MaxEs;
                        Log.Write($"Shield: maximum {es.MaxEs:N0} taken from memory");
                        MaxAdopted?.Invoke("Shield", 0, es.MaxEs);
                    }

                    // Keep the stored maxima honest while memory is speaking.
                    //
                    // A saved maximum mana of 597 - true when it was saved,
                    // wrong after a gear change - is what stopped the search
                    // finding anything at all for days: it was hunting a number
                    // the game no longer held. Nothing tells you a saved number
                    // has gone stale, so the fix is to stop letting it.
                    // Only from an address the numbers have vouched for.
                    //
                    // Taken from any structured address, this wrote a maximum
                    // life of 322 into the settings from an address that was
                    // declared stale sixteen seconds later. The screen said
                    // 278 throughout. Everything downstream then hunted 322:
                    // the numbers were refused for disagreeing with it, which
                    // made every read count as garbled, which triggered a
                    // repair, which re-ran setup - once per level.
                    if (_mem.Structured && _lifeMemConfirmed && _mem.TryGet(out var live))
                    {
                        // Small changes at once - that is levelling. A big one
                        // only after the same character has held for twenty
                        // seconds, because a big change a moment after a lock
                        // is exactly what a reused slot looks like, and one of
                        // those wrote a monster's 8,178 in as your life.
                        bool steady = _mem.LockedForMs >= 20000;
                        bool Small(int was, int now) => was <= 0 || Math.Abs(now - was) * 4 <= was;
                        if (live.MaxMp > 0 && _cfg.Mana.KnownMax != live.MaxMp
                            && (steady || Small(_cfg.Mana.KnownMax, live.MaxMp)))
                        {
                            Log.Write($"Mana: maximum {live.MaxMp:N0} refreshed from memory "
                                      + $"(was {_cfg.Mana.KnownMax:N0})");
                            _cfg.Mana.KnownMax = live.MaxMp;
                        }
                        if (live.MaxEs > 0 && _cfg.Shield.KnownMax != live.MaxEs
                            && (steady || Small(_cfg.Shield.KnownMax, live.MaxEs)))
                            _cfg.Shield.KnownMax = live.MaxEs;
                        if (live.MaxHp > 0 && _cfg.Life.KnownMax != live.MaxHp
                            && (steady || Small(_cfg.Life.KnownMax, live.MaxHp)))
                        {
                            Log.Write($"Life: maximum {live.MaxHp:N0} refreshed from memory "
                                      + $"(was {_cfg.Life.KnownMax:N0})");
                            _cfg.Life.KnownMax = live.MaxHp;
                        }
                    }

                    // A stored maximum nothing in the game holds is wrong, and
                    // keeping it means searching for it forever. Let it go and
                    // the numbers fill it back in within seconds.
                    // Unless the screen is saying that very number.
                    //
                    // This was meant for a maximum left over from an older
                    // character, which nothing in the game holds and nothing
                    // can correct. It does not apply when the numbers beside
                    // the globe are reading it back at you: there, the number
                    // is right and the search is what is failing, and throwing
                    // the number away only means the numbers re-read it a
                    // moment later and the whole thing goes round again.
                    //
                    // It went round four hundred times in a few minutes, and
                    // every lap threw away the memory search that was halfway
                    // through finding the answer.
                    int onScreen = _ocr.StableMaxOf("Life");

                    if (_mem.MaxMissing >= 3 && _cfg.Life.KnownMax > 0
                        && onScreen != _cfg.Life.KnownMax
                        && _ocr.NowMs - _forgotMaxAtMs > 60000)
                    {
                        _forgotMaxAtMs = _ocr.NowMs;
                        Log.Write($"Life: nothing in the game holds a maximum of "
                                  + $"{_cfg.Life.KnownMax} - that number is wrong, so it is "
                                  + "being forgotten and read again from the screen");
                        _cfg.Life.KnownMax = 0;
                        _ocr.ForgetGarble("Life");
                        MaxAdopted?.Invoke("Life", 0, 0);
                        RefindNow();
                    }

                    _mem.HintMaxHp = ExpectedMax("Life", _cfg.Life, 0);
                    _mem.HintMaxMp = ExpectedMax("Mana", _cfg.Mana, 0);
                    _mem.HintMaxEs = _cfg.Shield.KnownMax;

                    // The currents as well, when the numbers can be read. The
                    // maxima locate the structure; only the current tells the
                    // search which integer beside it is actually yours.
                    // Held for a good while rather than only while fresh. A
                    // search runs at the moment memory was thrown out, which is
                    // exactly when the numbers are most likely to be missing -
                    // so insisting on a reading from the last second meant the
                    // search ran blind and picked the same wrong address again.
                    if (_ocr.TryGet("Life", out var lh) && _ocr.NowMs - lh.AtMs < 20000)
                        _mem.HintCurHp = lh.Current;
                    if (_ocr.TryGet("Mana", out var mh) && _ocr.NowMs - mh.AtMs < 20000)
                        _mem.HintCurMp = mh.Current;
                }

                RefindLostNumbers(t0, focused, gameWnd, gameRect);

                // Decoration, and rate limited inside, so it can never compete
                // with the reading that decides whether to press a key.
                // Not "while the game has focus". The game keeps drawing while
                // you look at something else, and a screen capture does not
                // care what is focused - so requiring it meant the bar was
                // never looked for the moment you alt-tabbed, and the readout
                // vanished because nothing had found it.
                // Whenever the readout is up, not only while it is following
                // something. Saving a spot needs to know where the character
                // is, and saving a spot is what switches the following on - so
                // requiring it first meant the very first attempt could never
                // work.
                if (_cfg.OverlayOn && gameRunning)
                {
                    _bar.Ignore = OverlayBounds;
                    _bar.Look(gameRect);
                }

                AdoptChangedMax("Life", _cfg.Life);
                AdoptChangedMax("Mana", _cfg.Mana);
                AdoptChangedMax("Shield", _cfg.Shield);

                var lr = Sample(life, _cfg.Life, "Life", focused, clock, gameRunning);
                var mr = Sample(mana, _cfg.Mana, "Mana", focused, clock, gameRunning);
                var sr = Sample(shield, _cfg.Shield, "Shield", focused, clock, gameRunning);

                if (_cfg.LowLifeEnabled)
                {
                    if (!_lowLifeWasEnabled) ResetLowLifeTiers();
                    _lowLifeWasEnabled = true;

                    // Same bar every other press in this app has to clear:
                    // only while armed, and only while the game is actually
                    // focused.
                    if (Armed && focused) LowLifeCheck(shield, sr, t0);
                }
                else _lowLifeWasEnabled = false;

                // A fight is life going down. Regeneration and leech send it up
                // constantly, so a rise says nothing was hitting you - only a
                // drop does.
                if (lr.Ok && lr.Note.Length == 0)
                {
                    if (lastLife >= 0 && lr.Fraction < lastLife - 0.01) lastDropMs = t0;
                    lastLife = lr.Fraction;
                }

                // Read the numbers harder when anything is near its trigger.
                // Reading flat out all the time is wasted work while healthy,
                // and reading lazily is exactly wrong while dropping.
                bool nearTrouble =
                    (lr.Ok && _cfg.Life.Enabled && lr.Fraction < _cfg.Life.Threshold + 0.15)
                    || (mr.Ok && _cfg.Mana.Enabled && mr.Fraction < _cfg.Mana.Threshold + 0.15)
                    || (sr.Ok && _cfg.Shield.Enabled
                        && sr.Fraction < _cfg.Shield.Threshold + 0.15)
                    // Low Life setup reads shield instead, so it is shield's
                    // own first floor - not shield's Threshold, which does
                    // not apply while this owns the trigger - that says when
                    // reading needs to speed up.
                    || (sr.Ok && _cfg.LowLifeEnabled
                        && sr.Fraction < _cfg.LowLifeTier1 + 0.15);
                // Once memory is confirmed it decides everything, and the
                // numbers are only there to catch it pointing at the wrong
                // thing. Reading them several times a second for that is what
                // makes a machine hitch: each read is a screen grab and a
                // recognise. Once a second is plenty to police a liar.
                // Once memory is locked it decides everything and the numbers
                // have one job left: noticing if it is ever pointed at the
                // wrong thing. That is a check, not a reading, and a check does
                // not need doing several times a second - every five is plenty
                // and costs nothing. Until it is locked, they are what is
                // keeping you alive, so they are read hard.
                // Not so rare that it goes stale as a fallback: if memory ever
                // loses its address, these are what is left, and a reading two
                // seconds old is one that has to be waited for.
                _ocr.SetInterval(_lifeMemConfirmed ? 1500 : nearTrouble ? 60 : 160);
                _ocr.SetGameRunning(StillCountsAsRunning(gameRunning, gameGoneAtMs, t0,
                                                         GameGoneGraceMs));

                bool fighting = t0 - lastDropMs < _cfg.CombatGraceMs;
                if (fighting != InCombat)
                {
                    InCombat = fighting;
                    if (!fighting)
                    {
                        if (FiresThisFight > 0)
                        {
                            lock (_firesByPool)
                                Log.Write("fight over: "
                                    + string.Join(", ", _firesByPool
                                        .Where(p => p.Value > 0)
                                        .Select(p => $"{p.Value} {p.Key.ToLowerInvariant()}"))
                                    + " press(es) sent");
                        }
                        FiresThisFight = 0;
                        lock (_firesByPool)
                            foreach (string pool in _firesByPool.Keys.ToList())
                                _firesByPool[pool] = 0;
                    }
                }

                LastPollMs = clock.Elapsed.TotalMilliseconds - t0;

                polls++;
                if (t0 - hzWindowMs >= 1000)
                {
                    ActualHz = polls;
                    polls = 0;
                    hzWindowMs = t0;
                }

                // A readable trail of what was seen while armed, so a session
                // that failed to fire can be explained afterwards rather than
                // guessed at.
                if (Armed && t0 - lastLogMs >= 2000)
                {
                    lastLogMs = t0;
                    Log.Write($"watch  life {lr.Fraction:P1}{(_cfg.Life.Enabled ? "" : " (off)")}" +
                              $"  mana {mr.Fraction:P1}{(_cfg.Mana.Enabled ? "" : " (off)")}" +
                              (_cfg.Shield.Enabled ? $"  shield {sr.Fraction:P1}" : "") +
                              $"  focused={focused}  hz={ActualHz}  " +
                              $"skipped={_keys.Skipped}  poll={LastPollMs:0.0}ms");
                }

                // The UI cannot use 100 samples a second and repainting that
                // often would slow the loop it is reporting on.
                if (t0 - lastUiMs >= 60)
                {
                    lastUiMs = t0;
                    Sampled?.Invoke(lr, mr, sr, focused);
                }

                // Noticing the game has appeared a quarter-second late costs
                // nothing; noticing a hit a quarter-second late is the whole
                // job. Polling for a window that is not there at the
                // configured rate - up to 250 times a second - was most of
                // what "P02 idles at 30% of a core with the game closed"
                // turned out to be.
                int period = gameRunning ? 1000 / Math.Clamp(_cfg.PollHz, 5, 250) : 250;
                int sleep = period - (int)(clock.ElapsedMilliseconds - t0);
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"monitor loop died: {ex}");
        }
        finally
        {
            Native.timeEndPeriod(1);
            life.Cap.Dispose();
            mana.Cap.Dispose();
            shield.Cap.Dispose();
        }
    }

    /// <summary>The floating bar over the character, when anyone is asking.</summary>
    private readonly BarFinder _bar = new();

    /// <summary>
    /// Looks for the character right now, ignoring the usual rate limit.
    ///
    /// For the moment somebody saves a spot: the readout is deliberately
    /// sitting on the bars by then, so the caller hides it for an instant and
    /// asks again rather than waiting for a scan that would find nothing.
    /// </summary>
    public Rectangle FindCharacterNow()
    {
        if (Native.FindWindowRect(_cfg.WindowMatch) is not { } client) return Rectangle.Empty;

        _bar.Ignore = OverlayBounds;
        _bar.Look(client, everyMs: 0);
        return CharacterBar;
    }

    /// <summary>Where the character's own life bar is, or empty if it is not up.</summary>
    public Rectangle CharacterBar => _bar.Visible ? _bar.Bar : Rectangle.Empty;

    /// <summary>The run-up to each press, kept so one can be explained later.</summary>
    private readonly FireTrail _trail = new();

    /// <summary>How long the loop has been running, for checks that need warming up.</summary>
    public long UptimeMs => _started == 0 ? 0 : Environment.TickCount64 - _started;

    private long _started;

    /// <summary>Whether memory has an address it is willing to read from.</summary>
    /// <summary>
    /// Whether a reading from the numbers box may press a key on its own.
    ///
    /// Its own label on the line and the maximum exactly the one already
    /// known are required always - a mangled read almost always loses the
    /// label, and a misread digit gets the maximum wrong. The third test used
    /// to be the same reading twice, and that is what nearly killed somebody:
    /// "Life 41/874" arrived once, labelled, maximum exact, and then sat there
    /// UNTRUSTED for over a second because OCR - one engine, shared between
    /// two boxes, sometimes busy for whole seconds during a fight - did not
    /// look at the life box again in time to confirm it. The rule was waiting
    /// on a machine that had nothing left to give, at 4.7% life.
    ///
    /// A second look is worth having when nothing is at stake. It is not
    /// worth a life. So a reading below the panic floor - already labelled,
    /// already the right maximum - is trusted the first time it is seen, and
    /// only a normal-range reading still has to repeat. The fast check at the
    /// point of firing (the impossible-fall look, and panic's own double
    /// check) is what actually catches a misread in this range, and it needs
    /// only the next poll, not a second trip through OCR.
    /// </summary>
    internal static bool TrustNumbers(string raw, string label, int max, int knownMax,
                                      int repeats, double frac, double dangerBelow)
        => (label.Length == 0 || TextOcr.HasLabel(raw, label))
           && (knownMax <= 0 || max == knownMax)
           && (repeats >= 1 || (dangerBelow > 0 && frac < dangerBelow));

    /// <summary>
    /// Whether memory is a frozen copy: through a disagreement, the numbers
    /// moved and memory did not. A misread sits still; a live pool moves.
    /// </summary>
    internal static bool MemoryFrozen(double ocrFirst, double ocrNow,
                                      double memFirst, double memNow)
        => Math.Abs(ocrNow - ocrFirst) >= 0.02 && Math.Abs(memNow - memFirst) <= 0.005;

    /// <summary>
    /// Whether an emergency or last-ditch net may press again: the first
    /// press below the floor is always allowed; after that, only while there
    /// is room left in the count and life has not turned the corner.
    /// </summary>
    internal static bool NetMayFire(int used, int maxUses, bool canRepeat)
        => used == 0 || (used < maxUses && canRepeat);

    /// <summary>
    /// How long a reading that passed every trust check is still allowed to
    /// vouch for the safety nets after the current one stops being trusted.
    /// Short on purpose: this is a bridge across a glitch, not a licence to
    /// keep firing on data from a second ago.
    /// </summary>
    internal const long SafetyNetBridgeMs = 700;

    /// <summary>
    /// Whether a reading from a moment ago - one that itself passed the
    /// trust gate, never the current, untrusted one - may still stand in for
    /// the emergency/last-ditch nets while the current poll is held. Refusing
    /// the nets outright the instant a reading is not trusted was the same
    /// failure from the other direction: "the numbers glitched for one
    /// frame" and "nothing was protecting you" became the same event, for
    /// the two paths that exist specifically for near-death. Never trusted
    /// yet (the sentinel timestamp) never bridges.
    /// </summary>
    internal static bool SafetyNetMayBridge(long lastTrustedAtMs, long nowMs, long bridgeMs)
        => lastTrustedAtMs != long.MinValue / 2 && nowMs - lastTrustedAtMs <= bridgeMs;

    /// <summary>
    /// How long the game's window may go missing from one poll's lookup
    /// before the OCR reader is actually told to stop and pause. Native
    /// window enumeration is not perfectly reliable every single tick - an
    /// alt-tab, a loading screen, a moment of desktop churn - and pausing on
    /// one missed poll costs up to a second of frozen reading the instant it
    /// happens to land mid-fight. Long closures still pause it; a blink does
    /// not.
    /// </summary>
    internal const long GameGoneGraceMs = 3000;

    /// <summary>
    /// Whether the OCR reader should still be told the game is running, given
    /// this poll's own window lookup and how long ago the window was last
    /// actually seen. <paramref name="goneAtMs"/> is the sentinel
    /// (long.MinValue/2) whenever the window is currently found.
    /// </summary>
    internal static bool StillCountsAsRunning(bool foundThisPoll, long goneAtMs, long nowMs,
                                              long graceMs)
        => foundThisPoll || (goneAtMs != long.MinValue / 2 && nowMs - goneAtMs <= graceMs);

    /// <summary>
    /// Whether a Low Life tier may fire: it has not already fired since it
    /// last recovered, and the reading has actually crossed its floor. Mirrors
    /// the shape of the app's other emergency nets - one press per crossing,
    /// not a repeat trigger - applied to shield's reading instead of the
    /// pool's own.
    /// </summary>
    internal static bool LowLifeTierMayFire(bool alreadyFired, double frac, double floor)
        => !alreadyFired && frac <= floor;

    /// <summary>
    /// The same five-point margin the existing emergency nets rearm on, so a
    /// reading sitting exactly on a floor cannot flap a tier on and off every
    /// single poll.
    /// </summary>
    internal const double LowLifeRearmMargin = 0.05;

    /// <summary>Whether a fired tier has recovered enough to be armed again.</summary>
    internal static bool LowLifeTierRearmed(double frac, double floor)
        => frac > floor + LowLifeRearmMargin;

    /// <summary>
    /// How long shield must have read at or below the zero tier, genuinely,
    /// before that floor is trusted enough to fire.
    /// </summary>
    internal const long LowLifeZeroConfirmMs = 300;

    /// <summary>
    /// Whether shield has stayed at or below the zero tier long enough to
    /// trust it, rather than having only just arrived there on the one frame
    /// being looked at right now - a raw single-frame zero is exactly what a
    /// misread or a covered, blind globe looks like too, and this app has
    /// been burned by treating one as truth before.
    /// </summary>
    internal static bool LowLifeZeroConfirmed(long belowSinceMs, long nowMs, long confirmMs)
        => belowSinceMs != long.MinValue / 2 && nowMs - belowSinceMs >= confirmMs;

    /// <summary>
    /// Whether a reading's Fraction is a value carried forward through a real
    /// gap rather than read this poll - "last known X%" is the one marker
    /// Sample() writes for exactly that, when betterWanted/HadGoodSource
    /// substitutes an old value in for a missing fresh one. Shared by every
    /// display that shows a GlobeReading and by Low Life setup's own rearm
    /// check, so all three treat "stale" the same way rather than three
    /// separately-maintained guesses at it.
    /// </summary>
    internal static bool IsStaleCarry(string textRaw)
        => textRaw.StartsWith("last known", StringComparison.Ordinal);

    /// <summary>
    /// Whether a Low Life tier may rearm from this reading: only ever a
    /// reading that is both Ok and carries no note at all - never a stale
    /// carried-forward value, which can sit at a comfortable level for as
    /// long as a real reading gap lasts while the pool underneath it is
    /// still critical. Rearming every tier off that value is exactly how
    /// this stayed quiet through a shield actually carved to nothing.
    /// </summary>
    internal static bool LowLifeMayRearm(bool ok, string note, double frac, double floor)
        => ok && note.Length == 0 && LowLifeTierRearmed(frac, floor);

    /// <summary>What to do when memory and the numbers name different maxima.</summary>
    internal enum Verdict
    {
        /// <summary>They agree, or there is nothing to compare.</summary>
        Agree,

        /// <summary>The box is on the wrong line. Memory carries the pool.</summary>
        TrustMemory,

        /// <summary>Nothing to appeal to. Neither of them fires.</summary>
        HoldBoth,
    }

    /// <summary>
    /// Which of two disagreeing maxima to believe.
    ///
    /// Written once because it was written twice. The same decision existed in
    /// two branches a few lines apart - one comparing memory against the stored
    /// maximum, one against what the numbers had just read - and only the
    /// second ever ran. Fixing the first and shipping it changed nothing at
    /// all, and the log went on printing the old message while mana emptied
    /// its flask at a box reading "19/100" for a pool holding 191. Two copies
    /// of a rule is one copy of a rule and one bug waiting.
    ///
    /// A maximum is not a wobble. A box whose maximum is not the pool's
    /// maximum is on some other line entirely, and everything it reads is about
    /// something else. A structured memory lock is a component whose vitals
    /// point back at it and whose pools are self-consistent; a box is a
    /// rectangle somebody dragged once. So memory wins where it has one.
    ///
    /// Where it has not, neither fires. A flask not thrown costs a charge;
    /// firing on the wrong pool has cost a belt three times.
    /// </summary>
    internal static Verdict Judge(bool structured, int memoryMax, int boxMax)
    {
        if (memoryMax <= 0 || boxMax <= 0) return Verdict.Agree;
        if (memoryMax == boxMax) return Verdict.Agree;

        // How far apart they are is the whole of it.
        //
        // "Not equal" was far too blunt a test, and it went wrong in both
        // directions inside an hour. A box reading 100 for a pool of 191 is on
        // some other line entirely. A box reading 362 where memory has 346 is
        // the same pool, four percent apart, because one of them is a moment
        // behind the other - a level, a gear swap, a flask of life - and
        // declaring that box "on the wrong line" threw away a perfectly good
        // reading that had matched its own label, left the numbers dark, and
        // took the overlay with them.
        //
        // A pool does not change identity by a few percent. Near enough is the
        // same pool, and whichever is stale catches up on its own.
        int bigger = Math.Max(memoryMax, boxMax);
        int apart = Math.Abs(memoryMax - boxMax);
        if (apart * 5 <= bigger) return Verdict.Agree;

        return structured ? Verdict.TrustMemory : Verdict.HoldBoth;
    }

    /// <summary>
    /// The controller button this pool should press, or nothing for the
    /// keyboard.
    ///
    /// Empty unless a pad is being used and a button has actually been chosen -
    /// silently pressing a button nobody picked would be worse than pressing
    /// nothing at all.
    /// </summary>
    private string PadFor(WatcherConfig c)
        => _cfg.UseController && c.PadButton.Length > 0 ? c.PadButton : "";

    /// <summary>How often a pool's numbers come back unreadable.</summary>
    public void GarbleRate(string name, out int garbled, out int attempts)
        => _ocr.GarbleRate(name, out garbled, out attempts);

    /// <summary>Several components match and it is waiting for one to move.</summary>
    public bool MemoryPending => _mem.Pending;

    public bool MemoryLocked => _lifeMemConfirmed && (_lifeMemMoved || _mem.Structured);

    private bool _lifeMemMoved;

    /// <summary>Whether this pool's numbers have produced a reading recently.</summary>
    /// <summary>
    /// Whether any watched pool has a text region actually in play - the
    /// question HudVisible needs answered, and one that used to only ever be
    /// asked of Life.
    ///
    /// A player who switches "Watch my life" off while still watching mana
    /// found the overlay permanently gone: Life's OCR slot is torn down the
    /// moment it is disabled, so a check that only ever looked at Life saw
    /// nothing to read, forever, and fell straight into the "a menu must be
    /// covering it" branch - for a HUD that was never covered, reading a pool
    /// nobody had asked it to watch in the first place.
    /// </summary>
    internal static bool AnyPoolWatchedWithText(WatcherConfig life, WatcherConfig mana,
                                                WatcherConfig shield,
                                                bool lowLifeEnabled = false)
        => (life.Enabled && life.UseText && life.TextRegion.IsValid)
           || (mana.Enabled && mana.UseText && mana.TextRegion.IsValid)
           // Low Life setup reads shield without shield's own Enabled ever
           // being on - see the entry gate in Sample() - so it needs its own
           // way in here too, or the overlay goes back to thinking a menu is
           // covering a HUD it is reading just fine.
           || ((shield.Enabled || lowLifeEnabled) && shield.UseText
               && shield.TextRegion.IsValid);

    /// <summary>
    /// Whether the game's own HUD numbers are on screen right now.
    ///
    /// Asked so the overlay can get out of the way when a shop, the passive
    /// tree or an inventory covers the HUD. It has to be its own question,
    /// because the obvious answer stopped being true: the overlay used to hide
    /// on the reading note "numbers not on screen", and that note is only
    /// produced when nothing else can read the pool. The moment memory locks
    /// on - which is the goal - it reads straight through a shop, no note is
    /// ever produced, and the overlay sat there over the passive tree while
    /// the tickbox insisted it should not.
    ///
    /// With no text region set up there is nothing to judge by, and something
    /// permanently hidden is worse than something occasionally in the way.
    /// </summary>
    public bool HudVisible
    {
        get
        {
            if (!_ocr.Available) return true;
            if (!AnyPoolWatchedWithText(_cfg.Life, _cfg.Mana, _cfg.Shield, _cfg.LowLifeEnabled))
                return true;

            // A disabled pool's OCR slot does not exist - Configure() removed
            // it - so asking for one costs nothing and simply never matches.
            // No need to re-check Enabled here on top of that.
            foreach (string name in new[] { "Life", "Mana", "Shield" })
                if (NumbersReading(name)) { _hudSeenMs = _ocr.NowMs; return true; }

            // The numbers being gone is only evidence of a menu if something
            // else can still see your character.
            //
            // Memory reads straight through a shop, an inventory or the passive
            // tree - so memory reading fine while the numbers have vanished is
            // exactly the shape of a panel sitting over the HUD, and that is
            // worth getting out of the way for.
            //
            // Both of them failing at once says nothing about menus. It is a
            // setup in trouble, and hiding the readout is the last thing that
            // helps: that is how the overlay disappeared for a whole session on
            // a screen with nothing covering anything.
            //
            // Something permanently hidden is worse than something occasionally
            // in the way.
            if (!MemoryLocked) return true;

            // And only if the numbers were ever working, so a box that has
            // never read does not pass for a shop that never closes.
            //
            // Sixty seconds was too short: a crafting bench, the stash, or
            // planning a respec on the passive tree routinely runs past a
            // minute, and every time it did the overlay popped back up over
            // the menu it was supposed to be hiding from - the very "broke it
            // and never fixed it" complaint this guard exists to prevent, just
            // arriving from the other direction. A structured memory lock
            // (checked above) already rules out the original failure this
            // timeout was for - a broken setup masquerading as a shop - so the
            // remaining case is only "genuinely still in a menu," which
            // deserves minutes, not one.
            return !StillCoveredByMenu(_hudSeenMs, _ocr.NowMs, HudGiveUpMs);
        }
    }

    // --- Low Life setup: an "oh shit" net keyed off shield, firing life ----

    private bool _lowLifeTier1Fired, _lowLifeTier2Fired, _lowLifeTier3Fired;
    private long _lowLifeZeroSinceMs = long.MinValue / 2;
    private bool _lowLifeWasEnabled;

    /// <summary>
    /// Every tier armed, fresh. Run once whenever Low Life setup goes from
    /// off to on - including the moment the app starts up already enabled -
    /// so a tier that fired in an earlier session, or before it was switched
    /// off and back on mid-fight, cannot silently skip protection now just
    /// because shield never happened to climb back out in between.
    /// </summary>
    private void ResetLowLifeTiers()
    {
        _lowLifeTier1Fired = _lowLifeTier2Fired = _lowLifeTier3Fired = false;
        _lowLifeZeroSinceMs = long.MinValue / 2;
    }

    /// <summary>
    /// Watches shield's own reading (produced by Sample("Shield", ...), which
    /// Low Life setup keeps running through the entry gate above) and fires
    /// the life flask at three floors, each once per crossing. Entirely
    /// separate from shield's own threshold/panic/emergency-net firing, which
    /// is suppressed for the whole time this is on - see the early return
    /// for "Shield" inside Sample() itself.
    ///
    /// Takes shield's State as well as its latest GlobeReading because the
    /// two answer different questions: the reading says what to show and
    /// whether it is fresh enough to rearm on; the State's own
    /// LastTrustedFrac/AtMs - set only where a reading passed the full trust
    /// gate - is what firing bridges through a hold from, exactly like the
    /// app's other emergency nets.
    /// </summary>
    private void LowLifeCheck(State shieldState, GlobeReading shield, long now)
    {
        // Rearming - "shield has recovered enough to protect again" - only
        // ever happens off a reading that is fully trusted this exact poll
        // (Ok, and no note at all), never off shield.Fraction on its own.
        // During a real reading gap that value can be MonitorEngine's own
        // "last known X%" carried forward from before the gap started - a
        // comfortable-looking number that says nothing about the pool right
        // now, and rearming every tier off it is exactly how this stayed
        // quiet through a shield that had actually been carved to nothing.
        double fresh = shield.Fraction;
        if (LowLifeMayRearm(shield.Ok, shield.Note, fresh, _cfg.LowLifeTier1))
            _lowLifeTier1Fired = false;
        if (LowLifeMayRearm(shield.Ok, shield.Note, fresh, _cfg.LowLifeTier2))
            _lowLifeTier2Fired = false;
        if (LowLifeMayRearm(shield.Ok, shield.Note, fresh, _cfg.LowLifeTier3))
            _lowLifeTier3Fired = false;

        // Firing may still act on the last reading that genuinely passed the
        // trust gate, bridged briefly through a hold - the same discipline
        // the app's other emergency nets already use - rather than either
        // refusing outright the moment shield's reading is not fresh, or
        // trusting a stale value that happens to look safe. Past the bridge
        // window there is nothing left to act on either way.
        if (!SafetyNetMayBridge(shieldState.LastTrustedAtMs, now, SafetyNetBridgeMs))
        {
            _lowLifeZeroSinceMs = long.MinValue / 2;
            return;
        }

        double frac = shieldState.LastTrustedFrac;

        if (frac <= _cfg.LowLifeTier3)
        {
            if (_lowLifeZeroSinceMs == long.MinValue / 2) _lowLifeZeroSinceMs = now;
        }
        else _lowLifeZeroSinceMs = long.MinValue / 2;

        // Lowest floor first: falling through all three in one gap between
        // polls is a real thing a big hit does, and it is the deepest one
        // that actually matters at that point.
        if (LowLifeTierMayFire(_lowLifeTier3Fired, frac, _cfg.LowLifeTier3)
            && LowLifeZeroConfirmed(_lowLifeZeroSinceMs, now, LowLifeZeroConfirmMs))
        {
            _lowLifeTier3Fired = true;
            FireLowLife("shield depleted", frac);
            return;
        }

        if (LowLifeTierMayFire(_lowLifeTier2Fired, frac, _cfg.LowLifeTier2))
        {
            _lowLifeTier2Fired = true;
            FireLowLife("shield's second floor", frac);
            return;
        }

        if (LowLifeTierMayFire(_lowLifeTier1Fired, frac, _cfg.LowLifeTier1))
        {
            _lowLifeTier1Fired = true;
            FireLowLife("shield's first floor", frac);
        }
    }

    private void FireLowLife(string why, double shieldFrac)
    {
        if (!_keys.Send(_cfg.Life.Key, _cfg.Life.HoldMs, 1, 40, PostingKeys, GameWindow,
                        PadFor(_cfg.Life), _cfg.UseController && _cfg.AlsoPressKey))
            return;

        FiresThisFight++;
        lock (_firesByPool) _firesByPool["Life"] = FiresThisFightFor("Life") + 1;
        if (_cfg.SoundOnFire) _chime.Play(_cfg.SoundGapMs);
        Log.Write($"Low life setup: '{_cfg.Life.Key}' x1 - {why} at {shieldFrac:P1}");
        Fired?.Invoke("Life", shieldFrac);
    }

    /// <summary>
    /// How long a covered HUD is trusted to still be a menu rather than a
    /// broken setup. Fifteen minutes comfortably outlasts a crafting or
    /// trading session; the old one minute did not.
    /// </summary>
    internal const long HudGiveUpMs = 900_000;

    /// <summary>
    /// Whether the numbers going quiet still reads as "a panel is covering
    /// the HUD" rather than "give up and show the overlay anyway." True only
    /// for a box that has read before (<paramref name="hudSeenMs"/> nonzero)
    /// and not so long ago that a menu stops being the likely explanation.
    /// </summary>
    internal static bool StillCoveredByMenu(long hudSeenMs, long nowMs, long giveUpMs)
        => hudSeenMs != 0 && nowMs - hudSeenMs <= giveUpMs;

    private long _hudSeenMs;
    private long _forgotMaxAtMs = long.MinValue / 2;

    public bool NumbersReading(string name) =>
        _ocr.TryGet(name, out var r) && _ocr.NowMs - r.AtMs < 4000;

    /// <summary>Whether life's address is vouched for; the OCR rate follows it.</summary>
    private bool _lifeMemConfirmed;

    private GlobeReading Sample(State st, WatcherConfig c, string name,
                                bool focused, Stopwatch clock, bool gameRunning)
    {
        // Switched off means not looked at. Capturing costs about 9 ms whatever
        // the region size, so reading a globe nobody asked about was spending
        // half the loop's budget to update a number that changes nothing.
        //
        // Shield is the one exception: Low Life setup fires life's flask off
        // shield's own reading without shield's own "Enabled" ever being on,
        // so shield still needs reading here even then. It is read through
        // this exact trusted pipeline either way - only which firing logic
        // gets to act on it differs, decided further down.
        bool trackedAnyway = name == "Shield" && _cfg.LowLifeEnabled;
        if (!c.Enabled && !trackedAnyway)
        {
            st.Below = 0;
            st.Reset();
            return new GlobeReading(name, 0, false, "off");
        }

        // Not "off", and nothing reset - a loading screen or the moment
        // between alt-tabbing back can drop the window match for a frame or
        // two, and the grace-period state that bridges a real gap (see
        // HadGoodSource below) must survive that exactly like it survives a
        // missed OCR frame. This only skips the expensive part: with no game
        // window, the configured region is capturing whatever happens to be
        // on screen instead - the desktop, a browser, nothing worth an OrbDetector
        // pass or an OCR lookup over.
        if (!gameRunning) return new GlobeReading(name, 0, false, "game not running");

        long now = clock.ElapsedMilliseconds;

        // The globe is the fallback, not a requirement. Without a region this
        // used to stop dead - "no region" - even with the numbers set up and
        // reading perfectly, which is a watcher switched off by the absence of
        // the worst of its three sources.
        // Numbers only means exactly that: the globe is not read, not used as a
        // fallback, and its calibration stops mattering.
        bool haveGlobe = c.Region.IsValid && !_cfg.NumbersOnly;
        double frac = 0;

        if (haveGlobe)
        {
            if (!st.Cap.Grab(c.Region.ToRect()))
                return new GlobeReading(name, 0, false, "capture failed");

            frac = OrbDetector.Fraction(st.Cap.Buffer, st.Cap.Width, st.Cap.Height, c);
        }

        else if (!c.UseText && !_cfg.UseMemory)
        {
            return new GlobeReading(name, 0, false,
                _cfg.NumbersOnly ? "numbers not set up" : "no region");
        }

        // Kept aside, because it is about to be overwritten by a better source
        // and it is still worth having. The globe cannot tell life from energy
        // shield and reads a poisoned globe as empty, so it is a poor judge of
        // how much life there is - but it is a perfectly good judge of which of
        // two exact sources has gone mad, and that is the job it is given
        // below. Nothing here lets it decide a reading on its own.
        double globeFrac = frac;
        bool globeUsable = haveGlobe;

        // The numbers beside the globe are exact. When there is a recent
        // reading, it decides; the pixels stay as the fallback for the gaps
        // between OCR passes and for anyone who has not set the text region.
        bool fromText = false;
        string textRaw = "";
        bool textConfigured = c.UseText && c.TextRegion.IsValid && _ocr.Available;

        // Kept so memory can be checked against it below. Two sources that
        // both claim to be exact and disagree cannot both be right, and the
        // one printed on your screen is the one that is.
        bool haveOcr = false;
        bool holdOnDisagreement = false;
        bool numbersTrusted = false;
        double ocrFrac = 0;
        int ocrMax = 0;
        bool textLostOverride = true;

        // Asking for memory or for the numbers is asking for the globe pixels
        // NOT to decide. They cannot tell life from energy shield, and they
        // read a poisoned globe - which turns green - as empty, which looks
        // like a killing blow. Falling back to them quietly is how a better
        // source turns into a worse one without saying so.
        bool betterWanted = _cfg.UseMemory || textConfigured || _cfg.NumbersOnly;
        long textAge = long.MaxValue;

        if (textConfigured && _ocr.TryGet(name, out var tr))
        {
            textAge = _ocr.NowMs - tr.AtMs;
            // The pixels are crude but they are never wildly wrong. A text
            // reading that disagrees with them by this much is a misread, not a
            // correction, so the pixels win and the disagreement is logged.
            if (name == "Life") ReadingAgeMs = textAge;

            // The pixels used to be able to veto this, on the theory that they
            // are crude but never wildly wrong. They can be: a globe covered by
            // a panel, or one whose colours no longer separate, reads a flat
            // 100% forever - and it then vetoed 557 correct readings in a
            // single session, including "617/1,490" on the way to a death. The
            // pixels are the fallback. They do not get to overrule the numbers.
            //
            // What the check was actually guarding against - a stray digit
            // turning 1,465 into 11,465 - is caught properly by the maximum,
            // which is read from the same line and has to match. Only when
            // there is no maximum to check against is a second opinion worth
            // anything, and only then are the pixels asked for one.
            bool pixelsMayObject = c.KnownMax <= 0 && haveGlobe;
            bool agrees = !pixelsMayObject || Math.Abs(tr.Fraction - frac) <= 0.40;

            if (textAge < 1200) ocrMax = tr.Max;

            // Counted per fresh read of the box, not per poll: the same frame
            // looked at sixty times is still one frame.
            if (textAge < 1200 && tr.AtMs != st.LastOcrAtMs)
            {
                st.OcrRepeats = tr.Current == st.LastOcrCur && tr.Max == st.LastOcrMax
                    ? st.OcrRepeats + 1 : 0;
                st.LastOcrAtMs = tr.AtMs;
                st.LastOcrCur = tr.Current;
                st.LastOcrMax = tr.Max;
            }
            // The widest of the floors this pool actually uses - whichever net
            // would have fired on this reading anyway had it been trusted.
            double dangerBelow = Math.Max(c.PanicBelow, Math.Max(c.UberBelow, c.LastDitchBelow));
            numbersTrusted = TrustNumbers(tr.Raw, c.TextLabel, tr.Max, c.KnownMax,
                                          st.OcrRepeats, tr.Fraction, dangerBelow);

            if (textAge < 1200 && agrees)
            {
                frac = tr.Fraction;
                fromText = true;
                haveOcr = true;
                ocrFrac = tr.Fraction;
                textRaw = $"numbers, {tr.Current:N0}/{tr.Max:N0}";
            }
            else if (textAge < 1200)
            {
                textRaw = $"{tr.Current:N0}/{tr.Max:N0} ignored, pixels say {frac:P0}";
                if (now - st.LastDisagreeMs > 5000)
                {
                    st.LastDisagreeMs = now;
                    Log.Write($"{name}: text says {tr.Fraction:P0} but pixels say {frac:P0}"
                              + $" - ignoring the text ({tr.Current}/{tr.Max})");
                }
            }
        }

        int expectedMax = ExpectedMax(name, c, fromText ? ParseMax(textRaw) : 0);

        // Memory is exact when it is pointed at the right thing, and worthless
        // when it is not. Thousands of pairs in a heap look like a health pool,
        // so it is only believed when it agrees with what we already know.
        if (_cfg.UseMemory && _mem.TryGet(out var ms) && _mem.NowMs - ms.AtMs < 500)
        {
            // Each pool reads its own vital. Anything that was not life used to
            // read mana, so energy shield - had anybody switched it on - would
            // have been watching the wrong pool entirely and firing a life
            // flask for it.
            int cur = name switch
            {
                "Life" => ms.CurHp,
                "Shield" => ms.CurEs,
                _ => ms.CurMp,
            };

            int max = name switch
            {
                "Life" => ms.MaxHp,
                "Shield" => ms.MaxEs,
                _ => ms.MaxMp,
            };

            double memFrac = name switch
            {
                "Life" => ms.LifeFraction,
                "Shield" => ms.ShieldFraction,
                _ => ms.ManaFraction,
            };

            // A current of zero beside an intact maximum is a dead pointer, not
            // a dead character - the game frees and rebuilds that structure on
            // a zone change, and the address then reads 0/1,190 forever. It is
            // also the one reading that can never be worth acting on: at zero
            // you are already dead and no flask changes that.
            if (cur <= 0 && max > 0)
            {
                if (st.ZeroSinceMs == 0) st.ZeroSinceMs = now;

                if (now - st.ZeroSinceMs > 1500)
                {
                    st.ZeroSinceMs = 0;

            // Frozen is as bad as wrong, and harder to see: a freed address
            // keeps its last values forever, and they stay plausible. Life that
            // has not moved at all in eight seconds, while something else is
            // reading it moving, is not being read.
            if (cur == st.LastMemCur)
            {
                if (st.MemSameSinceMs == 0) st.MemSameSinceMs = now;
                else if (now - st.MemSameSinceMs > 4000 && haveOcr
                         && Math.Abs(ocrFrac - memFrac) > 0.05)
                {
                    st.MemSameSinceMs = 0;
                    st.MemConfirmed = false;
                    if (name == "Life") _lifeMemConfirmed = false;
                    Log.Write($"{name}: memory has read {cur:N0} unchanged for eight "
                              + "seconds while the numbers moved - the address is frozen, "
                              + "avoiding it and searching again");
                    _mem.Distrust();
                }
            }
            else
            {
                st.LastMemCur = cur;
                st.MemSameSinceMs = 0;
            }
                    st.MemConfirmed = false;
                    if (name == "Life") _lifeMemConfirmed = false;
                    // Distrust, not a plain Rescan. Tonight the same corpse -
                    // "life 0/886, mana 325/366, shield 0/1136" - outscored the
                    // real character on a stale OCR hint and was picked again
                    // every time this fired: found, read as zero for a second,
                    // rescanned, found again, on a nine-second loop that never
                    // once protected anything. Distrust blacklists the address
                    // for five minutes, so the next search is forced onto a
                    // different candidate - hopefully the one that is not dead.
                    Log.Write($"{name}: memory has read 0 of {max:N0} for over a second - "
                              + "the address has gone stale, avoiding it and searching "
                              + "again");
                    _mem.Distrust();
                }

                textRaw = $"memory reads 0/{max:N0} - stale address, ignored";
                goto pastMemory;
            }

            st.ZeroSinceMs = 0;

            // Every lock is provisional until the numbers have vouched for it
            // once. The old check only ran while an OCR reading was fresh, and
            // between those moments a wrong address had free rein - twenty
            // presses went out at "16%" in the gaps, each one caught and thrown
            // out afterwards, which is no use to anyone already drinking.
            if (st.MemGeneration != _mem.Generation)
            {
                st.MemGeneration = _mem.Generation;
                st.MemConfirmed = false;
                st.MemDisagreeSinceMs = 0;
                st.MemMoved = false;
                if (name == "Life") _lifeMemMoved = false;
                st.MemFirstCur = -1;
                st.LastMemCur = -1;
                st.MemSameSinceMs = 0;
            }

            // A pool that never moves is not a pool.
            //
            // The search matches a candidate against life as it is at that
            // instant, and at full health every stray copy of the maximum
            // matches. Those copies are then perfect - they agree with the
            // maximum, they agree with the numbers, and they never change
            // again. Watching for the value to move at least once is the only
            // check that tells a real pool from a coincidence, because it is
            // the only thing a coincidence cannot do.
            if (st.MemFirstCur < 0) st.MemFirstCur = cur;
            else if (!st.MemMoved && cur != st.MemFirstCur)
            {
                st.MemMoved = true;
                if (name == "Life") _lifeMemMoved = true;
                Log.Write($"{name}: memory moved ({st.MemFirstCur:N0} -> {cur:N0}) - "
                          + "this address follows the game");
            }

            // Agreeing with the configured maximum is not enough on its own:
            // if that maximum was itself adopted from a misread, the search was
            // pointed at the wrong number to begin with and will happily find
            // something that matches it. The numbers printed on screen are the
            // only independent witness, so when they are readable they get the
            // final say.
            bool matchesOcr = !haveOcr || Math.Abs(memFrac - ocrFrac) <= 0.15;

            // An address is taken as good once its maximum is the one we asked
            // the search to find - which is also what the search matched on, and
            // what it re-checks every read.
            //
            // It used to need the numbers to agree with it first, and that
            // deadlocked: while the numbers are misreading, they never agree,
            // so the address stays unconfirmed, so the misreads keep winning.
            // That is precisely the state where memory is the only thing still
            // telling the truth, and it was the one state where it had no say.
            // Two independent sources agreeing on a maximum that is not the one
            // configured settles it outright. A stale maximum is not a small
            // problem: every reading is measured against it, the numbers refuse
            // anything that disagrees, and memory will not confirm an address
            // whose maximum is "wrong" - so the whole thing goes blind at
            // exactly the moment a level or a gear swap changed it. That is
            // what 1,490 becoming 1,503 did.
            if (max > 0 && ocrMax > 0 && max == ocrMax && expectedMax > 0
                && max != expectedMax)
            {
                Log.Write($"{name}: memory and the numbers both read a maximum of {max:N0}, "
                          + $"not {expectedMax:N0} - adopting it at once");
                int wasBoth = expectedMax;
                c.KnownMax = max;
                expectedMax = max;
                _cfg.Save();
                MaxAdopted?.Invoke(name, wasBoth, max);
            }

            // The numbers are the authority on the maximum, because they are
            // read from the screen and cannot be pointed at the wrong thing. A
            // memory maximum that disagrees with a maximum being read right now
            // is a wrong address, whatever its current value looks like - and
            // "reading HP 754/1217" beside a game saying 0/1,151 is exactly
            // that.
            // A confirmed address whose maximum has moved is a level, not a
            // wrong address - the structure does not move when you level. Take
            // the new value from it and carry on; nothing needs reading and
            // nothing needs finding again.
            if (st.MemConfirmed && max > 0 && expectedMax > 0 && max != expectedMax
                && ocrMax <= 0)
            {
                Log.Write($"{name}: maximum is now {max:N0} where it was {expectedMax:N0} - "
                          + "taken from the address already locked");
                int wasLevelled = expectedMax;
                c.KnownMax = max;
                expectedMax = max;
                _cfg.Save();
                MaxAdopted?.Invoke(name, wasLevelled, max);
            }

            var verdict = Judge(_mem.Structured, max, ocrMax);
            if (verdict != Verdict.Agree)
            {
                // Two maxima that disagree means one of these is not your pool,
                // and this is the branch that decides which - the one that
                // actually runs, rather than the one further down that only
                // sees a disagreement with the STORED maximum.
                //
                // It used to assume the screen was the witness and jump past
                // memory entirely, keeping the reading from the box. That was
                // right when memory was a guessed address. It is wrong now, and
                // what it produced is in the log: mana firing three times a
                // second at a box reading "19/100" for a pool holding 191,
                // with memory sitting there reading the pool correctly and
                // being thrown away every time.
                //
                // A structured lock is a component whose vitals point back at
                // it and whose three pools are self-consistent. A box is a
                // rectangle somebody dragged once. When they disagree about
                // something as basic as the size of the pool, the box is on the
                // wrong line.
                if (verdict == Verdict.TrustMemory)
                {
                    if (now - st.LastMemBadMs > 20000)
                    {
                        st.LastMemBadMs = now;
                        Log.Write($"{name}: the numbers read a maximum of {ocrMax:N0} and "
                                  + $"the pool holds {max:N0} - that box is on the wrong "
                                  + "line, so it is being ignored and looked for again");

                        // Heal the stored maximum too, or the search keeps
                        // hunting a number the box invented.
                        c.KnownMax = max;
                        RefindNow();
                    }

                    // Memory carries the pool; the box carries nothing until it
                    // has been found again.
                    haveOcr = false;
                    ocrFrac = 0;
                }
                else
                {
                    if (st.MemConfirmed)
                    {
                        st.MemConfirmed = false;
                        if (name == "Life") _lifeMemConfirmed = false;
                        Log.Write($"{name}: memory says the maximum is {max:N0} but the "
                                  + $"numbers say {ocrMax:N0} - wrong address, searching "
                                  + "again");
                        _mem.Rescan();
                    }

                    // Nothing structural to appeal to, so neither of them fires.
                    // A flask not thrown costs a charge; firing on the wrong
                    // pool costs the belt, and has three times now.
                    textRaw = $"memory max {max:N0}, numbers say {ocrMax:N0} - not acting "
                              + "on either until they agree";
                    holdOnDisagreement = true;
                    goto pastMemory;
                }
            }

            if (!st.MemConfirmed && max > 0 && expectedMax > 0 && max == expectedMax)
            {
                st.MemConfirmed = true;
                if (name == "Life") _lifeMemConfirmed = true;
                Log.Write($"{name}: reading straight from the game - it has your maximum "
                          + $"of {max:N0}. The numbers on screen stay on as a cross-check, "
                          + "and half a second of disagreement hands it back to them.");
            }

            // Once the numbers have vouched for an address, they stop being the
            // authority over it, because they are the less reliable of the two
            // and fail differently. Memory reads the variable itself and can
            // only be wrong by pointing somewhere wrong - which is what
            // confirmation rules out, once. The numbers are guessed from pixels
            // and drop or invent a digit now and then: "490/1,490" is 1,490
            // with the leading 1 lost, and it read as 33% and fired three
            // times while the pool was full.
            //
            // A wrong address disagrees forever; a misread disagrees for a
            // frame. So disagreement is timed rather than acted on, and only a
            // steady one costs the lock.
            // A wrong address disagrees forever and loses the lock; a misread
            // disagrees for a frame or two and is ridden out. This is the only
            // thing that can take an address away, so it is also what protects
            // against the search having found the wrong one.
            if (st.MemConfirmed && haveOcr && !matchesOcr)
            {
                if (st.MemDisagreeSinceMs == 0)
                {
                    st.MemDisagreeSinceMs = now;
                    st.DisOcrFirst = ocrFrac;
                    st.DisMemFirst = memFrac;
                    st.DisOcrLast = ocrFrac;
                    st.DisMemLast = memFrac;
                }
                else
                {
                    st.DisOcrLast = ocrFrac;
                    st.DisMemLast = memFrac;
                }

                // Six hundred milliseconds, not two seconds. A misread lasts a
                // frame or two; anything still disagreeing after half a second
                // is an address that has stopped following the game, and every
                // moment it keeps its lock is a moment the wrong number could
                // be the one acted on.
                // Not when memory has your character.
                //
                // This rule is from when memory was the liar: a loosely matched
                // address froze at 100% on a zone change while the numbers
                // watched life fall to 41%. A structured lock is not that - it
                // is a component whose vitals point back at it, re-checked on
                // every read, and it gives up on its own when the component
                // goes away.
                //
                // Tonight the rule ran the other way. Memory read a full 578 of
                // 578; the box was returning "5781578" and "981578" - the same
                // numbers mangled - and settled on 98/578. After half a second
                // the rule threw memory out, trusted the 17%, and fired the
                // emergency and last-ditch nets again and again at a full pool.
                //
                // So with a structured lock the numbers are the suspect: they
                // are looked for again, and memory keeps deciding.
                bool structured = _mem.Structured;
                if (structured && now - st.MemDisagreeSinceMs > 600
                    && now - st.LastBoxDoubtMs > 20000)
                {
                    st.LastBoxDoubtMs = now;
                    Log.Write($"{name}: the numbers read {ocrFrac:P0} while memory, which has "
                              + $"your character, reads {memFrac:P0} - believing memory and "
                              + "finding the numbers again");
                    RefindNow();
                }

                // Unless memory is the one that has stopped.
                //
                // "Memory wins" was right against a misreading box - the box
                // sits on a wrong number and memory moves with you. It was
                // lethal against a frozen copy: memory held 100% while the
                // numbers fell to 43% and then 0%, and every one of those was
                // overruled. The difference is movement. A frozen copy never
                // changes; a misread does not follow the fight. So when the
                // numbers are moving and memory is not, memory is the one
                // that is wrong, and it goes - and that copy is avoided, so
                // the next search does not simply pick it again.
                bool frozen = structured
                              && MemoryFrozen(st.DisOcrFirst, st.DisOcrLast,
                                              st.DisMemFirst, st.DisMemLast);
                if (frozen && now - st.MemDisagreeSinceMs > 600)
                {
                    st.MemConfirmed = false;
                    st.MemDisagreeSinceMs = 0;
                    if (name == "Life") _lifeMemConfirmed = false;
                    Log.Write($"{name}: memory stayed at {memFrac:P0} while the numbers moved "
                              + $"to {ocrFrac:P0} - memory is a frozen copy, dropping it");
                    _mem.Distrust();
                }
                else if (!structured && now - st.MemDisagreeSinceMs > 600)
                {
                    st.MemConfirmed = false;
                    st.MemDisagreeSinceMs = 0;
                    if (name == "Life") _lifeMemConfirmed = false;
                    Log.Write($"{name}: memory has read {memFrac:P0} against the numbers' "
                              + $"{ocrFrac:P0} for half a second - going back to the "
                              + "numbers and looking for your character again");
                    _mem.Rescan();
                }
                else
                {
                    // Two exact sources disagreeing about how much life there
                    // is: act on the lower one until it is settled.
                    //
                    // Believing memory through the disagreement was fatal. On a
                    // zone change the old address keeps reading a plausible,
                    // frozen 2,078 of 2,078 - the maximum still matches, so
                    // nothing else notices - and it reported 100% for two full
                    // seconds while the numbers said 41% and falling. Two
                    // seconds is a death.
                    //
                    // Whichever is right, the lower reading is the safe one to
                    // act on. Firing early costs a charge; firing late costs
                    // the character.
                    // Ask the globe which of them has gone mad.
                    //
                    // Taking the lower reading was right when memory was the
                    // liar - it froze at 100% while the numbers watched life
                    // fall to 41%, and two seconds of that is a death. But it
                    // is catastrophic when the numbers are the liar, and the
                    // log shows exactly that: OCR read "6" out of 1,496, memory
                    // correctly said 1,496, and the lower-reading rule spent
                    // twenty-two flask charges on a character at full life.
                    //
                    // Neither source can be believed over the other on
                    // principle, so a third one settles it. The globe is a poor
                    // judge of an exact number and an excellent judge of
                    // whether a pool is nearly empty, which is the only
                    // question being asked here.
                    double pick;
                    string why;
                    if (globeUsable)
                    {
                        bool memClose = Math.Abs(globeFrac - memFrac)
                                        <= Math.Abs(globeFrac - ocrFrac);
                        pick = memClose ? memFrac : ocrFrac;
                        why = $"the globe reads {globeFrac:P0}, closer to "
                              + (memClose ? "memory" : "the numbers");
                    }
                    else if (_mem.Structured)
                    {
                        // No globe to ask, and memory is not a guess any more -
                        // it is a component whose vitals point back at it and
                        // whose maxima match. The numbers are the source that
                        // misreads a digit, so they no longer get to overrule it
                        // unaided.
                        pick = memFrac;
                        why = "no globe to settle it, and memory has your character";
                    }
                    else
                    {
                        // Nothing better than caution: firing early costs a
                        // charge, firing late costs the character.
                        pick = Math.Min(memFrac, ocrFrac);
                        why = "nothing to settle it - taking the lower";
                    }

                    frac = pick;
                    textRaw = $"memory {memFrac:P0} vs numbers {ocrFrac:P0} - "
                              + $"{why}, acting on {frac:P0}";
                    fromText = true;
                    textLostOverride = false;
                    haveOcr = false;
                }
            }
            else if (matchesOcr)
            {
                st.MemDisagreeSinceMs = 0;
            }

            // Never used to decide anything until it has been seen to move.
            // Until then the numbers stay in charge, which is slower but is
            // reading something that certainly exists.
            // A structural match is trusted at once. The probation exists to
            // catch an address found by matching loose numbers, which cannot be
            // told from a coincidence until it moves; a component whose vitals
            // point back at it is not that.
            bool trusted = max > 0 && (expectedMax == 0 || max == expectedMax)
                           && matchesOcr && st.MemConfirmed
                           && (st.MemMoved || _mem.Structured);


            if (trusted)
            {
                frac = name switch
                {
                    "Life" => ms.LifeFraction,
                    "Shield" => ms.ShieldFraction,
                    _ => ms.ManaFraction,
                };
                fromText = true;
                textRaw = $"memory, {name.ToLowerInvariant()} {cur:N0}/{max:N0}";
                textLostOverride = false;
            }
            // Only when the maxima genuinely differ. This used to fire whenever
            // the address was merely unconfirmed, and logged "memory found max
            // 1490 but the max is 1490 - wrong structure", then threw the
            // address away and found the same one again ten seconds later,
            // forever. Memory was never once used.
            else if (Judge(_mem.Structured, max, expectedMax) != Verdict.Agree)
            {
                // Two maxima that disagree means one of these is not your pool.
                //
                // This branch used to shrug - log "ignored", keep the reading
                // from the numbers, and carry on firing on it. What that looked
                // like in practice was mana emptying its flask three times a
                // second, indefinitely, at a mana box reading "19/100" while
                // the pool it claimed to be watching held 187.
                //
                // A maximum is not a wobble. A box whose maximum is not your
                // maximum is on the wrong line, and every reading it produces
                // is about something else - so it does not get to fire.
                //
                // Which of the two is yours is not a toss-up either. A
                // structured memory reading is a component whose vitals point
                // back at it and whose three pools are self-consistent; the
                // box is a rectangle somebody once dragged, that OCR returned
                // "IVI" from a minute ago.
                if (Judge(_mem.Structured, max, expectedMax) == Verdict.TrustMemory)
                {
                    frac = name switch
                    {
                        "Life" => ms.LifeFraction,
                        "Shield" => ms.ShieldFraction,
                        _ => ms.ManaFraction,
                    };
                    fromText = true;
                    textLostOverride = false;
                    textRaw = $"memory, {name.ToLowerInvariant()} {cur:N0}/{max:N0} - the "
                              + $"numbers box says {expectedMax:N0} and is on the wrong line";

                    if (now - st.LastMemBadMs > 20000)
                    {
                        st.LastMemBadMs = now;
                        Log.Write($"{name}: the numbers box has a maximum of {expectedMax} "
                                  + $"and the pool holds {max} - it is on the wrong line, "
                                  + "so it is being ignored and looked for again");
                        c.KnownMax = max;
                        RefindNow();
                    }
                }
                else
                {
                    // Nothing structural to appeal to, so neither can be
                    // trusted to fire. Holding is the safe half of the choice:
                    // the cost is a flask not thrown, not a character.
                    textRaw = $"memory says max {max:N0}, the numbers say {expectedMax:N0}"
                              + " - not acting on either until they agree";
                    holdOnDisagreement = true;

                    if (now - st.LastMemBadMs > 10000)
                    {
                        st.LastMemBadMs = now;
                        Log.Write($"{name}: memory found max {max} but the box says "
                                  + $"{expectedMax} - one of them is the wrong line, "
                                  + "searching again");
                        _mem.Rescan();
                    }
                }
            }
        }

        pastMemory:

        // The numbers are only drawn on the gameplay screen. Losing them for
        // more than a moment means an inventory, the passive tree, a vendor or
        // the atlas is up - and those cover the globe, so the pixel fallback
        // would be reading the panel and firing at it.
        // "The numbers are not on screen" is a reason to hold fire only when the
        // numbers are what it is reading. With memory locked on, they are a
        // cross-check running every second or two - so their being stale, or
        // hidden behind a menu, says nothing about whether the pool is being
        // read. Holding fire for it meant refusing to act while memory sat
        // there reporting your life correctly, which is the worst failure this
        // can have.
        // The numbers fire only when they have earned it.
        //
        // One night's log: 180 presses, 99 of them from the numbers, and very
        // nearly all of those were misreads - 269/469 nineteen times, 98/578
        // thirteen, then 69/269, 65/265, 20/269, 8/518, 3/542. Clipped or
        // mangled, and at least a third fired while memory had the right
        // answer. Each was patched as it appeared and there was always
        // another, because the box can be wrong in more ways than can be
        // listed.
        //
        // So the question stopped being "is this a misread we know?" and
        // became "has this reading earned the right to press a key?" Memory
        // with a real lock has. A line from the box has only if it carries its
        // own label, its maximum is exactly the one already known, and it has
        // been read the same twice running. Anything else is shown, not acted
        // on.
        if (fromText && textRaw.StartsWith("numbers,", StringComparison.Ordinal)
            && !numbersTrusted)
        {
            holdOnDisagreement = true;
            textRaw += " - not trusted to fire";
        }

        bool exactGone = textConfigured && textAge > c.RequireTextMs && textLostOverride
                         && !st.MemConfirmed;

        // Rather than going blind, hand over to the globe.
        //
        // Holding fire here is the failure that kills people. Both exact
        // sources being unavailable at once is not rare - a menu covers the
        // numbers, a zone change costs the memory lock - and the answer used to
        // be to stop acting entirely and say so in a status bar nobody is
        // reading mid-fight.
        //
        // The globe is a worse source and it is still a source. It cannot tell
        // life from energy shield and it reads a loading screen as an empty
        // globe, so it only covers when it is reading something credible right
        // now: a globe that has read nothing for longer than the blind grace is
        // one that cannot be seen, and that stays a hold. A globe that was
        // reading a moment ago and is draining is a character in trouble.
        bool globeCovers = exactGone && globeUsable
                           && !Unreadable(globeFrac, now, st.LastGoodMs, c);

        if (globeCovers)
        {
            frac = globeFrac;
            fromText = false;
            textRaw = $"globe {globeFrac:P0} - neither memory nor the numbers are "
                      + "available";
            if (!st.GlobeCovering)
            {
                st.GlobeCovering = true;
                Log.Write($"{name}: nothing exact to read - the globe is covering at "
                          + $"{globeFrac:P0}");
            }
        }
        else if (st.GlobeCovering)
        {
            st.GlobeCovering = false;
            Log.Write($"{name}: exact reading is back");
        }

        // Two exact sources naming different maxima is not a reading at all.
        bool textLost = (exactGone && !globeCovers) || holdOnDisagreement;

        // Anything above the floor is a real reading, and the moment it happens
        // is what separates "nearly dead" from "cannot see it".
        if (frac > c.IgnoreBelow) st.LastGoodMs = now;

        // A watched globe stuck at nothing is the single most common broken
        // setup, and it looks identical to a globe that is simply full: no
        // firing, no complaint. Say it out loud.
        if (frac <= c.IgnoreBelow)
        {
            if (st.BlindSinceMs == 0) st.BlindSinceMs = now;
            // Long enough that being dead or on a loading screen does not
            // trip it, short enough to notice before a fight.
            else if (!st.Blind && now - st.BlindSinceMs > 8000)
            {
                st.Blind = true;
                Log.Write($"{name}: reading {frac:P1} for 8 seconds - "
                          + (_cfg.NumbersOnly
                              ? "nothing is being read - looking for the numbers again"
                              : "the region is not on the globe"));
                Blind?.Invoke(name, true);
            }
        }
        else if (st.BlindSinceMs != 0)
        {
            st.BlindSinceMs = 0;
            if (st.Blind) { st.Blind = false; Blind?.Invoke(name, false); }
        }

        double dropRate = st.DropPctPerSec(now, frac);
        st.Push(now, frac);

        // Whether pressing again is worth it: only while life is not already
        // turning around. Computed here, ahead of where it is used below, so
        // the held-and-bridged path can reuse the exact same "is this still
        // falling" answer as the ordinary nets - one rule, not two copies of
        // it that could drift apart.
        bool canRepeat = c.FastDropPctPerSec <= 0 || dropRate > -c.FastDropPctPerSec;

        // A press that worked shows up as the globe climbing. Anything else is
        // a press that went nowhere, and that is worth saying out loud.
        if (st.Verifying)
        {
            st.MaxSinceFire = Math.Max(st.MaxSinceFire, frac);

            // Every class regenerates, and life and spell leech both refill the
            // globe as well, so any rise at all proves nothing. Only a jump
            // large enough that regen could not have produced it inside the
            // window is worth calling a flask - and even that is a hint, not a
            // fact. Nothing here is allowed to gate firing.
            double rise = st.MaxSinceFire - st.FracAtFire;
            if (rise >= 0.05)
            {
                st.Verifying = false;
                st.NoEffect = 0;
                st.BackoffUntilMs = 0;
                EffectChecked?.Invoke(name, true, 0);
            }
            else if (now - st.FireMs > c.VerifyWindowMs)
            {
                st.Verifying = false;
                if (rise <= 0.005)
                {
                    st.NoEffect++;
                    if (st.NoEffect >= Math.Max(1, c.NoEffectBefore))
                    {
                        st.BackoffUntilMs = now + c.NoEffectBackoffMs;
                        Log.Write($"{name}: {st.NoEffect} presses changed nothing - "
                                  + $"pausing {c.NoEffectBackoffMs} ms rather than spending "
                                  + "more charges on nothing");
                    }
                    Log.Write($"{name}: globe did not move after the press "
                              + $"({st.NoEffect} in a row) - no charges, wrong key, or "
                              + "input not reaching the game");
                }
                else
                {
                    Log.Write($"{name}: globe rose {rise:P1} after the press - too little to "
                              + "tell a flask from regen or leech");
                }
                EffectChecked?.Invoke(name, false, rise <= 0.005 ? st.NoEffect : 0);
            }
        }

        // Reading runs whenever the globe is on, so the UI stays live even
        // while disarmed; only firing is gated below.
        if (!Armed || !focused)
        {
            st.Below = 0;

            // Disarmed is the safe way to check a setup: the trigger point can
            // be confirmed by ear without a single key being sent. Only while
            // disarmed, not merely unfocused, so alt-tabbing at low health does
            // not chirp at you.
            // Reading flat zero means the box is not on the globe at all, so
            // there is nothing to announce. Chirping about an unreadable globe
            // while the firing path silently refuses to act on it made the
            // sound look like proof that keys were being sent.
            if (!Armed && c.Enabled && frac < c.Threshold && !textLost
                && !Unreadable(frac, now, st.LastGoodMs, c))
            {
                if (_cfg.SoundOnFire && _cfg.SoundWhenDisarmed) _chime.Play(_cfg.SoundGapMs);
                if (now - st.LastWouldFireMs > _cfg.SoundGapMs)
                {
                    st.LastWouldFireMs = now;
                    WouldFire?.Invoke(name, frac);
                }
            }
            return new GlobeReading(name, frac, true, "", fromText, textRaw);
        }

        // Nothing goes into a globe we cannot see. The grace period is what
        // makes this safe: a globe that read 60% a second ago and reads 1% now
        // is nearly dead and gets its flask, while one that has read nothing
        // for over a second is a loading screen and gets silence.
        // fromText covers memory as well: it means something exact decided.
        if (fromText)
        {
            st.NoGoodSourceSinceMs = 0;
            st.HadGoodSource = true;
            st.LastGoodFrac = frac;
        }
        else if (betterWanted)
        {
            if (st.NoGoodSourceSinceMs == 0) st.NoGoodSourceSinceMs = now;

            // The grace exists so one missed frame does not block a heal. It
            // was letting the globe pixels decide during it, which is how a
            // loading screen got to fire: the numbers are gone, the pixels read
            // whatever is on screen, and two seconds is long enough to act on
            // it. Carry the last exact reading through the gap instead. Stale
            // and real beats fresh and wrong.
            if (st.HadGoodSource)
            {
                frac = st.LastGoodFrac;
                textRaw = $"last known {frac:P0}";
            }
        }

        // The grace period is for a source that was working and dropped out for
        // a moment - a heal should not wait on one missed frame. A source that
        // has never once produced a reading is not having a hiccup, it is not
        // set up, and there is nothing to be patient about: the pixels must not
        // stand in for it even briefly. A globe turning green is a drop from
        // full to nothing in one frame, and two seconds is long enough to spend
        // every charge on it.
        bool sourceLost = betterWanted && !fromText
                          && (!st.HadGoodSource
                              || now - st.NoGoodSourceSinceMs > c.ActOnStaleMs);

        bool blind = Unreadable(frac, now, st.LastGoodMs, c);

        // Every poll, into memory only. This is what makes "it fired and it
        // should not have" answerable: the press itself says almost nothing,
        // and by the time it is logged the run-up has gone.
        _trail.Note($"{name} {frac:P1} from {(fromText ? textRaw : "globe pixels")} "
                    + $"age {(textAge == long.MaxValue ? -1 : textAge)}ms  "
                    + $"armed={Armed} focused={focused}  "
                    + $"refuse={(textLost ? "numbers gone" : sourceLost ? "no exact reading" : blind ? "globe unreadable" : "no")}  "
                    + $"fire<{c.Threshold:P0} panic<{c.PanicBelow:P0} emergency<{c.UberBelow:P0}  "
                    + $"below={st.Below} sinceFire={now - st.LastFireMs}ms "
                    + $"backoff={Math.Max(0, st.BackoffUntilMs - now)}ms uberUsed={st.UberUsed}");

        // Zero is never worth acting on. Either you are dead, in which case a
        // flask is beside the point, or something has broken - a freed address,
        // a misread, a globe behind a loading screen - in which case pressing
        // is worse than doing nothing. Both his machine and mine have spent an
        // evening firing at "0".
        bool zero = frac <= 0.0005;

        if (sourceLost || textLost || blind || zero)
        {
            st.Below = 0;
            string why = textLost ? (holdOnDisagreement ? "numbers not trusted" : "numbers not on screen")
                       : sourceLost ? "no exact reading yet"
                       : zero ? "reads zero - dead, or the reading has broken"
                       : "cannot read the globe";

            // The nets still get a chance here - from a reading that itself
            // passed the trust gate a moment ago, never from the current,
            // untrusted one. A flat refusal made "the numbers glitched for a
            // frame" and "nothing was protecting you" the same event, which
            // is backwards for the two paths that exist specifically for
            // near-death. Zero is excluded: a reading that has gone to zero
            // is exactly the case a stale bridge must not paper over.
            if (!zero && frac > 0
                && SafetyNetMayBridge(st.LastTrustedAtMs, now, SafetyNetBridgeMs))
            {
                double heldFrac = frac;
                frac = st.LastTrustedFrac;
                if (frac > 0)
                {
                    if (Net(c.UberBelow, ref st.UberUsed, int.MaxValue, canRepeat,
                            "EMERGENCY (bridged through a hold)"))
                        return new GlobeReading(name, frac, true, "", fromText, textRaw);
                    if (Net(c.LastDitchBelow, ref st.LastDitchUsed, 3, canRepeat,
                            "LAST DITCH (bridged through a hold)"))
                        return new GlobeReading(name, frac, true, "", fromText, textRaw);
                }
                frac = heldFrac;
            }

            return new GlobeReading(name, frac, true, why, fromText, textRaw);
        }

        st.LastTrustedFrac = frac;
        st.LastTrustedAtMs = now;

        // Low Life setup reads shield through every trust check above -
        // bridging, memory cross-check, all of it - but fires the life flask
        // through its own three-tier check elsewhere, not through shield's
        // own threshold, panic or emergency nets below. The two checkboxes
        // are mutually exclusive in the UI for exactly this reason: shield's
        // own firing logic and Low Life setup's must never both be live at
        // once, or the same drop in shield could press twice from two
        // separate systems.
        if (name == "Shield" && _cfg.LowLifeEnabled)
            return new GlobeReading(name, frac, true, "", fromText, textRaw);

        // The safety net: one press, once, when you fall past the floor.
        //
        // It sits below the readability guard on purpose. It used to sit above
        // it, which meant it was the one path that could fire on a reading the
        // rest of the code had already decided not to trust - so opening the
        // atlas, where the numbers are gone and the globe pixels read low,
        // pressed a flask. A net that fires when nothing is falling is not a
        // net.
        //
        // Everything else here can be waiting - a cooldown running, a burst
        // still going out, a confirming frame not yet counted - and that is
        // where a heal gets missed. This does not care what is waiting and does
        // not repeat: it fires a single press and then stays quiet until you
        // have climbed back out, so it is a net rather than a second trigger
        // spending charges alongside the first.
        // A net does not fire on a single frame either.
        //
        // The confirming look was added for panic presses and the nets were
        // left out of it, which is backwards: they are the paths that fire
        // instantly, from one reading, with no cooldown to slow them. A crop
        // clipping its own left edge turns "211/211" into "1/211" - a
        // perfectly well-formed pair, nothing about it looks wrong, and it
        // reads as half a percent of mana. Both nets went out on it.
        //
        // Nothing falls from full to nothing between two frames sixteen
        // milliseconds apart. If it says you did, look once more before
        // spending the charge; a misread does not survive into the next frame
        // and a real emergency still does.
        bool impossible = st.RecentHigh - frac > 0.4;
        if (impossible && st.NetHeldAt != frac)
        {
            st.NetHeldAt = frac;
            Log.Write($"{name}: {frac:P1} arrived straight from {st.RecentHigh:P0} - "
                      + "looking again before spending a charge on it");
            return new GlobeReading(name, frac, true, "", fromText, textRaw);
        }

        st.NetHeldAt = -1;

        if (frac > 0)
        {
            // Emergency: no cap. Below this floor and not recovering, keep
            // pressing - bounded only by having flasks left and by turning
            // the corner, never by an artificial gap.
            if (Net(c.UberBelow, ref st.UberUsed, int.MaxValue, canRepeat, "EMERGENCY"))
                return new GlobeReading(name, frac, true, "", fromText, textRaw);

            // Last ditch: up to three. The floor below emergency is the one
            // place a single miss is likeliest to be fatal, so it gets more
            // than one attempt too - but a number, not "as many as it takes",
            // because by the third failed press something other than a
            // missing charge is almost certainly wrong.
            if (Net(c.LastDitchBelow, ref st.LastDitchUsed, 3, canRepeat, "LAST DITCH"))
                return new GlobeReading(name, frac, true, "", fromText, textRaw);
        }

        if (frac >= c.Threshold)
        {
            st.Below = 0;
            return new GlobeReading(name, frac, true, "", fromText, textRaw);
        }

        bool Net(double floor, ref int used, int maxUses, bool canRepeat, string what)
        {
            if (floor <= 0) return false;

            // Recovered past the floor by a margin: the episode is over,
            // whether it took one press or several.
            if (frac > floor + 0.05) used = 0;
            if (frac > floor || _keys.Busy) return false;
            if (!NetMayFire(used, maxUses, canRepeat)) return false;

            if (!_keys.Send(c.Key, c.HoldMs, 1, 40, PostingKeys, GameWindow, PadFor(c),
                        _cfg.UseController && _cfg.AlsoPressKey)) return false;

            used++;
            st.LastFireMs = now;
            st.Below = 0;
            FiresThisFight++;
        lock (_firesByPool) _firesByPool[name] = FiresThisFightFor(name) + 1;
            if (_cfg.SoundOnFire) _chime.Play(_cfg.SoundGapMs);
            string tail = used > 1
                ? $"repeat press #{used}, still under the {floor:P0} floor"
                : "one press, as a last resort";
            Log.Write($"{name}: '{c.Key}' x1 at {frac:P1} {what} - {tail}");
            _trail.Dump($"{name} {what.ToLowerInvariant()} press"
                        + (used > 1 ? $" #{used}" : "") + $" at {frac:P1}, under the "
                        + $"{floor:P0} floor, reading from "
                        + $"{(fromText ? textRaw : "globe pixels")}");
            Fired?.Invoke(name, frac);
            return true;
        }

        // Deep in the red, or dropping fast enough that waiting a full cooldown
        // means dying with charges unspent: press again as soon as the game
        // will accept it, and do not wait for a second confirming frame.
        bool panic = frac < c.PanicBelow || dropRate >= c.FastDropPctPerSec;
        int gap = panic ? c.PanicCooldownMs : c.CooldownMs;
        int confirm = panic ? 1 : Math.Max(1, c.ConfirmFrames);

        // A fall this steep has to be seen twice.
        //
        // Panic exists so that real burst damage is not made to wait for a
        // confirming frame, and that is right - but it also meant a single
        // misread frame could fire on its own, and one did. The numbers read
        // 293 of 298 for a solid second, returned 98 once, and a flask went
        // out on that one sample: 293 misread as 98 is a leading digit lost,
        // which is the commonest way for these to fail.
        //
        // Waiting for the next reading costs about sixteen milliseconds at
        // sixty polls a second. Nothing dies in sixteen milliseconds, and a
        // misread never survives into the following frame.
        if (panic && st.RecentHigh > 0 && frac < st.RecentHigh - 0.4)
            confirm = 2;

        // Presses are doing nothing: no charges, or they are not arriving.
        // Either way, more of them will not help, so wait instead of emptying
        // what is left into a flask that cannot use it.
        if (now < st.BackoffUntilMs)
            return new GlobeReading(name, frac, true, "no effect - waiting", fromText, textRaw);

        st.Below++;
        if (st.Below < confirm || now - st.LastFireMs < gap)
            return new GlobeReading(name, frac, true, "", fromText, textRaw);

        // A burst still going out means the previous request has not even
        // finished leaving; asking for another only builds a backlog.
        if (_keys.Busy)
            return new GlobeReading(name, frac, true, "", fromText, textRaw);

        int shots = Math.Clamp(c.BurstCount, 1, 5);
        if (!_keys.Send(c.Key, c.HoldMs, shots, Math.Clamp(c.BurstGapMs, 5, 500),
                        PostingKeys, GameWindow, PadFor(c),
                        _cfg.UseController && _cfg.AlsoPressKey))
            return new GlobeReading(name, frac, true, "", fromText, textRaw);

        st.LastFireMs = now;
        st.Below = 0;
        FiresThisFight++;
        lock (_firesByPool) _firesByPool[name] = FiresThisFightFor(name) + 1;

        // A check already running is left to finish.
        //
        // Panic presses every 260 ms and the effect is judged over 900, so each
        // press reset the window before it could ever elapse. The verdict was
        // therefore never reached, "presses changed nothing" never counted, and
        // the pause that exists precisely to stop a belt being emptied into a
        // pool that is not moving could never engage. That is how mana came to
        // fire three times a second, indefinitely, at a box reading someone
        // else's numbers.
        if (c.VerifyEffect && !st.Verifying)
        {
            st.Verifying = true;
            st.FireMs = now;
            st.FracAtFire = frac;
            st.MaxSinceFire = frac;
        }
        // The globe has not refilled yet, so old samples would read as a
        // continuing crash and inflate the drop rate.
        st.Reset();
        if (_cfg.SoundOnFire) _chime.Play(_cfg.SoundGapMs);

        Log.Write($"{name}: '{c.Key}' x{shots} at {frac:P1}"
                  + (panic ? $" PANIC (drop {dropRate:0}%/s)" : ""));
        _trail.Dump($"{name} x{shots} at {frac:P1}, under the {c.Threshold:P0} trigger"
                    + (panic ? $", panicking (dropping {dropRate:0}%/s)" : "")
                    + $", reading from {(fromText ? textRaw : "globe pixels")}");
        Fired?.Invoke(name, frac);

        return new GlobeReading(name, frac, true, "", fromText, textRaw);
    }

    /// <summary>What a watcher should act on, and whether it should act at all.</summary>
    internal readonly record struct SourceChoice(double Frac, bool Hold, bool Exact);

    /// <summary>
    /// Decides which reading a watcher uses when an exact source is wanted.
    ///
    /// The globe pixels must never stand in for memory or the numbers. They
    /// cannot tell life from energy shield, they read a recoloured globe as
    /// empty, and on a loading screen they read the loading screen. The grace
    /// period exists so one missed frame does not block a heal - so it carries
    /// the last exact reading through the gap rather than handing the decision
    /// to the pixels for two seconds, which is long enough to act on nonsense.
    /// </summary>
    internal static SourceChoice ChooseSource(bool betterWanted, bool exactNow,
                                              double exactFrac, double pixelFrac,
                                              bool hadExact, double lastExactFrac,
                                              long sinceMs, long nowMs, int graceMs)
    {
        if (!betterWanted) return new SourceChoice(pixelFrac, false, false);
        if (exactNow) return new SourceChoice(exactFrac, false, true);

        // Never had one: not a hiccup, not set up. Nothing to carry, and the
        // pixels do not get to fill in.
        if (!hadExact) return new SourceChoice(pixelFrac, true, false);

        // The carried value is shown either way, but it is only acted on
        // briefly. A missed frame is one interval; a loading screen is seconds,
        // and a value frozen from before the load kept firing right through it.
        bool tooOld = nowMs - sinceMs > graceMs;
        return new SourceChoice(lastExactFrac, tooOld, false);
    }

    /// <summary>
    /// Is this a globe we cannot see, rather than one that is nearly empty?
    ///
    /// Both mistakes are bad and they look identical in a single frame. Refuse
    /// too eagerly and it will not fire at 1% life, the moment it matters most.
    /// Refuse too late and it fires into every loading screen. What separates
    /// them is history: a globe that read normally a moment ago and reads
    /// nothing now has just crashed; one that has read nothing for over a
    /// second is not being seen.
    /// </summary>
    internal static bool Unreadable(double frac, long nowMs, long lastGoodMs, WatcherConfig c)
        => frac <= c.IgnoreBelow && nowMs - lastGoodMs > c.BlindGraceMs;

    /// <summary>
    /// The maximum for a pool: what you typed, else what the numbers on screen
    /// last said, else nothing.
    /// </summary>
    private int ExpectedMax(string name, WatcherConfig c, int fromRaw)
    {
        if (c.KnownMax > 0) return c.KnownMax;
        if (fromRaw > 0) return fromRaw;
        if (c.UseText && c.TextRegion.IsValid && _ocr.TryGet(name, out var t)) return t.Max;
        return 0;
    }

    private static int ParseMax(string raw)
    {
        int slash = raw.IndexOf('/');
        if (slash < 0) return 0;
        int n = 0;
        foreach (char ch in raw.AsSpan(slash + 1))
        {
            if (char.IsAsciiDigit(ch)) n = n * 10 + (ch - '0');
            else if (ch != ',' && ch != '.') break;
            if (n > 1_000_000) return 0;
        }
        return n;
    }

    private bool WindowFocused()
    {
        string title = Native.ForegroundTitle();
        ForegroundTitle = title;

        string match = _cfg.WindowMatch?.Trim() ?? string.Empty;
        if (match.Length == 0) return true;
        return title.Contains(match, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Plays the ding once, ignoring the gap, so it can be auditioned.</summary>
    public void TestSound() => _chime.Play(0);

    public void Dispose()
    {
        Stop();
        _keys.Dispose();
        _chime.Dispose();
        _ocr.Dispose();
        _mem.Dispose();
    }
}
