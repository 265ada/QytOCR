namespace P02;

/// <summary>
/// Every explanation in one place.
///
/// A setting whose name you have to guess at is a setting that gets left wrong,
/// and several of the worst failures here came from exactly that: a maximum
/// typed into the wrong box, a burst set faster than a key can physically be
/// sent, a colour slider that did nothing. Each of these says what the thing
/// is for, when it is worth changing, and what to set it to.
/// </summary>
internal static class Tips
{
    private static readonly ToolTip Tip = new()
    {
        AutoPopDelay = 30000,
        InitialDelay = 350,
        ReshowDelay = 120,
        ShowAlways = true,
    };

    public static void On(Control c, params string[] lines) =>
        Tip.SetToolTip(c, string.Join(Environment.NewLine, lines));

    // --- per globe ------------------------------------------------------

    public const string Watch =
        "Whether this pool is watched at all.\n\n"
        + "Switched off it is not even read: a screen capture costs the same "
        + "whether the answer is used or not, and skipping one is worth about "
        + "20 ms of every poll.";

    public static readonly string[] FireBelow =
    [
        "The trigger. Below this fraction it presses your flask key.",
        "",
        "Set it above where you are comfortable rather than at the edge - a",
        "flask recovers over a couple of seconds, so firing at the last moment",
        "means the recovery lands after the hit that killed you.",
        "",
        "Around 60-70% for life on a character that takes real hits; lower if",
        "you are mostly chipping through trash and want to keep charges.",
    ];

    public static readonly string[] Cooldown =
    [
        "The gap between presses while you sit below the trigger.",
        "",
        "It cannot go below what a burst takes to send, which the line under",
        "these settings works out for you. Setting it lower does nothing except",
        "make requests get skipped.",
        "",
        "300-900 ms suits most builds. This is the setting that controls how",
        "often it fires - not the hold time.",
        "",
        "It also stops on its own when presses are doing nothing: after three",
        "in a row that do not move the pool, it waits a few seconds rather than",
        "spending what is left of your charges on a flask that cannot use them.",
    ];

    public static readonly string[] PanicBelow =
    [
        "Under this, it stops waiting: the short gap below is used instead of",
        "the cooldown, and it fires on the first low reading rather than",
        "waiting for a second one to confirm.",
        "",
        "It also triggers on rate - if the pool is falling faster than 30% a",
        "second it panics even while still above this line, so a spike is",
        "caught on the way down instead of after it lands.",
        "",
        "Half your trigger is a reasonable start.",
    ];

    public static readonly string[] PanicGap =
    [
        "The gap between presses while panicking.",
        "",
        "Same floor as the cooldown: a burst takes as long as it takes to send.",
        "60-260 ms.",
    ];

    public static readonly string[] Presses =
    [
        "How many presses one trigger sends.",
        "",
        "Worth knowing before raising this: a flask recovers over a duration and",
        "the recovery is cancelled the moment the pool fills, so a second press",
        "landing while the first is still working spends a charge for almost",
        "nothing. Three presses per trigger is what emptied a full set of",
        "charges in one fight.",
        "",
        "Leave at 1 unless one charge genuinely does not cover a hit. If it does",
        "not, raise the panic settings instead - those fire again only while you",
        "are still low.",
    ];

    public static readonly string[] Hold =
    [
        "How long the key is held down.",
        "",
        "A game reads input once a frame, so a press shorter than one frame can",
        "go down and back up between two of them and never register - 20 ms is",
        "invisible below about 50 fps.",
        "",
        "70 ms spans a frame down to 14 fps and is the default. Raise it if the",
        "ding sounds and nothing happens in game; there is no benefit above",
        "about 120 ms, and every press costs that long to send.",
        "",
        "It is not a rate limit. A long hold does slow firing down, but only",
        "because each press takes that long to leave - and it slows the",
        "emergency press down with everything else. Use Cooldown to control how",
        "often it fires.",
    ];

