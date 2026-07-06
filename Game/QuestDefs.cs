namespace XiHeadless.Game;

/// What a quest step does. Covers the mechanics the advanced-job unlocks use:
///  Talk/Examine — go to a zone, walk to a position, talk to/examine the entity there, answer Option.
///  Goto         — just travel to a zone.
///  ZoneInFrom   — travel so we ENTER Zone from FromZone (quests that fire onZoneIn checking prevZone).
///  Equip        — equip an item (e.g. a quest weapon) into a slot.
///  KillWith     — equip a weapon, then defeat Count monsters with it (combat-objective steps).
///  Trade        — walk to an NPC and trade Count of ItemId to it (quest turn-ins).
public enum StepKind { Goto, Talk, Examine, ZoneInFrom, Equip, KillWith, Trade }

/// One step of a quest flow. The server tracks quest state + key items; the engine performs the
/// physical actions in order. Built via the factory helpers for readability.
public readonly record struct QuestStep(
    StepKind Kind, string Zone = "", float X = 0, float Y = 0, float Z = 0,
    uint Option = 0, ushort ItemId = 0, byte Slot = 0, int Count = 0, string FromZone = "", string Label = "",
    ushort EventId = 0)
{
    // eventId != 0 => BLIND-FINISH: the bot never receives the event-start packet (recv gap), so it sends the
    // Talk (triggers the NPC's onTrigger -> progressEvent(eventId) server-side) then EVENTENDs the KNOWN csid +
    // option; the server runs onEventFinish, setting the real quest flags. Verified via the 0x056 quest-log.
    public static QuestStep Talk(string zone, float x, float y, float z, uint option, string label, ushort eventId = 0) => new(StepKind.Talk, zone, x, y, z, option, Label: label, EventId: eventId);
    public static QuestStep Examine(string zone, float x, float y, float z, string label) => new(StepKind.Examine, zone, x, y, z, Label: label);
    public static QuestStep Goto(string zone, string label) => new(StepKind.Goto, zone, Label: label);
    public static QuestStep ZoneInFrom(string fromZone, string zone, string label) => new(StepKind.ZoneInFrom, zone, FromZone: fromZone, Label: label);
    public static QuestStep Equip(ushort itemId, byte slot, string label) => new(StepKind.Equip, ItemId: itemId, Slot: slot, Label: label);
    public static QuestStep KillWith(ushort weaponItem, int count, string label) => new(StepKind.KillWith, ItemId: weaponItem, Count: count, Label: label);
    public static QuestStep Trade(string zone, float x, float y, float z, ushort itemId, int qty, string label, ushort eventId = 0) => new(StepKind.Trade, zone, x, y, z, ItemId: itemId, Count: qty, Label: label, EventId: eventId);
}

