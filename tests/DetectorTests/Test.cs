using P02;

static class T
{
    const int W = 700, H = 460, Bpp = 4;
    static byte[] _buf = new byte[W * H * Bpp];

    static void Px(int x, int y, int b, int g, int r)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        int i = (y * W + x) * Bpp;
        _buf[i] = (byte)b; _buf[i + 1] = (byte)g; _buf[i + 2] = (byte)r; _buf[i + 3] = 255;
    }

    static void Disc(int cx, int cy, int rad, int b, int g, int r)
    {
        for (int y = cy - rad; y <= cy + rad; y++)
            for (int x = cx - rad; x <= cx + rad; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad) Px(x, y, b, g, r);
    }

    static void Rect(int x0, int y0, int w, int h, int b, int g, int r)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) Px(x, y, b, g, r);
    }

    static void Reset()
    {
        for (int i = 0; i < _buf.Length; i += Bpp)
        { _buf[i] = 40; _buf[i + 1] = 38; _buf[i + 2] = 36; _buf[i + 3] = 255; }
    }

    /// <summary>
    /// Everything past the globe/OCR tests near the top of this file reports
    /// PASS/FAIL by printing a line, not by touching a counter - so a test
    /// could fail right there in the console and "ALL PASS" still printed
    /// underneath it, because nothing read the line back. Found while adding
    /// the hud-hiding tests below: they printed two FAILs and the run still
    /// claimed a clean pass. Mirroring stdout here and counting FAIL lines in
    /// it at the end is the actual source of truth now; the scattered `fails`
    /// counter below is left alone rather than torn out of forty call sites,
    /// but no longer decides anything.
    /// </summary>
    sealed class TeeWriter(TextWriter real, StringWriter mirror) : TextWriter
    {
        public override System.Text.Encoding Encoding => real.Encoding;
        public override void Write(char value) { real.Write(value); mirror.Write(value); }
        public override void Write(string? value) { real.Write(value); mirror.Write(value); }
        public override void WriteLine(string? value) { real.WriteLine(value); mirror.WriteLine(value); }
    }

    static void Main()
    {
        var realOut = Console.Out;
        var mirror = new StringWriter();
        Console.SetOut(new TeeWriter(realOut, mirror));

        int fails = 0;

        // The real bottom-right corner: mana globe, plus blue skill gems and a
        // blue flask to its left. Projections merged all of this; blobs must not.
        Reset();
        Disc(520, 300, 110, 190, 90, 40);              // mana globe
        Rect(60, 330, 44, 44, 200, 120, 60);           // gem icon
        Rect(112, 330, 44, 44, 200, 120, 60);          // gem icon
        Rect(164, 330, 44, 44, 200, 120, 60);          // gem icon
        Rect(30, 250, 26, 70, 210, 110, 50);           // flask
        var got = OrbDetector.Locate(_buf, W, H, blue: true);
        fails += Check("mana globe beside gems+flask", got, 520, 300, 110);

        // Specular highlight punching a hole in the middle of the globe.
        Reset();
        Disc(520, 300, 110, 190, 90, 40);
        Rect(470, 230, 60, 18, 250, 250, 250);
        got = OrbDetector.Locate(_buf, W, H, blue: true);
        fails += Check("globe with glare streak", got, 520, 300, 110);

        // Life corner, red.
        Reset();
        Disc(150, 300, 100, 40, 40, 180);
        Rect(300, 300, 120, 20, 50, 50, 190);          // a red bar, not round
        got = OrbDetector.Locate(_buf, W, H, blue: false);
        fails += Check("life globe beside a red bar", got, 150, 300, 100);

        // Nothing but icons: must refuse rather than box the icons.
        Reset();
        Rect(60, 330, 44, 44, 200, 120, 60);
        Rect(112, 330, 44, 44, 200, 120, 60);
        got = OrbDetector.Locate(_buf, W, H, blue: true);
        if (got is null) Console.WriteLine("PASS  icons only -> refused");
        else { Console.WriteLine($"FAIL  icons only -> returned {got}"); fails++; }

        // The mana globe's centre is a pale washed-out blue: blue leads green
        // by a wide absolute margin but barely at all proportionally, which is
        // what defeated the old ratio test.
        Reset();
        Disc(520, 300, 110, 150, 70, 35);
        Disc(520, 300, 60, 210, 165, 130);
        got = OrbDetector.Locate(_buf, W, H, blue: true);
        fails += Check("globe with pale washed centre", got, 520, 300, 110);

        // Fill fraction across levels, on a real disc.
        var cfg = new WatcherConfig { Hue = "blue", ColourMargin = 30, MinValue = 50 };
        foreach (double want in new[] { 1.0, 0.75, 0.5, 0.25 })
        {
            Reset();
            int cx = 520, cy = 300, rad = 110;
            int top = (int)(cy - rad + (1 - want) * rad * 2);
            for (int y = top; y <= cy + rad; y++)
                for (int x = cx - rad; x <= cx + rad; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad)
                        Px(x, y, 190, 90, 40);

            var box = new byte[(rad * 2 + 1) * (rad * 2 + 1) * Bpp];
            int bw = rad * 2 + 1;
            for (int y = 0; y < bw; y++)
                for (int x = 0; x < bw; x++)
                {
                    int src = ((cy - rad + y) * W + (cx - rad + x)) * Bpp;
                    int dst = (y * bw + x) * Bpp;
                    Array.Copy(_buf, src, box, dst, Bpp);
                }
            double f = OrbDetector.Fraction(box, bw, bw, cfg);
            bool ok = Math.Abs(f - want) < 0.04;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  fill {want:P0} -> read {f:P1}");
            if (!ok) fails++;
        }

        // A box drawn by hand catches frame above the globe. Uncalibrated that
        // reads low; calibration must pull a full globe back to a true 100%.
        {
            const int pad = 24, bw2 = 220, bh2 = 270;
            var box = new byte[bw2 * bh2 * Bpp];
            for (int i = 0; i < box.Length; i += Bpp)
            { box[i] = 40; box[i + 1] = 38; box[i + 2] = 36; box[i + 3] = 255; }

            int rad = 105, cx = bw2 / 2, cy = pad + rad;
            for (int yy = 0; yy < bh2; yy++)
                for (int xx = 0; xx < bw2; xx++)
                    if ((xx - cx) * (xx - cx) + (yy - cy) * (yy - cy) <= rad * rad)
                    {
                        int i = (yy * bw2 + xx) * Bpp;
                        box[i] = 190; box[i + 1] = 90; box[i + 2] = 40;
                    }

            var c2 = new WatcherConfig { Hue = "blue", ColourMargin = 30, MinValue = 50 };
            double before = OrbDetector.Fraction(box, bw2, bh2, c2);
            bool okCal = OrbDetector.CalibrateFull(box, bw2, bh2, c2, out int fr, out int er);
            c2.FullRow = fr;
            c2.EmptyRow = er;
            double after = OrbDetector.Fraction(box, bw2, bh2, c2);

            bool low = before < 0.95;
            bool fixedUp = okCal && after > 0.99;
            Console.WriteLine((low ? "PASS" : "FAIL") + $"  padded box reads low uncalibrated -> {before:P1}");
            Console.WriteLine((fixedUp ? "PASS" : "FAIL") + $"  calibration restores full -> {after:P1} (rows {fr}..{er})");
            if (!low) fails++;
            if (!fixedUp) fails++;
        }

        // A globe is not one flat colour: it is saturated in the middle and
        // falls off towards the rim. With a single strict threshold only the
        // core passes, and that core is itself a disc that passes every shape
        // check - so auto-find returned a box a fraction of the real size.
        Reset();
        {
            int cx = 520, cy = 300, rad = 120;
            for (int y = cy - rad; y <= cy + rad; y++)
                for (int x = cx - rad; x <= cx + rad; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d > rad) continue;
                    // Full blue in the middle, fading towards the rim.
                    double k = 1.0 - 0.65 * (d / rad);
                    Px(x, y, (int)(200 * k), (int)(95 * k), (int)(45 * k));
                }

            var strict = OrbDetector.Locate(_buf, W, H, blue: true, margin: 48);
            var swept = OrbDetector.LocateBest(_buf, W, H, blue: true);

            int strictW = strict?.Width ?? 0;
            int sweptW = swept?.Width ?? 0;
            bool strictTooSmall = strictW < sweptW;
            bool sweptRight = swept is not null && Math.Abs(sweptW - rad * 2) <= 12;

            Console.WriteLine((strictTooSmall ? "PASS" : "FAIL")
                + $"  one strict threshold under-reads the globe -> {strictW}px "
                + $"vs {sweptW}px swept, of {rad * 2}px actual");
            Console.WriteLine((sweptRight ? "PASS" : "FAIL")
                + $"  sweeping thresholds finds the whole globe -> "
                + $"{swept?.Width ?? 0}px of {rad * 2}px");
            if (!strictTooSmall) fails++;
            if (!sweptRight) fails++;
        }

        // Refusing to act on a globe we cannot see must not also refuse to act
        // on one that is nearly empty. Both look like "almost zero" in a single
        // frame; only the history tells them apart.
        {
            var wc = new WatcherConfig { IgnoreBelow = 0.02, BlindGraceMs = 1200 };
            int bad = 0;

            void Case(string what, double frac, long now, long lastGood, bool wantRefuse)
            {
                bool refused = MonitorEngine.Unreadable(frac, now, lastGood, wc);
                bool ok = refused == wantRefuse;
                Console.WriteLine((ok ? "PASS" : "FAIL") + $"  {what} -> "
                    + (refused ? "refuse" : "fire") + $" (wanted {(wantRefuse ? "refuse" : "fire")})");
                if (!ok) bad++;
            }

            // Dying: read 60% a moment ago, 1% now. Must still fire.
            Case("1% life, healthy 200ms ago", 0.01, 10_000, 9_800, false);
            Case("0% life, healthy 900ms ago", 0.00, 10_000, 9_100, false);

            // Loading or death screen: nothing readable for a while.
            Case("0% for 1.3s", 0.00, 10_000, 8_700, true);
            Case("0% for 30s", 0.00, 40_000, 10_000, true);
            Case("0% since launch", 0.00, 5_000, long.MinValue / 2, true);

            // Ordinary low life is never refused.
            Case("15% life", 0.15, 10_000, 10_000, false);
            Case("3% life, just crossed", 0.03, 10_000, 10_000, false);

            fails += bad;
        }

        // What the OCR box actually returns, including the exact misread that
        // reported a maximum of 14,652,005 from a life of 1,465 and a shield of
        // 2,005 - and read as 0% life while the character was at full.
        {
            int bad = 0;
            void Parse(string what, string text, int wantCur, int wantMax)
            {
                bool ok = TextOcr.TryParse(text, out int cur, out int max);
                bool right = wantMax == 0 ? !ok : ok && cur == wantCur && max == wantMax;
                Console.WriteLine((right ? "PASS" : "FAIL") + $"  {what} -> "
                    + (ok ? $"{cur}/{max}" : "rejected")
                    + (wantMax == 0 ? "  (wanted rejected)" : $"  (wanted {wantCur}/{wantMax})"));
                if (!right) bad++;
            }

            // Current above maximum is legitimate here - skills push life past
            // the pool - and above full never fires either way.
            Parse("overhealed above maximum", "2,029/1,465\n2,005/2,005", 2029, 1465);
            Parse("good first line, shield below", "1,465/1,465\n2,005/2,005", 1465, 1465);
            Parse("life over shield, one line", "1,465/1,465 2,005/2,005", 1465, 1465);
            Parse("plain", "1,465/1,465", 1465, 1465);
            // Was written down as "465/1,465 is fine". It is not fine - it is a
            // 1,465 that came apart, and reading its second half as your
            // current life turns a full pool into 31% and fires. That is the
            // misread that emptied a flask belt, so the expectation is the
            // thing that changed.
            Parse("half a number is not a reading", "1 465/1,465", 0, 0);
            Parse("dot for comma", "412/1.465", 412, 1465);
            Parse("mana", "747/747", 747, 747);

            // Rejections that matter.
            // A leading digit that was not there. Overhealing is real, but not
            // to 234% - and the damage is that it clamps to a comfortable 100%
            // while the pool it claims to describe may be nearly empty.
            Parse("phantom leading digit, clamps to full", "1747/747", 0, 0);
            Parse("absurd maximum", "2,029/14,652,005", 0, 0);
            Parse("no pair at all", "Life Shield Ward", 0, 0);

            fails += bad;
        }

        // The real HUD block, all three lines in one box. Ward is a pair too,
        // and reading 90/90 as life is a misfire waiting to happen.
        {
            string hud = "Life 1,465/1,465\nShield 2,005/2,005\nWard 90/90";
            string jumbled = "Ward 90/90\nLife 1,465/1,465\nShield 2,005/2,005";
            int bad = 0;

            void Pick(string what, string text, string label, int expected,
                      int wantCur, int wantMax)
            {
                bool ok = TextOcr.TryParse(text, out int cur, out int max, label, expected);
                bool right = ok && cur == wantCur && max == wantMax;
                Console.WriteLine((right ? "PASS" : "FAIL") + $"  {what} -> "
                    + (ok ? $"{cur}/{max}" : "rejected") + $"  (wanted {wantCur}/{wantMax})");
                if (!right) bad++;
            }

            Pick("label picks life out of three lines", hud, "Life", 0, 1465, 1465);
            Pick("label works whatever the order", jumbled, "Life", 0, 1465, 1465);
            Pick("label picks shield when asked", hud, "Shield", 0, 2005, 2005);
            Pick("known maximum picks life with no label", jumbled, "", 1465, 1465, 1465);

            // Without either anchor it takes the first line, which is exactly
            // the ward misread that prompted all this.
            bool anyOk = TextOcr.TryParse(jumbled, out int c0, out int m0);
            Console.WriteLine((anyOk && c0 == 90 && m0 == 90 ? "PASS" : "FAIL")
                + $"  no anchor takes the first line -> {c0}/{m0} (this is why anchors exist)");
            if (!(anyOk && c0 == 90 && m0 == 90)) bad++;

            fails += bad;
        }

        // With a label configured but absent from what was read, nothing is
        // reported. Falling back to position is what put ward on screen as
        // life, and a refused reading is better than the wrong one.
        {
            int bad = 0;
            string noLabel = "1,465/1,465\n2,005/2,005\n90/90";

            bool gotNoLabel = TextOcr.TryParse(noLabel, out int c, out int m, "Life", 0);
            Console.WriteLine((!gotNoLabel ? "PASS" : "FAIL")
                + "  label configured but missing -> "
                + (gotNoLabel ? $"{c}/{m}" : "refused") + "  (wanted refused)");
            if (gotNoLabel) bad++;

            // The maximum still rescues it when the label is not readable.
            bool gotByMax = TextOcr.TryParse(noLabel, out int c2, out int m2, "Life", 1465);
            Console.WriteLine((gotByMax && c2 == 1465 && m2 == 1465 ? "PASS" : "FAIL")
                + "  missing label, known maximum -> "
                + (gotByMax ? $"{c2}/{m2}" : "refused") + "  (wanted 1465/1465)");
            if (!(gotByMax && c2 == 1465 && m2 == 1465)) bad++;

            fails += bad;
        }

        // A stray leading digit turns 1,465 into 11,465. Current 1,465 against
        // that reads as 13%, which is under any trigger - so it fires at full
        // health. The label finds the right line; only the maximum catches it.
        {
            int bad = 0;
            string strayDigit = "Life 1,465/11,465\nShield 2,005/2,005";

            bool taken = TextOcr.TryParse(strayDigit, out int c, out int m, "Life", 1465);
            Console.WriteLine((!taken ? "PASS" : "FAIL")
                + "  stray digit in the maximum -> "
                + (taken ? $"{c}/{m} = {100.0 * c / m:0}%" : "refused") + "  (wanted refused)");
            if (taken) bad++;

            // The same line is fine once the maximum agrees.
            bool ok = TextOcr.TryParse("Life 1,465/1,465", out int c2, out int m2, "Life", 1465);
            Console.WriteLine((ok && c2 == 1465 && m2 == 1465 ? "PASS" : "FAIL")
                + "  correct maximum still accepted -> "
                + (ok ? $"{c2}/{m2}" : "refused"));
            if (!(ok && c2 == 1465 && m2 == 1465)) bad++;

            // With no maximum stated there is nothing to check it against, so
            // it parses - the repeat rule in the reader is what guards that.
            bool loose = TextOcr.TryParse(strayDigit, out _, out int m3, "Life", 0);
            Console.WriteLine((loose && m3 == 11465 ? "PASS" : "FAIL")
                + $"  no stated maximum, nothing to compare -> {m3}");
            if (!(loose && m3 == 11465)) bad++;

            fails += bad;
        }

        // The globe pixels must never decide once memory or the numbers are
        // asked for. This has come back three times in different disguises -
        // most recently as a ding on every loading screen, where the numbers
        // vanish and the pixels read the loading screen.
        {
            int bad = 0;
            const int grace = 400;

            void Case(string what, bool better, bool exactNow, bool hadExact,
                      long since, long now, double wantFrac, bool wantHold)
            {
                var c = MonitorEngine.ChooseSource(better, exactNow,
                                                   exactFrac: 0.20, pixelFrac: 0.00,
                                                   hadExact: hadExact, lastExactFrac: 0.95,
                                                   sinceMs: since, nowMs: now, graceMs: grace);
                bool ok = Math.Abs(c.Frac - wantFrac) < 0.001 && c.Hold == wantHold;
                Console.WriteLine((ok ? "PASS" : "FAIL") + $"  {what} -> "
                    + $"{c.Frac:P0}{(c.Hold ? ", held" : "")}"
                    + $"  (wanted {wantFrac:P0}{(wantHold ? ", held" : "")})");
                if (!ok) bad++;
            }

            // Nothing better asked for: the pixels are all there is.
            Case("pixels only", false, false, false, 0, 10_000, 0.00, false);

            // An exact reading is available and wins.
            Case("numbers reading", true, true, true, 0, 10_000, 0.20, false);

            // Numbers gone for a moment: carry the last exact value, do NOT
            // fall to the pixels, which read 0% on a loading screen.
            Case("gone 200ms, carries last", true, false, true, 9_800, 10_000, 0.95, false);

            // A loading screen is seconds, not one missed frame. Acting on a
            // value frozen from before the load is what fired through them.
            Case("gone 500ms, holds", true, false, true, 9_500, 10_000, 0.95, true);
            Case("gone 3s, holds", true, false, true, 7_000, 10_000, 0.95, true);

            // Never worked: holds immediately, no grace at all.
            Case("never read, holds now", true, false, false, 10_000, 10_000, 0.00, true);

            fails += bad;
        }

        // Every key the bind box can capture must be sendable. Numpad keys
        // share scancodes with the navigation cluster and differ only by the
        // extended flag, so this guards a genuinely easy mistake.
        {
            int bad = 0, mapped = 0;
            foreach (Keys k in Enum.GetValues<Keys>())
            {
                string? name = KeySender.FromKeys(k);
                if (name is null) continue;
                mapped++;
                if (!KeySender.IsKnown(name))
                {
                    Console.WriteLine($"FAIL  {k} -> '{name}' has no scancode");
                    bad++;
                }
            }
            foreach (string want in new[] { "1", "2", "3", "4", "5", "0", "-", "=" })
                if (!KeySender.IsKnown(want))
                {
                    Console.WriteLine($"FAIL  '{want}' missing from scancode map");
                    bad++;
                }

            // Numpad is deliberately unsupported: it must be capturable by
            // nothing and sendable as nothing, or it can creep back in.
            foreach (Keys k in new[] { Keys.NumPad0, Keys.NumPad1, Keys.Add, Keys.Divide })
                if (KeySender.FromKeys(k) is not null)
                {
                    Console.WriteLine($"FAIL  {k} is still capturable");
                    bad++;
                }
            if (KeySender.IsKnown("numpad0"))
            {
                Console.WriteLine("FAIL  numpad still in the scancode map");
                bad++;
            }
            Console.WriteLine((bad == 0 ? "PASS" : "FAIL")
                              + $"  keybind map: {mapped} keys capturable, all sendable");
            fails += bad;
        }

        // A phantom digit in the CURRENT, where the maximum is still right, so
        // nothing else catches it - and it clamps to a comfortable 100% however
        // little life is really left. Seen mid-fight as "14,610/1,490".
        {
            bool bad = TextOcr.TryParse("Life 14,610/1,490", out _, out _, "Life", 1490);
            bool overheal = TextOcr.TryParse("Life 1,947/1,490", out int oc, out int om, "Life", 1490);
            Console.WriteLine((!bad ? "PASS" : "FAIL")
                              + "  current with a phantom digit is refused");
            Console.WriteLine((overheal && oc == 1947 && om == 1490 ? "PASS" : "FAIL")
                              + $"  overheal is still accepted -> {oc}/{om}");
        }

        // Levelling. The stored maximum is always a moment behind the screen,
        // and refusing every reading until somebody notices is how it came to
        // "lose it" once per level.
        {
            bool levelled = TextOcr.TryParse("Life 288/288", out int lc, out int lm,
                                             "Life", 278);
            bool phantom = TextOcr.TryParse("Life 1,465/11,465", out _, out _,
                                            "Life", 1465);
            Console.WriteLine((levelled && lc == 288 && lm == 288 ? "PASS" : "FAIL")
                              + $"  a levelled maximum is accepted -> {lc}/{lm}");
            Console.WriteLine((!phantom ? "PASS" : "FAIL")
                              + "  a maximum with a phantom digit is still refused");
        }

        // Mana emptying its flask three times a second at a box reading
        // "19/100" for a pool holding 191. The box was on some other line
        // entirely, and the reading it produced was about something else.
        {
            var onWrongLine = MonitorEngine.Judge(structured: true, memoryMax: 191, boxMax: 100);
            var noLock = MonitorEngine.Judge(structured: false, memoryMax: 191, boxMax: 100);
            var fine = MonitorEngine.Judge(structured: true, memoryMax: 191, boxMax: 191);
            var nothingToSay = MonitorEngine.Judge(structured: true, memoryMax: 0, boxMax: 100);

            Console.WriteLine((onWrongLine == MonitorEngine.Verdict.TrustMemory ? "PASS" : "FAIL")
                              + $"  a box reading 100 for a pool of 191 loses -> {onWrongLine}");
            Console.WriteLine((noLock == MonitorEngine.Verdict.HoldBoth ? "PASS" : "FAIL")
                              + $"  with no structural lock, neither fires -> {noLock}");
            Console.WriteLine((fine == MonitorEngine.Verdict.Agree ? "PASS" : "FAIL")
                              + $"  agreement is left alone -> {fine}");
            Console.WriteLine((nothingToSay == MonitorEngine.Verdict.Agree ? "PASS" : "FAIL")
                              + $"  nothing to compare is not a disagreement -> {nothingToSay}");

            // The other direction, an hour later: the box read "Life 362/362"
            // with its own label matched and memory was the stale one at 346.
            // Calling that box wrong left the numbers dark and the overlay
            // hidden behind them.
            var levelled = MonitorEngine.Judge(structured: true, memoryMax: 346, boxMax: 362);
            Console.WriteLine((levelled == MonitorEngine.Verdict.Agree ? "PASS" : "FAIL")
                              + $"  346 against 362 is one pool, four percent apart -> {levelled}");
        }

        // OCR puts specks and spaces inside words. "Life" came back as
        // "I i-fe", which failed every label test and so threw away the
        // numbers beside it - which is how a stuck maximum could never correct
        // itself.
        {
            bool speckled = TextOcr.HasLabel("I i-fe 362/362", "Life");
            bool truncated = TextOcr.HasLabel("Li 362/362", "Life");
            bool notMana = TextOcr.HasLabel("Mana 207/207", "Life");
            Console.WriteLine((speckled ? "PASS" : "FAIL")
                              + "  \"I i-fe\" is still the word Life");
            Console.WriteLine((truncated ? "PASS" : "FAIL")
                              + "  a truncated \"Li\" is still Life");
            Console.WriteLine((!notMana ? "PASS" : "FAIL")
                              + "  the mana line is not mistaken for Life");
        }

        // The slash went missing: "5771577" is 577/577, and "677677" with no
        // separator at all. Only recoverable with the maximum already known.
        {
            bool a = TextOcr.TryParse("5771577", out int ac, out int am, "Life", 577);
            bool b = TextOcr.TryParse("4321577", out int bc, out int bm, "Life", 577);
            bool c = TextOcr.TryParse("5771577", out _, out _, "Life", 0);
            Console.WriteLine((a && ac == 577 && am == 577 ? "PASS" : "FAIL")
                              + $"  a slash read as 1 is recovered -> {ac}/{am}");
            Console.WriteLine((b && bc == 432 && bm == 577 ? "PASS" : "FAIL")
                              + $"  and at part life -> {bc}/{bm}");
            Console.WriteLine((!c ? "PASS" : "FAIL")
                              + "  with no known maximum a run of digits is still refused");
        }

        // Mana flip-flopping 269 <-> 469: the 2 read as a 4, inside the 2x
        // allowed for levelling, reading a full pool as 57% and firing.
        {
            bool misread = TextOcr.TryParse("Mana 269/469", out _, out _, "Mana", 269);
            bool level = TextOcr.TryParse("Mana 278/278", out int lc, out _, "Mana", 269);
            Console.WriteLine((!misread ? "PASS" : "FAIL")
                              + "  one digit off by hundreds is a misread, not a level");
            Console.WriteLine((level && lc == 278 ? "PASS" : "FAIL")
                              + "  a real level-up is still accepted");
        }

        // 269 then 69: the leading digit clipped off, reading a full pool as 25%.
        {
            Console.WriteLine((TextOcr.LooksClipped(269, 69) ? "PASS" : "FAIL")
                              + "  69 after 269 is a clipped number");
            Console.WriteLine((!TextOcr.LooksClipped(269, 120) ? "PASS" : "FAIL")
                              + "  120 after 269 is real damage");
            Console.WriteLine((!TextOcr.LooksClipped(269, 260) ? "PASS" : "FAIL")
                              + "  a small change is never a clip");
        }

        // Whether the emergency and last-ditch nets may press again while
        // still below their floor: unbounded for emergency, capped at three
        // for last-ditch, and neither repeats once life is climbing fast.
        {
            bool firstEver = MonitorEngine.NetMayFire(0, int.MaxValue, canRepeat: false);
            bool emergencyAgain = MonitorEngine.NetMayFire(1, int.MaxValue, canRepeat: true);
            bool emergencyRecovering = MonitorEngine.NetMayFire(1, int.MaxValue, canRepeat: false);
            bool lastDitchSecond = MonitorEngine.NetMayFire(1, 3, canRepeat: true);
            bool lastDitchExhausted = MonitorEngine.NetMayFire(3, 3, canRepeat: true);

            Console.WriteLine((firstEver ? "PASS" : "FAIL")
                              + "  net: the first press below a floor is always allowed");
            Console.WriteLine((emergencyAgain ? "PASS" : "FAIL")
                              + "  net: emergency presses again while still falling, no cap");
            Console.WriteLine((!emergencyRecovering ? "PASS" : "FAIL")
                              + "  net: emergency stops once life is climbing fast");
            Console.WriteLine((lastDitchSecond ? "PASS" : "FAIL")
                              + "  net: last-ditch may press a second time");
            Console.WriteLine((!lastDitchExhausted ? "PASS" : "FAIL")
                              + "  net: last-ditch stops after three, even if still falling");

            // "Rising rapidly" reuses the pool's own fast-drop number, negated:
            // falling, or rising slower than that, still allows a repeat.
            bool stillFalling = MonitorEngine.NetMayFire(1, int.MaxValue,
                canRepeat: 20.0 > -30.0);
            bool risingSlowly = MonitorEngine.NetMayFire(1, int.MaxValue,
                canRepeat: -10.0 > -30.0);
            bool risingFast = MonitorEngine.NetMayFire(1, int.MaxValue,
                canRepeat: -40.0 > -30.0);
            Console.WriteLine((stillFalling ? "PASS" : "FAIL")
                              + "  net: still falling at 20%/s -> may repeat");
            Console.WriteLine((risingSlowly ? "PASS" : "FAIL")
                              + "  net: rising slowly at 10%/s (below the 30%/s floor) -> may repeat");
            Console.WriteLine((!risingFast ? "PASS" : "FAIL")
                              + "  net: rising fast at 40%/s (past the 30%/s floor) -> holds");
        }

        // Whether a reading from a moment ago - one that itself passed the
        // trust gate - may still stand in for the emergency/last-ditch nets
        // while the current poll is held (numbers untrusted, memory
        // momentarily gone, and so on). Refusing outright made a one-frame
        // glitch and "nothing is protecting you" the same event.
        {
            const long bridge = MonitorEngine.SafetyNetBridgeMs;
            bool neverTrusted = MonitorEngine.SafetyNetMayBridge(long.MinValue / 2, 10_000, bridge);
            bool justWentUntrusted = MonitorEngine.SafetyNetMayBridge(9_900, 10_000, bridge);
            bool rightAtTheEdge = MonitorEngine.SafetyNetMayBridge(10_000 - bridge, 10_000, bridge);
            bool pastTheEdge = MonitorEngine.SafetyNetMayBridge(10_000 - bridge - 1, 10_000, bridge);

            Console.WriteLine((!neverTrusted ? "PASS" : "FAIL")
                              + "  bridge: never trusted (sentinel timestamp) never bridges");
            Console.WriteLine((justWentUntrusted ? "PASS" : "FAIL")
                              + "  bridge: trusted 100ms ago still bridges");
            Console.WriteLine((rightAtTheEdge ? "PASS" : "FAIL")
                              + $"  bridge: exactly {bridge}ms ago still bridges");
            Console.WriteLine((!pastTheEdge ? "PASS" : "FAIL")
                              + $"  bridge: {bridge + 1}ms ago no longer bridges");
        }

        // Whether the overlay's "is the HUD visible" check looks at whatever
        // is actually being watched, rather than always asking about Life.
        // Disabling "Watch my life" while mana stayed on used to leave the
        // overlay convinced a menu was covering the HUD forever, because the
        // check only ever asked about Life's own numbers.
        {
            WatcherConfig On() => new() { Enabled = true, UseText = true,
                                          TextRegion = Box.From(new Rectangle(0, 0, 40, 20)) };
            WatcherConfig Off() => new() { Enabled = false };

            bool lifeOnly = MonitorEngine.AnyPoolWatchedWithText(On(), Off(), Off());
            bool manaOnlyLifeDisabled = MonitorEngine.AnyPoolWatchedWithText(Off(), On(), Off());
            bool shieldOnly = MonitorEngine.AnyPoolWatchedWithText(Off(), Off(), On());
            bool noneWatched = MonitorEngine.AnyPoolWatchedWithText(Off(), Off(), Off());
            bool enabledButNoRegion = MonitorEngine.AnyPoolWatchedWithText(
                new WatcherConfig { Enabled = true, UseText = true }, Off(), Off());

            // Low Life setup reads shield without shield's own Enabled ever
            // being true - see Sample()'s entry gate - so this needs its own
            // way to count shield as watched, or the overlay goes right back
            // to thinking a menu covers a HUD it is reading just fine.
            WatcherConfig ShieldTextOnly() => new() { Enabled = false, UseText = true,
                TextRegion = Box.From(new Rectangle(0, 0, 40, 20)) };
            bool shieldViaLowLife = MonitorEngine.AnyPoolWatchedWithText(
                Off(), Off(), ShieldTextOnly(), lowLifeEnabled: true);
            bool shieldTextOnlyWithoutLowLife = MonitorEngine.AnyPoolWatchedWithText(
                Off(), Off(), ShieldTextOnly(), lowLifeEnabled: false);

            Console.WriteLine((lifeOnly ? "PASS" : "FAIL")
                              + "  hud-watch: life alone watched with text -> counts");
            Console.WriteLine((manaOnlyLifeDisabled ? "PASS" : "FAIL")
                              + "  hud-watch: life disabled, mana watched with text -> still counts");
            Console.WriteLine((shieldOnly ? "PASS" : "FAIL")
                              + "  hud-watch: shield alone watched with text -> counts");
            Console.WriteLine((!noneWatched ? "PASS" : "FAIL")
                              + "  hud-watch: nothing watched -> does not count");
            Console.WriteLine((!enabledButNoRegion ? "PASS" : "FAIL")
                              + "  hud-watch: enabled but no region drawn yet -> does not count");
            Console.WriteLine((shieldViaLowLife ? "PASS" : "FAIL")
                              + "  hud-watch: shield.Enabled off but Low Life setup on -> still counts");
            Console.WriteLine((!shieldTextOnlyWithoutLowLife ? "PASS" : "FAIL")
                              + "  hud-watch: same shield config without Low Life setup -> does not count");
        }

        // Whether a single missed window lookup should actually pause the OCR
        // reader, or just be a blink the debounce absorbs. Pausing on one
        // poll's miss cost up to a second of frozen reading the instant it
        // landed mid-fight; only a window genuinely gone for a while should.
        {
            const long grace = MonitorEngine.GameGoneGraceMs;
            bool foundNow = MonitorEngine.StillCountsAsRunning(
                true, long.MinValue / 2, 10_000, grace);
            bool justMissed = MonitorEngine.StillCountsAsRunning(
                false, 9_900, 10_000, grace);
            bool rightAtTheEdge = MonitorEngine.StillCountsAsRunning(
                false, 10_000 - grace, 10_000, grace);
            bool pastTheEdge = MonitorEngine.StillCountsAsRunning(
                false, 10_000 - grace - 1, 10_000, grace);
            bool neverSeen = MonitorEngine.StillCountsAsRunning(
                false, long.MinValue / 2, 10_000, grace);

            Console.WriteLine((foundNow ? "PASS" : "FAIL")
                              + "  game-gone: found this poll -> counts as running");
            Console.WriteLine((justMissed ? "PASS" : "FAIL")
                              + "  game-gone: missed 100ms ago -> still counts as running");
            Console.WriteLine((rightAtTheEdge ? "PASS" : "FAIL")
                              + $"  game-gone: gone exactly {grace}ms -> still counts as running");
            Console.WriteLine((!pastTheEdge ? "PASS" : "FAIL")
                              + $"  game-gone: gone {grace + 1}ms -> no longer counts as running");
            Console.WriteLine((!neverSeen ? "PASS" : "FAIL")
                              + "  game-gone: never seen at all (sentinel) -> not running");
        }

        // Low Life setup: three floors on shield, each firing once per
        // crossing and rearming only once shield has climbed back out with
        // margin - the same shape as the app's other emergency nets.
        {
            bool crossedFloor = MonitorEngine.LowLifeTierMayFire(false, 0.12, 0.15);
            bool alreadyFired = MonitorEngine.LowLifeTierMayFire(true, 0.12, 0.15);
            bool notCrossedYet = MonitorEngine.LowLifeTierMayFire(false, 0.20, 0.15);
            bool exactlyOnFloor = MonitorEngine.LowLifeTierMayFire(false, 0.15, 0.15);

            Console.WriteLine((crossedFloor ? "PASS" : "FAIL")
                              + "  low-life tier: below the floor, never fired -> may fire");
            Console.WriteLine((!alreadyFired ? "PASS" : "FAIL")
                              + "  low-life tier: below the floor but already fired -> holds");
            Console.WriteLine((!notCrossedYet ? "PASS" : "FAIL")
                              + "  low-life tier: still above the floor -> does not fire");
            Console.WriteLine((exactlyOnFloor ? "PASS" : "FAIL")
                              + "  low-life tier: exactly on the floor -> may fire");

            const double margin = MonitorEngine.LowLifeRearmMargin;
            bool notRearmedYet = MonitorEngine.LowLifeTierRearmed(0.15 + margin, 0.15);
            bool rearmedPastMargin = MonitorEngine.LowLifeTierRearmed(0.15 + margin + 0.001, 0.15);
            bool stillBelowFloor = MonitorEngine.LowLifeTierRearmed(0.10, 0.15);

            Console.WriteLine((!notRearmedYet ? "PASS" : "FAIL")
                              + "  low-life rearm: exactly on the margin -> not rearmed yet");
            Console.WriteLine((rearmedPastMargin ? "PASS" : "FAIL")
                              + "  low-life rearm: just past the margin -> rearmed");
            Console.WriteLine((!stillBelowFloor ? "PASS" : "FAIL")
                              + "  low-life rearm: still below the floor -> not rearmed");

            const long confirm = MonitorEngine.LowLifeZeroConfirmMs;
            bool neverBelowZero = MonitorEngine.LowLifeZeroConfirmed(long.MinValue / 2, 10_000, confirm);
            bool justArrived = MonitorEngine.LowLifeZeroConfirmed(10_000, 10_000, confirm);
            bool confirmedNow = MonitorEngine.LowLifeZeroConfirmed(10_000 - confirm, 10_000, confirm);

            Console.WriteLine((!neverBelowZero ? "PASS" : "FAIL")
                              + "  low-life zero-confirm: never seen at zero -> not confirmed");
            Console.WriteLine((!justArrived ? "PASS" : "FAIL")
                              + "  low-life zero-confirm: arrived this instant -> not confirmed yet "
                              + "(one frame is exactly what a misread looks like)");
            Console.WriteLine((confirmedNow ? "PASS" : "FAIL")
                              + $"  low-life zero-confirm: held for the full {confirm}ms -> confirmed");
        }

        // Whether the overlay should still treat a quiet numbers box as a menu
        // covering the HUD rather than give up and show anyway. A box that has
        // never read at all never counts as covered (nothing to be patient
        // about); one that read recently stays covered for fifteen minutes,
        // not the old one minute that popped the overlay back up over a
        // crafting bench or the passive tree.
        {
            bool neverRead = MonitorEngine.StillCoveredByMenu(0, 5_000, MonitorEngine.HudGiveUpMs);
            bool justWentQuiet = MonitorEngine.StillCoveredByMenu(1_000, 2_000, MonitorEngine.HudGiveUpMs);
            bool fiveMinutesInAMenu = MonitorEngine.StillCoveredByMenu(
                1, 1 + 5 * 60_000, MonitorEngine.HudGiveUpMs);
            bool justUnderTheLimit = MonitorEngine.StillCoveredByMenu(
                1, 1 + MonitorEngine.HudGiveUpMs, MonitorEngine.HudGiveUpMs);
            bool pastTheLimit = MonitorEngine.StillCoveredByMenu(
                1, 1 + MonitorEngine.HudGiveUpMs + 1, MonitorEngine.HudGiveUpMs);

            Console.WriteLine((!neverRead ? "PASS" : "FAIL")
                              + "  hud: a box that has never read is never 'covered'");
            Console.WriteLine((justWentQuiet ? "PASS" : "FAIL")
                              + "  hud: quiet for a second still reads as covered");
            Console.WriteLine((fiveMinutesInAMenu ? "PASS" : "FAIL")
                              + "  hud: five minutes in a menu still reads as covered");
            Console.WriteLine((justUnderTheLimit ? "PASS" : "FAIL")
                              + "  hud: right at the give-up point still reads as covered");
            Console.WriteLine((!pastTheLimit ? "PASS" : "FAIL")
                              + "  hud: one past the give-up point gives up");
        }

        // The gate every numbers reading has to pass before it may fire,
        // tested against the night's actual misreads - and against the night
        // it went too far the other way. "Life 41/874" arrived once,
        // labelled, the right maximum, at 4.7% - and sat untrusted for over a
        // second because OCR did not look again in time. A dangerously low
        // reading now clears on the first look; an ordinary one still has to
        // repeat.
        {
            bool clean = MonitorEngine.TrustNumbers("Life 578/578", "Life", 578, 578, 1, 1.0, 0.35);
            bool mangled = MonitorEngine.TrustNumbers("981578", "Life", 578, 578, 3, 0.17, 0.35);
            bool digit = MonitorEngine.TrustNumbers("Mana 269/469", "Mana", 469, 269, 5, 0.573, 0.30);
            bool onceSafe = MonitorEngine.TrustNumbers("Life 500/578", "Life", 578, 578, 0, 0.865, 0.35);
            bool onceDanger = MonitorEngine.TrustNumbers("Life 41/874", "Life", 874, 874, 0, 41.0 / 874, 0.35);
            Console.WriteLine((clean ? "PASS" : "FAIL") + "  gate: a clean labelled line seen twice may fire");
            Console.WriteLine((!mangled ? "PASS" : "FAIL") + "  gate: 981578 has no label and may not, however low");
            Console.WriteLine((!digit ? "PASS" : "FAIL") + "  gate: 269/469 names the wrong maximum and may not");
            Console.WriteLine((!onceSafe ? "PASS" : "FAIL") + "  gate: an ordinary reading seen once still waits");
            Console.WriteLine((onceDanger ? "PASS" : "FAIL")
                              + "  gate: Life 41/874 at 4.7%, seen once, fires - the near-death case");
        }

        // Which HUD the game is showing, from painted pixels at the places the
        // real screenshots put them: coloured face buttons for a pad, a red
        // and a blue flask by the globe for a keyboard, and neither for a menu.
        {
            const int W = 3440, H = 1440;

            ModeDetector.Pixel Paint(params (double x, double y, int r, int g, int b)[] spots)
                => (x, y) =>
                {
                    foreach (var s in spots)
                        if (Math.Abs(x - s.x) <= 10 && Math.Abs(y - s.y) <= 10)
                            return (s.r, s.g, s.b);
                    return (8, 8, 8);
                };

            double faceY = H - 0.031 * H, flaskY = H - 0.060 * H;
            var pad = Paint((W / 2.0 + 0.0285 * H, faceY, 20, 160, 40),
                            (W / 2.0 + 0.0715 * H, faceY, 30, 60, 200),
                            (W / 2.0 + 0.1146 * H, faceY, 200, 180, 30),
                            (W / 2.0 + 0.1590 * H, faceY, 200, 30, 30));
            var keys = Paint((0.241 * H, flaskY, 102, 24, 20),
                             (0.287 * H, flaskY, 5, 20, 56));
            var menu = Paint();

            var a = ModeDetector.Detect(W, H, pad);
            var b = ModeDetector.Detect(W, H, keys);
            var c = ModeDetector.Detect(W, H, menu);
            Console.WriteLine((a == ModeDetector.Mode.Controller ? "PASS" : "FAIL")
                              + $"  mode: coloured face buttons are the controller HUD -> {a}");
            Console.WriteLine((b == ModeDetector.Mode.Keyboard ? "PASS" : "FAIL")
                              + $"  mode: dimmed red and blue flasks are the keyboard HUD -> {b}");
            Console.WriteLine((c == ModeDetector.Mode.Unknown ? "PASS" : "FAIL")
                              + $"  mode: a menu showing neither changes nothing -> {c}");
        }

        // The back buttons, from the exact block in the player's Steam layout
        // for Path of Exile 2: L4 sends D-pad left, R4 D-pad right, and the
        // upper pair is not bound - which must stay unbound, not borrow the
        // binding of the button after it. Written with ' for " so the layout
        // text needs no escaping.
        {
            string vdf = (
                "'button_back_left' { 'activators' { 'Full_Press' { 'bindings' { "
                + "'binding' 'xinput_button DPAD_LEFT, , ' } } } 'disabled_activators' { } } "
                + "'button_back_left_upper' { 'activators' { } 'disabled_activators' { } } "
                + "'button_back_right' { 'activators' { 'Full_Press' { 'bindings' { "
                + "'binding' 'xinput_button DPAD_RIGHT, , ' } } } 'disabled_activators' { } }"
            ).Replace('\'', '"');
            var map = SteamLayout.Parse(vdf);
            string? l4 = map.TryGetValue("button_back_left", out var a) ? SteamLayout.ToPad(a) : null;
            string? r4 = map.TryGetValue("button_back_right", out var b) ? SteamLayout.ToPad(b) : null;
            bool l5Unbound = !map.ContainsKey("button_back_left_upper");
            Console.WriteLine((l4 == "Left" ? "PASS" : "FAIL") + $"  steam: L4 is bound to D-pad left -> {l4}");
            Console.WriteLine((r4 == "Right" ? "PASS" : "FAIL") + $"  steam: R4 is bound to D-pad right -> {r4}");
            Console.WriteLine((l5Unbound ? "PASS" : "FAIL") + "  steam: an unbound L5 does not borrow R4's binding");
            Console.WriteLine((SteamLayout.ToPad("key_press KEY_1, , ") is null ? "PASS" : "FAIL")
                              + "  steam: a keyboard binding is not a pad button");
        }

        // Whether a memory lock is still the character it locked onto, judged
        // by its own maxima: levelling passes, a reused slot does not.
        {
            bool level = GameMemory.Continuous(850, 354, 862, 358);
            bool monster = GameMemory.Continuous(850, 354, 4437, 200);
            bool monster2 = GameMemory.Continuous(862, 358, 8178, 200);
            bool fresh = GameMemory.Continuous(0, 0, 862, 358);
            Console.WriteLine((level ? "PASS" : "FAIL") + "  lock: 850 -> 862 is the same character levelling");
            Console.WriteLine((!monster ? "PASS" : "FAIL") + "  lock: 850 -> 4,437 is a reused slot, not you");
            Console.WriteLine((!monster2 ? "PASS" : "FAIL") + "  lock: 862 -> 8,178 is a reused slot, not you");
            Console.WriteLine((fresh ? "PASS" : "FAIL") + "  lock: a fresh lock has nothing to compare and is accepted");
        }

        // A frozen copy against a misreading box, from the night's log: memory
        // held 100% while the numbers fell to 43%; the box sat on 17% while
        // memory stayed at a steady full pool.
        {
            bool frozen = MonitorEngine.MemoryFrozen(1.00, 0.43, 1.00, 1.00);
            bool misread = MonitorEngine.MemoryFrozen(0.17, 0.17, 1.00, 1.00);
            bool bothMove = MonitorEngine.MemoryFrozen(0.80, 0.60, 0.80, 0.61);
            Console.WriteLine((frozen ? "PASS" : "FAIL") + "  frozen: memory still at 100% while numbers fall is a frozen copy");
            Console.WriteLine((!misread ? "PASS" : "FAIL") + "  frozen: a box stuck on 17% is a misread, memory kept");
            Console.WriteLine((!bothMove ? "PASS" : "FAIL") + "  frozen: both moving is not frozen");
        }

        // Short enough to paste into a chat message, and carrying nobody's keys.
        {
            var mine = new AppConfig();
            mine.Life.Key = "5";
            mine.Life.Threshold = 0.42;
            mine.ArmHotkey = "F9";
            mine.OverlayShowMana = false;

            string block = SettingsShare.Export(mine, "9.9.9");

            var theirs = new AppConfig();
            theirs.Life.Key = "0";
            theirs.ArmHotkey = "F8";
            string? why = SettingsShare.Import(block, theirs, "9.9.9");

            Console.WriteLine((block.Length <= 300 ? "PASS" : "FAIL")
                              + $"  shared settings are {block.Length} chars (want <= 300)");
            Console.WriteLine((why is null ? "PASS" : "FAIL")
                              + $"  they load back ({why ?? "ok"})");
            Console.WriteLine((theirs.Life.Key == "0" && theirs.ArmHotkey == "F8"
                               ? "PASS" : "FAIL")
                              + $"  their own keys survive -> flask {theirs.Life.Key}, arm {theirs.ArmHotkey}");
            Console.WriteLine((Math.Abs(theirs.Life.Threshold - 0.42) < 1e-9
                               && !theirs.OverlayShowMana ? "PASS" : "FAIL")
                              + $"  the behaviour travels -> fire below {theirs.Life.Threshold:P0}, mana row {theirs.OverlayShowMana}");
        }

        // The misread that emptied a flask belt at full life. OCR turned
        // "Life 1,496/1,496" into "1,49 6/1149 6s", and a perfectly plausible
        // "6/1149" was sitting in the middle of it - six out of fourteen
        // hundred, which is a last-ditch emergency.
        {
            bool wreck = TextOcr.TryParse("1,49 6/1149 6s", out _, out _, "Life", 0);
            bool wreck2 = TextOcr.TryParse("Life 1,49 6/1149 6s", out _, out _, "Life", 0);
            bool clean = TextOcr.TryParse("Life 1,496/1,496", out int cc, out int cm,
                                          "Life", 1496);
            Console.WriteLine((!wreck && !wreck2 ? "PASS" : "FAIL")
                              + "  a pair cut out of a mangled line is refused");
            Console.WriteLine((clean && cc == 1496 && cm == 1496 ? "PASS" : "FAIL")
                              + $"  the same line read properly is accepted -> {cc}/{cm}");
        }

        // A box cropped to the numbers alone, which is a sensible thing to have
        // done and which the label check used to refuse - leaving the maximum
        // stale and memory hunting a number that no longer existed.
        {
            bool tight = TextOcr.TryParse("3,096/2,078", out int tc, out int tm, "Life", 0);
            Console.WriteLine((tight && tc == 3096 && tm == 2078 ? "PASS" : "FAIL")
                              + "  numbers alone in a box assigned to Life -> "
                              + (tight ? $"{tc}/{tm}" : "refused"));

            bool wrong = TextOcr.TryParse("Shield 512/3,357", out _, out _, "Life", 0);
            Console.WriteLine((!wrong ? "PASS" : "FAIL")
                              + "  another stat's line is still refused for Life");
        }

        // OCR reads small pale text over a moving background; the label came
        // back a letter short or a letter wrong often enough that demanding it
        // exactly threw away good numbers several times a minute.
        {
            (string Text, bool Want)[] cases =
            [
                ("tife 1,947/1,490", true),
                ("Li 1,947/1,490", true),
                ("Li fiv 1,947/1,490", true),
                ("Life 1,947/1,490", true),
                ("Shield 2,011/2,011", false),
                ("Ward 90/90", false),
                ("Mana 759/759", false),
                ("Spirit 42/132", false),
            ];

            bool all = true;
            foreach (var (text, want) in cases)
            {
                bool saw = TextOcr.HasLabel(text, "Life");
                if (saw != want)
                {
                    all = false;
                    Console.WriteLine($"FAIL  \"{text}\" as Life -> {saw}, wanted {want}");
                }
            }

            if (all) Console.WriteLine("PASS  label survives a mis-read letter, "
                                       + "without matching another stat");
        }

        // A settings file saved while PaddleOCR was the active choice, from
        // before it proved itself unreliable on real captures, must not go on
        // quietly using it forever - that is exactly the "held" spam and
        // vanishing bars this was written to catch.
        {
            bool migratesPaddle = AppConfig.ShouldMigrateOffPaddle("paddle");
            bool migratesPaddleAnyCase = AppConfig.ShouldMigrateOffPaddle("Paddle");
            bool leavesTesseract = !AppConfig.ShouldMigrateOffPaddle("tesseract");
            bool leavesWindows = !AppConfig.ShouldMigrateOffPaddle("windows");

            Console.WriteLine((migratesPaddle ? "PASS" : "FAIL")
                              + "  paddle migration: \"paddle\" is migrated off");
            Console.WriteLine((migratesPaddleAnyCase ? "PASS" : "FAIL")
                              + "  paddle migration: \"Paddle\" is migrated off regardless of case");
            Console.WriteLine((leavesTesseract ? "PASS" : "FAIL")
                              + "  paddle migration: \"tesseract\" is left alone");
            Console.WriteLine((leavesWindows ? "PASS" : "FAIL")
                              + "  paddle migration: \"windows\" is left alone");
        }

        // Proves Tesseract and PaddleOCR both actually read something, through
        // the real TextOcr.Recognise path, on a box the size the real
        // pipeline actually produces - a small crop, not a generously-sized
        // synthetic image. "It compiles" and "the object constructs without
        // throwing" were both true of PaddleOCR the night this was written,
        // and it still returned nothing: only running it caught that a
        // missing native runtime package meant every real reading was
        // silently failing and falling back to Windows OCR the whole time.
        {
            using var small = new System.Drawing.Bitmap(240, 48);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.Clear(System.Drawing.Color.White);
                g.DrawString("Life 874/874", new System.Drawing.Font("Arial", 24,
                    System.Drawing.FontStyle.Bold), System.Drawing.Brushes.Black, 4, 6);
            }

            var ocr = new TextOcr();
            var recognise = typeof(TextOcr).GetMethod("Recognise",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            foreach (string engine in new[] { "tesseract", "paddle" })
            {
                ocr.EngineChoice = engine;
                string text = "";
                Exception? threw = null;
                try { text = (string)recognise.Invoke(ocr, [small])!; }
                catch (Exception ex) { threw = ex.InnerException ?? ex; }

                bool ok = threw is null && text.Contains("874") && ocr.EngineWhy.Length == 0;
                Console.WriteLine((ok ? "PASS" : "FAIL")
                    + $"  {engine} reads a real 240x48 crop -> \"{text.Trim()}\""
                    + (threw is null ? "" : $" THREW: {threw.Message}")
                    + (ocr.EngineWhy.Length > 0 ? $" ({ocr.EngineWhy})" : ""));
            }
            ocr.Dispose();
        }

        Console.SetOut(realOut);
        int printedFails = mirror.ToString()
            .Split('\n').Count(line => line.TrimStart().StartsWith("FAIL"));

        Console.WriteLine(printedFails == 0 ? "\nALL PASS" : $"\n{printedFails} FAILED");
        Environment.Exit(printedFails == 0 ? 0 : 1);
    }

    static int Check(string name, Rectangle? got, int cx, int cy, int rad)
    {
        if (got is null) { Console.WriteLine($"FAIL  {name} -> not found"); return 1; }
        var r = got.Value;
        int gx = r.X + r.Width / 2, gy = r.Y + r.Height / 2;
        bool ok = Math.Abs(gx - cx) <= 6 && Math.Abs(gy - cy) <= 6
                  && Math.Abs(r.Width - rad * 2) <= 8 && Math.Abs(r.Height - rad * 2) <= 8;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} -> {r}");
        return ok ? 0 : 1;


    }
}