    public static readonly string[] KnownMax =
    [
        "Your maximum for this pool. It fills itself in and keeps itself up to",
        "date; there is normally no reason to touch it.",
        "",
        "It is read from the numbers and re-read as you level, and a new value",
        "has to hold steady before it is taken, so a stray digit cannot become",
        "your maximum. It is a reading, not a rule - a value here never refuses",
        "anything, which is what used to leave it blind for a whole level after",
        "a gear change.",
        "",
        "Type one only if the numbers cannot be read at all. 0 means work it",
        "out.",
    ];

    public static readonly string[] Region =
    [
        "The box on screen holding this globe, for the pixel fallback.",
        "",
        "Only used when nothing better is available. The pixels cannot tell life",
        "from energy shield, because the shield is drawn over the same globe,",
        "and they read a poisoned globe as empty because it turns green.",
        "",
        "Set the numbers up instead and this stops mattering.",
    ];

    public static readonly string[] SetRegion =
    [
        "Drag a box around this globe by hand.",
        "",
        "Use it when Auto-find picks badly. Crop to the liquid only - no frame,",
        "no gargoyle - then press Full = 100% with the globe topped up.",
    ];

    public static readonly string[] AutoFind =
    [
        "Finds this globe by looking for a large round region of its colour in",
        "the corner of the game where it lives.",
        "",
        "The globe has to be full when you press it. It sweeps a range of",
        "colour thresholds and keeps the largest disc any of them finds, then",
        "calibrates against the full globe and checks the result reads 100% -",
        "saying so plainly if it does not, rather than accepting a bad box.",
    ];

    public static readonly string[] FullHundred =
    [
        "Records where the liquid sits when the globe is full.",
        "",
        "A box drawn by hand always carries some frame above the liquid, and",
        "every one of those rows counts as missing - 24 rows of frame on a",
        "270-row box reads as 91% at full health, and no colour setting can fix",
        "it. This is what does.",
        "",
        "Press it with the globe topped up. Re-drawing the region clears it.",
    ];

    public static readonly string[] Check =
    [
        "A live view of what the detector sees.",
        "",
        "Green line = the liquid surface it found, gold = your trigger. Spend",
        "the globe and watch the number follow it down; if it stays near 100%",
        "while the globe drains, that is the bug.",
        "",
        "Tick the mask box to see exactly which pixels it counts as liquid.",
    ];

    // --- global ---------------------------------------------------------

    public static readonly string[] WindowMatch =
    [
        "Only fires while the focused window's title contains this.",
        "",
        "It is a substring test, so \"Path of Exile\" matches \"Path of Exile 2\".",
        "Clearing it lets it fire into whatever has focus, which is rarely what",
        "anyone wants.",
    ];

    public static readonly string[] PollHz =
    [
        "How many times a second the screen is read.",
        "",
        "Screen capture costs about the same whatever the region size, so this",
        "has a ceiling set by your machine rather than by the setting - the",
        "status line reports what it actually achieved. Asking for more than",
        "that only spins a core.",
        "",
        "60 is a good target. Reading from memory instead is far faster than",
        "any of this.",
    ];

    public static readonly string[] ArmKey =
    [
        "A hotkey that arms and disarms without alt-tabbing.",
        "",
        "It works while the game has focus, which is the point.",
    ];

    public static readonly string[] TestKeys =
    [
        "Sends your keys once, ignoring arm state and the window check.",
        "",
        "Click it, click into the game, and watch. If the flask does not fire,",
        "the problem is the keybind or input reaching the game - not detection.",
        "That is the fastest way to split those two apart.",
    ];

    public static readonly string[] Diagnostics =
    [
        "Writes everything needed to explain a misbehaving setup to one zip:",
        "what each globe reads right now as numbers and pictures, every",
        "setting, which monitor things are on, the scancode each key resolves",
        "to, and the recent log.",
        "",
        "It ends with a verdict listing anything that looks wrong. The plain",
        "text version beside it is meant to be pasted into a chat.",
    ];