/// Advanced-job unlock quest flows, keyed by job id, transcribed from the server quest Lua (positions,
/// options, item ids and zone names all verified against the server data).
///
/// IMPORTANT — the engine performs actions; the SERVER enforces the gates, which are NOT bypassed here:
///   * ADVANCED_JOB_LEVEL = 30 (a job must be level 30 first).
///   * Prerequisite quest chains (PLD/BST/BRD/etc.) — not encoded; must be done first.
///   * Expansion / zone access: BLU/COR/PUP need Treasures of Aht Urhgan + ToAU mission progress to
///     reach Aht Urhgan Whitegate; SAM needs Norg (Sea Serpent Grotto); SCH needs Wings of the Goddess.
/// Mechanics the fixed-step model does NOT yet execute (documented per quest, need follow-up support):
///   * Item TRADE to an NPC (SAM/BLU/SCH and others) — no trade step/capability yet; modeled as Talk.
///   * Timed waits (SAM ~3 game-days, PUP ~1) — no wait step.
///   * NM spawn-then-fight (NIN leeches, SAM Forger/Treant), RNG's no-damage poison kill, and SMN's
///     weather-gated light roaming — can't be expressed as deterministic steps.
public static class QuestDefs
{
    // Subjob unlock. There are TWO mutually-exclusive versions, flagged by which city you entered FIRST:
    // "The Old Lady" (Vera, Mhaura) and "Elder Memories" (Isacio, Selbina) — each requires the OTHER to still
    // be QUEST_AVAILABLE, so only the entered-first city's NPC will start a quest. The WAR entered Mhaura first,
    // so use The Old Lady. Flow (needs lv18): accept (ev131, option 40) -> trade WILD_RABBIT_TAIL (542, a
    // unique drop we must farm) -> get Cup of Dhalmel Saliva -> get Bloody Robe -> ev137 unlockJob. The chain
    // GIVES items 2 & 3; only the Rabbit Tail is farmed. (Do NOT rely on the ROV Gilgamesh-letter ev137 skip.)
    public static readonly QuestStep[] SubjobUnlock =
    {
        QuestStep.Goto("Mhaura", "travel to Mhaura (subjob quest — entered Mhaura first)"),
        QuestStep.Talk("Mhaura", -49f, -5f, 20f, 40, "Vera: accept The Old Lady (ev131, option 40)", eventId: 131),
    };
    public const ushort WildRabbitTail = 542;   // first trade item for The Old Lady — unique drop to farm
    public const ushort CupOfDhalmelSaliva = 541;
    public const ushort BloodyRobe = 540;

    // Subjob quest COMPLETION (after accept, Prog=1). Section 2: trade Wild Rabbit Tail -> ev135 (Prog2, get
    // Cup) -> trade Cup -> ev136 (Prog3, get Bloody Robe) -> trade Bloody Robe -> ev137 (unlockJob + complete).
    // Each Trade triggers onTrade->progressEvent server-side; we blind-finish the known csid (recv gap). The
    // Cup/Bloody Robe are given by the quest as it progresses; only the Rabbit Tail is farmed.
    public static readonly QuestStep[] SubjobComplete =
    {
        QuestStep.Goto("Mhaura", "travel to Mhaura (subjob completion)"),
        QuestStep.Trade("Mhaura", -49f, -5f, 20f, WildRabbitTail, 1, "Vera: trade Wild Rabbit Tail -> ev135", eventId: 135),
        QuestStep.Trade("Mhaura", -49f, -5f, 20f, CupOfDhalmelSaliva, 1, "Vera: trade Cup of Dhalmel Saliva -> ev136", eventId: 136),
        QuestStep.Trade("Mhaura", -49f, -5f, 20f, BloodyRobe, 1, "Vera: trade Bloody Robe -> ev137 unlock subjob", eventId: 137),
    };