    public static readonly string[] Ding =
    [
        "A short sound when a key is actually sent.",
        "",
        "It is the difference between \"it decided to fire\" and \"something went",
        "out\", which is otherwise very hard to tell apart while playing.",
    ];

    public static readonly string[] DingGap =
    [
        "The least time between two dings.",
        "",
        "Firing can repeat several times a second and a ding per press is a",
        "machine gun. A few seconds is enough to know it is working.",
    ];

    public static readonly string[] Volume =
    [
        "Loudness over the original level, in dB. 0 is unchanged.",
        "",
        "Up to +16 is pure level. Past that the sound is already at full scale,",
        "so the extra comes from filling in the gap between its sharp peak and",
        "its quiet tail - louder, and a little harder-edged. Nothing clips at",
        "any setting.",
        "",
        "Start around +12 and go up only if it is getting lost under the game.",
    ];

    public static readonly string[] SendBy =
    [
        "How the key is delivered.",
        "",
        "Injected input goes into the system as a hardware scancode, which is",
        "what most games read.",
        "",
        "Posted to window sends it straight to the game window instead. It",
        "reaches a window without focus, and some games take it where injected",
        "input is missed - and some ignore it entirely, because it never touches",
        "the keyboard state raw input reads.",
        "",
        "If the ding sounds and nothing happens in game, try the other one",
        "before changing anything else.",
    ];

    public static readonly string[] Memory =
    [
        "Read life and mana out of the game's memory instead of the screen.",
        "",
        "Exact, and far more responsive: it refreshes every 15 ms where the",
        "numbers manage 60-160 ms. It is also the most intrusive thing here -",
        "reading another process is what anti-cheat looks for, where watching",
        "the screen is passive.",
        "",
        "It hardcodes no offsets, so it survives patches: given your maxima it",
        "finds them near each other in the heap and learns the layout from",
        "whichever spacing the candidates agree on.",
    ];

    public static readonly string[] Rescan =
    [
        "Searches memory again from scratch.",
        "",
        "Rarely needed - it re-searches on its own when a reading stops making",
        "sense, and when your maximum changes. Use it after switching character.",
    ];

    public static readonly string[] OcrEngine =
    [
        "Which engine reads the digits beside a globe. Only this - not the",
        "one-time 'find my numbers for me' search, which still uses Windows",
        "either way, since it needs word positions on the screen that only it",
        "gives back here.",
        "",
        "Windows (built in): no download, no setup - and no way to tell it",
        "'only digits live in this box,' which is most of why the numbers here",
        "have ever come back as a letter or a symbol instead.",
        "",
        "Tesseract (light): a real OCR engine, restricted here to exactly the",
        "digits and the label words, so it is not physically able to answer",
        "with anything else. Small download, modest CPU cost.",
        "",
        "PaddleOCR (accurate): a modern engine built for exactly this kind of",
        "small or stylized text, and the most accurate of the three in testing",
        "here - it read a label Tesseract dropped a space from correctly.",
        "Needed a minimum-size fix to avoid failing outright on a small crop,",
        "which is what a fresh box usually is; upscaled internally now if it",
        "is too small to answer at all. Largest download of the three, and a",
        "little more CPU per reading.",
        "",
        "Both are a bigger step up over Windows than they are over each other -",
        "on a handful of digits in a fixed box, do not expect a dramatic gap",
        "between Tesseract and PaddleOCR specifically. If one does not start on",
        "your machine, reading quietly falls back to Windows and says why in",
        "the log.",
    ];

    public static readonly string[] PadKind =
    [
        "Which sort of controller QytOCR pretends to be.",
        "",
        "Steam Input does not pass your controller through to the game - it",
        "reads yours and hands the game one of its own. What it does with a",
        "third pad that turns up depends on which sort it thinks that pad is,",
        "and it treats PlayStation controllers differently from Xbox ones.",
        "",
        "If one of them is being swallowed, try the other.",
    ];

    public static readonly string[] FollowMode =
    [
        "Watches which HUD the game is showing and switches Send keys by to",
        "match: the controller bar with its coloured A X Y B buttons means the",
        "pad, the keyboard layout with the flasks beside the life globe means",
        "keys.",
        "",
        "Checked every few seconds and only switched when two looks agree.",
        "Loading screens and menus show neither, and change nothing.",
    ];

    public static readonly string[] AlsoKey =
    [
        "Sends the keyboard key as well as the controller button.",
        "",
        "With Steam Input in the middle it is genuinely hard to know which",
        "layer a press will survive, so this sends both and lets whichever",
        "works get through.",
        "",
        "A flask that fires twice is not a problem - the second press lands on",
        "a flask already going and does nothing. A flask that does not fire is.",
    ];

    public static readonly string[] PadButton =
    [
        "Which button on the pad this flask sits on, in the game's own",
        "controller layout. Xbox names first, PlayStation beside them.",
        "",
        "This is not a key being sent a different way. A game in controller",
        "mode ignores the keyboard outright, which is why keys went nowhere.",
        "QytOCR presents a virtual pad through the ViGEmBus driver and presses",
        "that instead - so the driver has to be installed for any of it to",
        "work, and QytOCR will say so if it is not.",
    ];

    public static readonly string[] Badge =
    [
        "QytOCR, and nothing else. It is drawn rather than loaded - the orb is",
        "built out of geometry every time the app starts, at whatever size it",
        "is asked for, which is why it can turn.",
        "",
        "It does no work and reports nothing. If you would rather have your own",
        "picture behind the window, put one in the QytOCR folder beside the log,",
        "named backdrop.png.",
    ];

    public static readonly string[] Pin =
    [
        "A small always-on-top readout over the game: the pools you watch, the",
        "numbers being read, whether it is armed, a light when a key goes out,",
        "and a count of presses in the current fight.",
        "",
        "It has no background - only the readouts show - so drag it by any part",
        "of it.",
        "",
        "Ctrl and right-click on the readout itself opens its own options: three",
        "saved positions, a lock, and letting clicks pass straight through into",
        "the game.",
        "",
        "The three positions - Left, Middle and Right - exist because opening",
        "your inventory slides the character one way and the character sheet the",
        "other. Pick one and drag the readout where you want it; it is",
        "remembered there. With \"Follow the panels\" on, it chooses between them",
        "by where your character actually is, so it moves as the panels do.",
        "Ctrl deliberately, because a plain right-click over a game belongs to",
        "the game.",
        "",
        "Click-through has its own way back: hold Ctrl and the readout is solid",
        "again for as long as you hold it, so Ctrl and right-click always reaches",
        "the menu. Right-clicking this button also brings it back, unlocked and",
        "clickable, wherever it has got to.",
    ];

    public static readonly string[] HideCapture =
    [
        "Hides this window from screen capture, so it cannot be read as a globe",
        "when it covers one.",
        "",
        "Off for a reason: it hides the window from every capture path on the",
        "system - screenshots, the Snipping Tool, Discord and OBS all see",
        "nothing. Moving the window is usually the better answer.",
    ];

    public static readonly string[] TuneColours =
    [
        "Works out which colours count as liquid, from this globe as it is now.",
        "",
        "It samples the globe drained and full and picks the threshold that",
        "separates them, instead of you guessing at the sliders. Use it if the",
        "reading does not follow the globe down, or after recolouring the",
        "interface.",
        "",
        "It says so plainly when colour alone cannot tell full from empty here -",
        "which happens with a poisoned globe, and is the reason to use the",
        "numbers instead.",
    ];

    public static readonly string[] Numbers =
    [
        "The numbers printed above the globe - 1,490/1,490 and so on.",
        "",
        "This is the reading worth having. It is exact, needs no calibration, it",
        "cannot confuse life with energy shield, and it does not care what colour",
        "the globe has turned. It also tells it there are no numbers on screen at",
        "all, which is how it stays quiet in menus and loading screens.",
        "",
        "It finds the box for you; drag one by hand only if it picks badly.",
    ];