    public static readonly Dictionary<byte, QuestStep[]> Unlock = new()
    {
        // PLD — "A Knight's Test" (Southern San d'Oria + Davoi). PREREQ: A Squire's Test I+II, level 30.
        [Job.Pld] = new[]
        {
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Balasiel: accept (ev627)", 627),
            QuestStep.Talk("Southern_San_dOria", -55f, -8f, -32f, 0, "Baunise: Book of the West (ev634)", 634),
            QuestStep.Talk("Southern_San_dOria", 55.749f, -8.601f, -29.354f, 0, "Cahaurme: Book of the East (ev633)", 633),
            QuestStep.Examine("Davoi", -221f, 2f, -293f, "Disused Well: get Knight's Soul"),
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Balasiel: complete -> unlock PLD (ev628)", 628),
        },

        // DRK — "Blade of Darkness" (Bastok). PREREQ: level 30. Chaosbringer = item 16607.
        [Job.Drk] = new[]
        {
            QuestStep.Talk("Bastok_Mines", 52f, 0f, -36f, 0, "Gumbah: accept (ev99)"),
            QuestStep.ZoneInFrom("Palborough_Mines", "Zeruhn_Mines", "enter Zeruhn from Palborough -> Chaosbringer"),
            QuestStep.Equip(16607, EquipSlot.Main, "equip Chaosbringer"),
            QuestStep.KillWith(16607, 100, "kill 100 monsters wielding Chaosbringer"),
            QuestStep.ZoneInFrom("Pashhow_Marshlands", "Beadeaux", "enter Beadeaux from Pashhow -> unlock DRK"),
        },

        // NIN — "Ayame and Kaede" (Port Bastok, Korroloka Tunnel, Norg). PREREQ: level 30. No prior quest.
        // CAVEAT: the qm2 examines spawn 3x Korroloka Leech NMs that must be killed between the two examines.
        [Job.Nin] = new[]
        {
            QuestStep.Talk("Port_Bastok", 48f, -6f, 67f, 0, "Kaede: offer quest (ev240, begin)"),
            QuestStep.Talk("Port_Bastok", -96f, -2f, 29f, 0, "Kagetora -> Prog1 (ev241)"),
            QuestStep.Talk("Port_Bastok", 33f, -6f, 67f, 0, "Ensetsu -> Prog2 (ev242)"),
            QuestStep.Examine("Korroloka_Tunnel", -208f, -9f, 176f, "Examine qm2 -> spawns 3x Korroloka Leech NMs (kill them)"),
            QuestStep.Examine("Korroloka_Tunnel", -208f, -9f, 176f, "Re-examine qm2 -> Strangely Shaped Coral, Prog3"),
            QuestStep.Talk("Port_Bastok", 33f, -6f, 67f, 0, "Ensetsu -> Prog4 (ev245)"),
            QuestStep.Talk("Norg", -23f, 0f, -9f, 0, "Ryoma -> Sealed Dagger, Prog5 (ev95)"),
            QuestStep.Talk("Port_Bastok", 33f, -6f, 67f, 0, "Ensetsu: complete -> unlock NIN (ev246)"),
        },

        // RNG — "The Fanged One" (Windurst Woods, Sauromugue Champaign). PREREQ: level 30.
        // CAVEAT: Old Sabertooth must die from its own POISON with ZERO player damage — the KillWith below
        // is a PLACEHOLDER; the real action is "spawn it, then do not attack". Not faithfully executable.
        [Job.Rng] = new[]
        {
            QuestStep.Talk("Windurst_Woods", 117f, -3f, 92f, 0, "Perih Vashai: accept (ev351)"),
            QuestStep.Goto("Sauromugue_Champaign", "head to Sauromugue Champaign"),
            QuestStep.Examine("Sauromugue_Champaign", 666f, -8f, -379f, "Examine Tiger Bones -> spawn Old Sabertooth"),
            QuestStep.KillWith(0, 1, "PLACEHOLDER: let Old Sabertooth die from poison WITHOUT attacking"),
            QuestStep.Examine("Sauromugue_Champaign", 666f, -8f, -379f, "Re-examine Tiger Bones -> Old Tiger's Fang"),
            QuestStep.Talk("Windurst_Woods", 117f, -3f, 92f, 0, "Perih Vashai: turn in -> unlock RNG (ev357)"),
        },

        // SMN — "I Can Hear a Rainbow" (Windurst Walls start; weather-gated outdoor roam; La Theine turn-in).
        // PREREQ: hold Carbuncle's Ruby (item 1125), level 30.
        // CAVEAT: the middle phase collects 7 elemental "lights" via onZoneIn auto-events that fire ONLY
        // under matching weather — NOT a fixed sequence. The ZoneInFrom rows are placeholders; real play
        // needs a weather-watch roam loop. Only the start/finish steps are deterministic.
        [Job.Smn] = new[]
        {
            QuestStep.Talk("Windurst_Walls", -26f, -14.498f, 262.994f, 0, "_6n2: begin with Carbuncle's Ruby (ev384)"),
            QuestStep.ZoneInFrom("West_Sarutabaruta", "East_Sarutabaruta", "PLACEHOLDER: collect a light under matching weather"),
            QuestStep.ZoneInFrom("La_Theine_Plateau", "East_Ronfaure", "PLACEHOLDER: collect a light under matching weather"),
            QuestStep.ZoneInFrom("Konschtat_Highlands", "North_Gustaberg", "PLACEHOLDER: collect a light under matching weather"),
            QuestStep.ZoneInFrom("Tahrongi_Canyon", "East_Sarutabaruta", "PLACEHOLDER: collect a light under matching weather"),
            QuestStep.ZoneInFrom("Valkurm_Dunes", "La_Theine_Plateau", "PLACEHOLDER: collect a light under matching weather"),
            QuestStep.Goto("La_Theine_Plateau", "return to La Theine for turn-in"),
            QuestStep.Talk("La_Theine_Plateau", -179.693f, 8.845f, 254.327f, 0, "qm3: trade Carbuncle's Ruby -> unlock SMN (ev124)"),
        },

        // BRD — "Path of the Bard" (Valkurm Dunes). PREREQ: complete "A Minstrel in Despair", level 30.
        [Job.Brd] = new[]
        {
            QuestStep.Examine("Valkurm_Dunes", -721f, -7f, 102f, "Song Runes: examine -> unlock BRD (cs2)"),
        },

        // BST — "Path of the Beastmaster" (Upper Jeuno). PREREQ: complete "Save My Son", level 30.
        [Job.Bst] = new[]
        {
            QuestStep.Talk("Upper_Jeuno", -55f, 8f, 95f, 0, "Brutus: talk -> unlock BST (ev70)"),
        },

        // DNC — "Lakeside Minuet" (Upper Jeuno, S.San d'Oria, Jugner Forest [S]). PREREQ: level 30 + WotG.
        [Job.Dnc] = new[]
        {
            QuestStep.Talk("Upper_Jeuno", -54.045f, -1f, 100.996f, 1, "Laila: accept (ev10111, opt1)"),
            QuestStep.Talk("Upper_Jeuno", -56.220f, -1f, 101.805f, 0, "Rhea Myuliah -> Prog1 (ev10113)"),
            QuestStep.Talk("Southern_San_dOria", 97f, 0.1f, 113f, 0, "Valderotaux -> Prog2 (ev888)"),
            QuestStep.Talk("Upper_Jeuno", -56.220f, -1f, 101.805f, 0, "Rhea Myuliah -> Prog3 (ev10115)"),
            QuestStep.Talk("Jugner_Forest_[S]", 104.2f, 4.1f, 443.6f, 0, "Glowing Pebbles -> Stardust Pebble, Prog4 (ev100)"),
            QuestStep.Talk("Upper_Jeuno", -54.045f, -1f, 100.996f, 0, "Laila: complete -> unlock DNC (ev10118)"),
        },

        // DRG — "The Holy Crest" (San d'Oria x3, Maze of Shakhrami, Meriphataud, Ghelsba battlefield).
        // PREREQ: level 30. NPC positions/events verified against the server scripts (Ceraulian ev24,
        // Novalmauge ev6, Morjean ev65/ev62 at !pos 99 0 116, Rahal ev60 -> KI Dragon Curse Remedy).
        // CAVEAT: Novalmauge is deep in Bostaunieux Oubliette (reached THROUGH Chateau d'Oraguille) and
        // PATROLS — the fixed-coord walk may miss him. CAVEAT: the final step is a BATTLEFIELD ("Holy
        // Crest" at the Hut Door): enter, kill Cyranuce M Cutauleon, and the win CS needs a NON-ZERO
        // option (the wyvern's name; 0 = decline) — battlefield entry/fight/CS-option are not
        // executable as fixed steps yet. Items: Pickaxe 605, Wyvern Egg 1159.
        [Job.Drg] = new[]
        {
            QuestStep.Talk("Port_San_dOria", 0f, -8f, -122f, 0, "Ceraulian: accept (ev24)"),
            QuestStep.Talk("Bostaunieux_Oubliette", 70f, -24f, 21f, 0, "Novalmauge (patrols; via Chateau d'Oraguille) -> ev6"),
            QuestStep.Talk("Northern_San_dOria", 98.609f, 0f, 114.141f, 0, "Morjean -> ev65"),
            QuestStep.Trade("Maze_of_Shakhrami", 255.75f, -0.18f, -144.89f, 605, 1, "Excavation Point: trade Pickaxe -> Wyvern Egg"),
            QuestStep.Talk("Northern_San_dOria", 98.609f, 0f, 114.141f, 0, "Morjean again -> ev62"),
            QuestStep.Trade("Meriphataud_Mountains", 640.0f, -15.9f, 10.7f, 1159, 1, "qm1: trade Wyvern Egg (ev56)"),
            QuestStep.Talk("Chateau_dOraguille", -28f, 0.1f, -6f, 0, "Rahal -> KI Dragon Curse Remedy (ev60)"),
            QuestStep.Examine("Ghelsba_Outpost", -162f, -11f, 78f, "Hut Door: BATTLEFIELD 'Holy Crest' — kill Cyranuce M Cutauleon; win CS needs a NON-ZERO option (wyvern name, 0=decline)"),
        },

        // SAM — "Forge Your Destiny" (Norg, Konschtat, Zi'Tah). PREREQ: level 30 + Norg access.
        // CAVEAT: heavily TRADE + NM-driven (Forger, Guardian Treant) + a ~3 game-day forge wait. The
        // examines below are NM-spawn/trade points; trades + wait are not executable yet. Items: Sacred
        // Branch 1153, Lump of Bomb Steel 1152, Lump of Oriental Steel 1151, Sacred Sprig 1198, Hatchet 1021.
        [Job.Sam] = new[]
        {
            QuestStep.Talk("Norg", 91f, -7f, -8f, 1, "Jaucribaix: begin (ev25)"),
            QuestStep.Examine("Konschtat_Highlands", -709f, 2f, 102f, "qm2: trade Lump of Oriental Steel -> spawn+kill Forger"),
            QuestStep.Examine("The_Sanctuary_of_ZiTah", 639f, -1f, -151f, "qm2: trade Hatchet -> spawn+kill Guardian Treant; get Sacred Branch"),
            QuestStep.Talk("Norg", 4f, 0f, -4f, 0, "Aeka: refine ore -> Lump of Oriental Steel"),
            QuestStep.Talk("Norg", 15f, 0f, 23f, 0, "Ranemaud: refine -> Sacred Sprig"),
            QuestStep.Talk("Norg", 91f, -7f, -8f, 0, "Jaucribaix: trade for forge (ev27), ~3 game-day wait"),
            QuestStep.Talk("Norg", 91f, -7f, -8f, 0, "Jaucribaix: after wait -> Mumeito, unlock SAM (ev29)"),
        },

        // BLU — "An Empty Vessel" (Aht Urhgan Whitegate, Aydeewa Subterrane). PREREQ: ToAU + Whitegate
        // access, level 30. CAVEAT: divination test (menu answers), then a TRADE of a per-player random
        // item (Siren's Tear 576 / Valkurm Sunsand 503 / Dangruf Stone 553); trade not executable yet.
        [Job.Blu] = new[]
        {
            QuestStep.Talk("Aht_Urhgan_Whitegate", 65f, -6f, -78f, 50, "Waoud: divination test (start)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 65f, -6f, -78f, 0, "Waoud: receive item assignment"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 65f, -6f, -78f, 0, "Waoud: trade assigned item"),
            QuestStep.Goto("Aydeewa_Subterrane", "enter Aydeewa with the item -> unlock BLU (ev3)"),
        },

        // COR — "Luck of the Draw" (Aht Urhgan Whitegate, Arrapago Reef, Talacca Cove). PREREQ: ToAU +
        // Whitegate access, level 30. Walk-and-talk; Forgotten Hexagun is a key item (791).
        [Job.Cor] = new[]
        {
            QuestStep.Talk("Aht_Urhgan_Whitegate", 75.225f, -6f, -137.203f, 0, "Ratihb: accept (ev547)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 149.11f, -2f, -2.7127f, 0, "Mafwahb -> Prog2 (ev548)"),
            QuestStep.Examine("Arrapago_Reef", 468.767f, -12.292f, 111.817f, "qm6 -> Prog3 (ev211)"),
            QuestStep.Examine("Talacca_Cove", -62.239f, -7.9619f, -137.1251f, "qm1 -> Forgotten Hexagun, Prog4 (ev2)"),
            QuestStep.Examine("Talacca_Cove", -99f, -7f, -91f, "Rock Slab -> unlock COR (ev3)"),
        },

        // PUP — "No Strings Attached" (Bastok Markets, Aht Urhgan Whitegate, Arrapago Reef). PREREQ: ToAU +
        // Whitegate access, level 30. CAVEAT: ~1 game-day wait between Ghatsad turn-in and the final talk.
        // Antique Automaton is a key item; Animator reward = item 17859.
        [Job.Pup] = new[]
        {
            QuestStep.Talk("Bastok_Markets", -285.382f, -13.021f, -84.743f, 0, "Shamarhaan: initial contact (ev434)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 101.329f, -6.999f, -29.042f, 0, "Iruki-Waraki: accept (ev260)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 34.325f, -7.804f, 57.511f, 0, "Ghatsad -> Prog1 (ev262)"),
            QuestStep.Examine("Arrapago_Reef", 457.128f, -8.249f, 60.795f, "qm10 -> Antique Automaton, Prog2 (cs214)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 34.325f, -7.804f, 57.511f, 0, "Ghatsad: turn in -> Prog3 (ev264), 1 game-day wait"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 34.325f, -7.804f, 57.511f, 0, "Ghatsad: after wait -> Prog4 (ev265)"),
            QuestStep.Talk("Aht_Urhgan_Whitegate", 101.329f, -6.999f, -29.042f, 0, "Iruki-Waraki: complete -> unlock PUP (ev266)"),
        },

        // SCH — "A Little Knowledge" (Eldieme Necropolis [S], Crawlers' Nest [S]). PREREQ: WotG/Campaign
        // access, level 30. CAVEAT: TRADE-driven (Rolanberry 4365 x12 -> Tucker for Sheet of Vellum 2550
        // x12 -> Erlene); trades not executable yet.
        [Job.Sch] = new[]
        {
            QuestStep.Talk("The_Eldieme_Necropolis_[S]", 376.936f, -39.999f, 17.914f, 0, "Erlene: begin (ev10)"),
            QuestStep.Goto("Crawlers_Nest_[S]", "go to Crawlers' Nest [S] for Tucker"),
            QuestStep.Trade("Crawlers_Nest_[S]", 216.763f, -32.441f, -20.239f, 4365, 12, "Tucker: trade 12 Rolanberry -> Sheet of Vellum"),
            QuestStep.Trade("The_Eldieme_Necropolis_[S]", 376.936f, -39.999f, 17.914f, 2550, 12, "Erlene: trade 12 Sheet of Vellum -> unlock SCH"),
        },

        // GEO — "Dances with Luopans" (Western Adoulin; Sylvie at !pos 78.094 32 135.725). PREREQ: level 30 +
        // Adoulin access. Transcribed from scripts/zones/Western_Adoulin/npcs/Sylvie.lua (ev31 accept opt1 ->
        // addQuest; ev34 onTrade Petrified Log 703 with KI Fistful of Homeland Soil -> KI Luopan; ev36 when
        // GEO_DWL_Luopan=1 -> Matre Bell 21460 + Plate of Indi-Poison 6074 + unlockJob GEO), the nation Ergon
        // Locus (Windurst = Tahrongi_Canyon/npcs/Ergon_Locus.lua, examine -> KI soil; Sandy = La Theine,
        // Bastok = Konschtat) and effects/healing.lua + Ceizak/Yahse Zone.lua for the Luopan phase.
        // TODO(zone-access): the Adoulin cluster (256/257/260/261) only has zonelines among itself — no
        // overland edge from the mainland (ship route), so QuestRunner can't route there on foot yet.
        // CAVEAT: the 4th step is a RESTING mechanic, not an examine — with KI Luopan, /heal INSIDE an Ergon
        // Locus trigger area (Ceizak K-10 cyl x357.8 z-250.2 r11 / I-8 x87.2 z72.9 r8; Yahse F-6 x-447.7
        // z362.8 r6.6) until "mystical warmth" fires (random, 3 ticks..8 min; sets GEO_DWL_Luopan=1). The
        // fixed-step model has no Rest step — the Examine below is a PLACEHOLDER walk-to.
        [Job.Geo] = new[]
        {
            QuestStep.Talk("Western_Adoulin", 78.094f, 32f, 135.725f, 1, "Sylvie: accept Dances with Luopans (ev31, opt1)", 31),
            QuestStep.Examine("Tahrongi_Canyon", 90.846f, 40.448f, 339.803f, "Ergon Locus ???: examine -> KI Fistful of Homeland Soil (Windurst-nation spot)"),
            QuestStep.Trade("Western_Adoulin", 78.094f, 32f, 135.725f, 703, 1, "Sylvie: trade Petrified Log -> KI Luopan (ev34)", 34),
            QuestStep.Examine("Ceizak_Battlegrounds", 357.819f, 0f, -250.201f, "PLACEHOLDER: REST at the Ergon Locus (K-10) holding KI Luopan until 'mystical warmth' (max ~8 min healing)"),
            QuestStep.Talk("Western_Adoulin", 78.094f, 32f, 135.725f, 0, "Sylvie: complete -> Matre Bell + Plate of Indi-Poison, unlock GEO (ev36)", 36),
        },

        // RUN — "Children of the Rune" (Eastern Adoulin; Octavien at !pos 100.580 -40.150 -63.830). PREREQ:
        // level 30 + Adoulin access. Transcribed from scripts/zones/Eastern_Adoulin/npcs/Octavien.lua (ev23
        // accept opt1 -> addQuest; ev26 with KI Yahse Wildflower Petal, opt1 = rune trial -> REWARD_PENDING,
        // same onEventFinish gives Sowilo Claymore 20781 + unlockJob RUN) and Yahse_Hunting_Grounds/npcs/
        // Yahse_Wildflower.lua (examine while ACCEPTED -> KI petal). TODO(zone-access): same Adoulin-on-foot
        // problem as GEO. NOTE: the ev26 trial HALVES the bot's HP and MP (onEventUpdate) before completing —
        // top up first; a low-HP bot survives (floor of 5 HP is not enforced below hp<=5).
        [Job.Run] = new[]
        {
            QuestStep.Talk("Eastern_Adoulin", 100.580f, -40.150f, -63.830f, 1, "Octavien: accept Children of the Rune (ev23, opt1)", 23),
            QuestStep.Examine("Yahse_Hunting_Grounds", 370.6285f, 0.6692f, 153.3728f, "Yahse Wildflower: examine -> KI Yahse Wildflower Petal"),
            QuestStep.Talk("Eastern_Adoulin", 100.580f, -40.150f, -63.830f, 1, "Octavien: rune trial (ev26, opt1; HALVES HP/MP) -> Sowilo Claymore, unlock RUN", 26),
        },
    };

    /// Prerequisite quest chains that must be COMPLETED before the unlock quest will start. Each entry
    /// is the chain's quests concatenated in order; the runner executes these before Unlock[job]. Same
    /// step model, same caveats (the server still enforces each quest's own level/fame/time gates).
    public static readonly Dictionary<byte, QuestStep[]> Prereqs = new()
    {
        // PLD: A Squire's Test (trade Revival Tree Root 940) -> A Squire's Test II (lvl 10; timed
        // Stalactite Dew grab in Ordelles Caves — the 30s qm2->qm3 window isn't executable as steps).
        [Job.Pld] = new[]
        {
            // Event ids from A_Squires_Test*.lua — WITHOUT them the blind-finish never runs and the server
            // leaves each quest un-progressed (the root trade landed but ev617 was never EVENTENDed;
            // Balasiel kept re-prompting for the root).
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "A Squire's Test: accept (Balasiel, ev616)", 616),
            QuestStep.Trade("Southern_San_dOria", -136f, -11f, 64f, 940, 1, "A Squire's Test: trade Revival Tree Root (ev617)", 617),
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Squire II: accept (lvl 10+, ev625)", 625),
            // qm2/qm3 exact server coords (npc_list ids 17568172 / 17568173). Both are messageSpecial qm (NO
            // event/csid): qm2.onTrigger sets a 30s Timer, qm3.onTrigger grants KI Stalactite Dew if within it.
            // So these carry NO EventId — QuestRunner fires the 0x1A trigger only (with retry). Re-triggering
            // qm2 resets the timer, so the trigger-retry keeps the window fresh for the ~50y hop to qm3.
            // BLOCKED: the Ordelle's navmesh does NOT connect these qm — they sit on an isolated island at Y~1
            // while every cave route/entrance reaches only the overhang shelf above them (Y~30) or the deep
            // floor (Y~-29); the ~6y vertical seam at the qm1 area is unbridged. Needs a navmesh fix (regen /
            // off-mesh link) before this step can execute. See nav-test evidence.
            QuestStep.Examine("Ordelles_Caves", -91.745f, 1.246f, 276.166f, "Squire II: examine pool qm2 (starts 30s Timer)"),
            QuestStep.Examine("Ordelles_Caves", -140.279f, 0.736f, 265.654f, "Squire II: examine dew qm3 within 30s -> Stalactite Dew"),
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Squire II: return dew (Balasiel, ev626)", 626),
        },

        // BST: Chocobo's Wounds (lvl 20; feed gausebit wildgrass 534 several times, 45s between feeds —
        // the cooldown/early-feed rejection isn't executable) -> Save My Son (lvl 18; Nightflowers must
        // be examined at NIGHT 21:30-05:40).
        [Job.Bst] = new[]
        {
            QuestStep.Talk("Upper_Jeuno", -55f, 8f, 95f, 1, "Chocobo's Wounds: accept (Brutus, opt1)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (1)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (2)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (3)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (4)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (5)"),
            QuestStep.Trade("Upper_Jeuno", -61.42f, 8.2f, 93f, 534, 1, "feed gausebit wildgrass (6) -> complete"),
            QuestStep.Talk("Lower_Jeuno", -82.22f, -7.65f, -168.839f, 0, "Save My Son: begin (door _6t2)"),
            QuestStep.Talk("Upper_Jeuno", -50.541f, 8.199f, 87.17f, 0, "Save My Son: Shalott story"),
            QuestStep.Examine("Qufim_Island", -264.775f, -3.718f, 28.767f, "Save My Son: Nightflowers at NIGHT (21:30-05:40)"),
            QuestStep.Talk("Lower_Jeuno", -82.22f, -7.65f, -168.839f, 0, "Save My Son: return to door -> complete"),
        },

        // BRD: The Old Monument (trade Sheet of Parchment 917 -> Poetic Parchment 634) -> A Minstrel in
        // Despair (trade Poetic Parchment 634).
        [Job.Brd] = new[]
        {
            QuestStep.Talk("Lower_Jeuno", -17f, 0f, -61f, 0, "Old Monument: talk Mertaire"),
            QuestStep.Examine("Buburimu_Peninsula", -244f, 16f, -280f, "Old Monument: examine Song Runes"),
            QuestStep.Trade("Buburimu_Peninsula", -244f, 16f, -280f, 917, 1, "Old Monument: trade Sheet of Parchment"),
            QuestStep.Trade("Lower_Jeuno", -17.201f, -0.100f, -60.072f, 634, 1, "Minstrel: trade Poetic Parchment -> complete"),
        },
    };
}