    public static readonly string[] Key =
    [
        "The key pressed for this pool. Click and press it.",
        "",
        "It has to be the key your flask is actually bound to in game - the slot",
        "number, not the flask. Test keys is the way to check without dying to",
        "find out.",
    ];

    public static readonly string[] Uber =
    [
        "A last-resort press, one only, when everything else has missed.",
        "",
        "The cooldown and the hold both mean there are moments where nothing can",
        "be sent, and a hit landing in one of those is how you die at what looks",
        "like a safe fraction. Under this line it sends one press regardless.",
        "",
        "One press, not a stream - it re-arms only after you have recovered, so",
        "it cannot drain your charges. Capped at 30%; set it well under your",
        "trigger, around 15-20%.",
    ];

    public static readonly string[] ShieldOn =
    [
        "Fire this pool's flask for energy shield as well as for life.",
        "",
        "Off by default because it is wrong for most characters: shield already",
        "recharges on its own, and a flask does nothing for it. Tick it only if",
        "you have the passive or the item that makes flasks recover shield.",
        "",
        "It reads your shield from the game's own structure, at its own offset -",
        "not from the globe, and not from a third box of text. Its maximum fills",
        "in by itself.",
        "",
        "Left off, shield is not just ignored - it is kept out of the life",
        "reading, which is otherwise a real source of misfires, because the",
        "shield is drawn over the same globe.",
    ];

    public static readonly string[] ShieldBelow =
    [
        "The fraction of your shield that fires the flask.",
        "",
        "Uses the same panic and emergency rules as life.",
    ];

    public static readonly string[] ShieldMax =
    [
        "Your maximum energy shield, or 0 to read it from its own numbers box.",
        "",
        "It has to be the shield maximum, not the life one - typing life here",
        "makes the shield reading follow your life, which fires at the wrong",
        "time in both directions.",
    ];

    public static readonly string[] LowLifeOn =
    [
        "A separate \"oh shit\" net for a low-life build: energy shield is the",
        "pool that actually absorbs hits, and its own three floors below say",
        "when to drink the life flask - not shield's own threshold.",
        "",
        "Mutually exclusive with \"Also fire for energy shield\": that one",
        "assumes the flask refills shield directly. This one does not touch",
        "shield at all - it reads shield only to decide when life needs a",
        "flask before the hit that empties it reaches life itself. Turning",
        "this on turns that one off.",
        "",
        "Each floor fires once and stays quiet until shield has climbed back",
        "out, the same as the emergency and last-ditch nets elsewhere. It uses",
        "your life flask's key.",
        "",
        "While this is on, the overlay shows energy shield where life normally",
        "goes, since shield is the number actually worth watching here.",
    ];

    public static readonly string[] LowLifeTier1 =
    [
        "First warning: one press when shield first drops below this.",
        "",
        "Set it high enough to catch a shield that is falling before it is",
        "actually in danger - this is the least urgent of the three floors.",
    ];

    public static readonly string[] LowLifeTier2 =
    [
        "Second floor, further down: one more press.",
        "",
        "Shield fell through the first floor and kept going - this is the",
        "warning that it is close to gone.",
    ];

    public static readonly string[] LowLifeTier3 =
    [
        "Shield genuinely empty, or as close to it as you set this: the last",
        "press before life itself starts taking hits.",
        "",
        "This floor alone waits for a real, sustained empty reading rather",
        "than firing on the first frame that says zero - a raw zero is",
        "exactly what a misread or a covered, blind globe looks like too.",
    ];

    public static readonly string[] AnyWindow =
    [
        "Clears the window filter, so it fires into whatever has focus.",
        "",
        "Only useful for testing against something that is not the game. Put the",
        "title back before playing.",
    ];

    public static readonly string[] OpenLog =
    [
        "Opens the folder holding the log and your settings file.",
        "",
        "The log says what it read and why it did or did not fire, every poll",
        "that mattered. Export diagnostics is the better thing to send, but this",
        "is where to look if you want to read it yourself.",
    ];

    public static readonly string[] CheckUpdates =
    [
        "Asks GitHub whether there is a newer build.",
        "",
        "It lists everything that changed since the version you are on, not just",
        "the newest release, and says how many you are behind. It also checks on",
        "its own at launch unless you turn that off.",
    ];

    public static readonly string[] UpdateAtLaunch =
    [
        "Checks for a new version each time it starts.",
        "",
        "One request, and nothing installs without you saying so.",
        "",
        "Being three or fewer releases behind, it simply takes the update: ten",
        "seconds of notice on the overlay, then it swaps itself and comes back",
        "in a couple of seconds, still armed if it was armed. A bigger jump is a",
        "decision worth making rather than having made for you, so it asks.",
        "",
        "It also looks again every five minutes for releases marked critical -",
        "the ones that fix a way for this to sit quiet while you die. Those are",
        "the only ones that interrupt: thirty seconds of warning on the overlay,",
        "then your game is paused, and never while you are in a fight.",
    ];

    public static readonly string[] FindNumbers =
    [
        "Does the whole setup, in the right order, and tells you what happened.",
        "",
        "It finds the life and mana lines beside your globes, fills in your",
        "maxima from them, and points the memory search at those numbers. That",
        "order matters and there is no reason you should have to know it.",
        "",
        "Press it once with the game on screen and your numbers showing - in a",
        "town or a hideout is ideal. Press it again any time something looks",
        "wrong; it is the whole of the fix. It also runs itself after twenty",
        "seconds of reading nothing.",
    ];

    public static readonly string[] DingDisarmed =
    [
        "Ding for presses that were only decided on, while disarmed.",
        "",
        "Nothing is sent either way. It is for hearing whether it would have",
        "fired at the right moments before trusting it with your flasks - which",
        "is worth an evening.",
    ];

    public static readonly string[] Status =
    [
        "What it is doing right now: polls a second actually achieved, how long",
        "each poll takes, and where the readings are coming from.",
        "",
        "This is the line that says whether memory has locked on, and what it is",
        "still missing if it has not.",
    ];

    public static readonly string[] Focus =
    [
        "Why it is or is not firing at this moment.",
        "",
        "Disarmed, the focused window not matching, and nothing being readable",
        "all look identical from the outside. This says which one it is.",
    ];

    public static readonly string[] Live =
    [
        "The last thing that happened, as it happens: what was read, what was",
        "decided, and whether a key went out.",
    ];

    public static readonly string[] OverlaySnap =
    [
        "Sits the readout directly above the game's own life numbers, instead",
        "of wherever it was last dragged.",
        "",
        "It anchors to the numbers box, which is a fixed part of the HUD and",
        "already tracked - so it follows the readout it describes rather than a",
        "remembered screen position, which is wrong the moment a window moves or",
        "a monitor is added.",
        "",
        "Dragging is off while this is on; untick it to place it by hand again.",
    ];

    public static readonly string[] OverlayAutoHide =
    [
        "Hides the readout whenever the game's numbers are not on screen, or",
        "the game itself is not the focused window.",
        "",
        "A shop, the passive tree or an inventory covers the HUD, and while one",
        "is up the readout has nothing true to say - it is only in the way of",
        "the thing you opened. Alt-tabbing away is the same idea: nothing here",
        "is watching your game while something else has focus.",
        "",
        "Both are also exactly when nothing can fire, so the readout",
        "disappearing is information rather than a gap: gone means not",
        "watching.",
    ];

    public static readonly string[] LastDitch =
    [
        "A second net, below the emergency one.",
        "",
        "Each net fires once and then stays quiet until you have climbed back",
        "out, so one net is one press - and a single press can be wasted: it can",
        "land while a burst is still going out, or on a flask with no charges",
        "left. By the time that is apparent there is no second chance coming.",
        "",
        "A line further down costs nothing while things are going well. Set it",
        "well under the emergency threshold - around half of it.",
        "",
        "0 turns it off.",
    ];

    public static readonly string[] ShareSettings =
    [
        "Writes this whole setup out as text - onto the clipboard, and to a file",
        "beside the log - so it can be handed to someone else or to another",
        "machine.",
        "",
        "Every setting travels except the screen regions and your own maxima.",
        "Two machines rarely share a resolution or a monitor layout, and a region",
        "copied from someone else's screen is worse than none at all: it points",
        "confidently at nothing. Those are found again wherever it is loaded, and",
        "the maxima read themselves back in seconds.",
    ];

    public static readonly string[] ApplyShared =
    [
        "Applies exported settings from the clipboard.",
        "",
        "It only loads a block written by this same version. Settings gain",
        "meanings between releases - a hold time that was a rate limit, a maximum",
        "that was a rule and is now a reading - so a block from another version",
        "cannot be trusted to mean the same thing here.",
        "",
        "Your regions and maxima are left exactly as they are. It restarts",
        "afterwards, and comes back disarmed.",
    ];

    public static readonly string[] FollowBar =
    [
        "Keeps the readout in the right one of your three saved spots as you",
        "open and close panels.",
        "",
        "To set them up: put the readout where you want it, Ctrl and right-click",
        "it, and choose \"Remember this spot\". Do that once with your panels",
        "closed, once with the inventory open, and once with the character sheet",
        "open.",
        "",
        "It remembers what the screen looked like each time, and matches against",
        "that. There is nothing to name, no order to get right, and nothing that",
        "has to be found - a panel is either open or it is not.",
    ];

    public static readonly string[] NumbersOnly =
    [
        "Decide only from the numbers beside the globes, and from memory - never",
        "from the globe colours.",
        "",
        "The globe was the original way and it is the worst of the three. It",
        "cannot tell life from energy shield, because the shield is drawn over",
        "the same globe. It reads a poisoned globe as empty, because that turns",
        "green. It needs calibrating against a drained globe. And on plenty of",
        "setups no calibration exists at all - drained and full are the same hue",
        "at overlapping brightness, which is not something anyone can fix by",
        "pressing Tune colours again.",
        "",
        "On by default. Turn it off only if the numbers cannot be read on your",
        "machine and you would rather have a rough reading than none - and know",
        "that a rough reading is what the misfires were.",
    ];

    public static readonly string[] TeachIt =
    [
        "Learns this globe from two pictures of it: full, and empty.",
        "",
        "The old colour tuning asked which colours count as liquid, and on many",
        "globes there is no answer - drained and full are the same hue at",
        "overlapping brightness, and pressing it again could never change that.",
        "",
        "This asks a question that always has one: does this row look more like",
        "it did when the globe was full, or when it was empty? Rows differ from",
        "each other even where the picture as a whole does not, because the",
        "frame, the shading and the gargoyle sit in fixed places.",
        "",
        "Dead is the easiest empty to be sure of. It tells you how many rows",
        "came out useful, and says so plainly if the two pictures were too alike",
        "to learn anything from.",
    ];

    public static readonly string[] WhatIsWrong =
    [
        "Checks the whole setup and lists what is wrong, worst first, with the",
        "one thing that fixes each.",
        "",
        "All of it was knowable before - spread over a status bar, three",
        "coloured labels, a log file and a tooltip, in the app's words rather",
        "than yours. Working out which of them mattered took somebody who had",
        "read the source.",
        "",
        "It also runs itself at startup, and speaks up only when something is",
        "actually stopping it working.",
    ];

    public static readonly string[] HowTo =
    [
        "What to do, in order, in plain words.",
        "",
        "The four steps that set it up, what the readings mean, which settings",
        "are worth touching and which are not, and what to do when something",
        "looks wrong.",
        "",
        "It opens by itself the first time, when nothing has been set up yet.",
    ];

    public static readonly string[] History =
    [
        "Every release, newest first, with what each one was for.",
        "",
        "The changelog only ever appeared while an update was waiting, and only",
        "covered the versions between where you were and the newest. Once",
        "installed, a release had no way of telling you what it had changed.",
        "",
        "The one you are running is marked.",
    ];
}
