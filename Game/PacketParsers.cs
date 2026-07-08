using System.Buffers.Binary;

namespace XiHeadless.Game;

/// Parses the inbound sub-packets we care about into WorldState.
/// Offsets are body-relative (after the 4-byte sub-packet header) per the LSB s2c structs.
public static class PacketParsers
{
    static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    // Bit read matching the server's unpackBitsBE (common/utils.cpp). NOTE: despite the "BE" name it is actually
    // LITTLE-ENDIAN bit order — it views the byte stream as a native LE integer and pulls `len` bits starting at
    // the LSB of the byte at (bitOffset>>3). So field bit 0 is at the LOWEST bit of the lowest byte and fields fill
    // upward across bytes. (My first version read MSB-first, which bit-reversed every field — actor/target IDs were
    // garbage so nothing ever matched our id and the attacker count stayed 0.) Advances bitOff by n.
    static ulong BitsBE(ReadOnlySpan<byte> b, ref int bitOff, int n)
    {
        ulong v = 0;
        for (int i = 0; i < n; i++)
        {
            int bo = bitOff + i, byteIdx = bo >> 3, localBit = bo & 7;
            if (byteIdx < b.Length) v |= (ulong)((b[byteIdx] >> localBit) & 1) << i;   // LSB-first: bit i -> result position i
        }
        bitOff += n;
        return v;
    }

    // 0x028 GP_SERV_COMMAND_BATTLE2 (action packet). Bit-packed BE; data bits start at byte 5 (bit 40), per
    // 0x028_battle2.cpp::unpack. We only need WHO actioned WHOM: record any mob actor whose action targeted us
    // or a party member into w.Attackers, so the bot can SEE its attacker count (it was blind to combat actions
    // before — only HP% told it anything). We still walk every result field to advance the bit cursor across
    // multi-target packets so later target ids stay aligned.
    static void Battle2(ReadOnlySpan<byte> b, WorldState w)
    {
        int bit = 8 * 5;                                   // skip 4-byte header + worksize byte
        uint actor = (uint)BitsBE(b, ref bit, 32);          // m_uID (caster/actor)
        int trgSum = (int)BitsBE(b, ref bit, 6);            // target count
        BitsBE(b, ref bit, 4);                              // res_sum (always 0)
        BitsBE(b, ref bit, 4);                              // cmd_no (action type)
        BitsBE(b, ref bit, 32);                             // cmd_arg
        BitsBE(b, ref bit, 32);                             // info (recast)

        // An actor is hostile-to-us if it's a known MOB (not us, not a PC/party member). Friendly actions
        // (our own swings, the WHM's cures targeting us) are thereby excluded from the attacker set.
        bool actorHostile = actor != w.MyId
            && !w.PartyMembers.ContainsKey(actor)
            && (!w.Entities.TryGetValue(actor, out var ae) || ae.IsMob || !ae.TypeKnown);

        for (int t = 0; t < trgSum && t < 15; t++)
        {
            uint tgt = (uint)BitsBE(b, ref bit, 32);
            int resultSum = (int)BitsBE(b, ref bit, 4);
            // Record: a hostile actor that just acted on us or a party member is ATTACKING that target.
            if (actorHostile && (tgt == w.MyId || w.PartyMembers.ContainsKey(tgt)))
                w.Attackers[actor] = (tgt, w.NowMs);
            for (int r = 0; r < resultSum && r < 8; r++)
            {
                // Core result = 85 bits: miss(3) kind(2) sub_kind(12) info(5) scale(5) value(17) message(10) bit(31).
                BitsBE(b, ref bit, 3 + 2 + 12 + 5 + 5);
                int value = (int)BitsBE(b, ref bit, 17);
                int message = (int)BitsBE(b, ref bit, 10);
                BitsBE(b, ref bit, 31);
                // CONFIRMED EFFECTS, not decisions (user rule: a "Cure" log is a decision; the landed heal is
                // this packet). msg 7 = MAGIC_RECOVERS_HP (<target> recovers <value> HP). Recorded so healers
                // can verify their cast actually landed; logged only for us/party (mob self-heals are noise).
                if (message == 7)
                {
                    w.LastHeal = (actor, tgt, value, w.NowMs);
                    if (actor == w.MyId || w.PartyMembers.ContainsKey(actor) || tgt == w.MyId || w.PartyMembers.ContainsKey(tgt))
                        Log.Info($"[land] heal 0x{actor:X} -> 0x{tgt:X} +{value}HP");
                }
                if (BitsBE(b, ref bit, 1) != 0) BitsBE(b, ref bit, 6 + 4 + 17 + 10);   // proc block
                if (BitsBE(b, ref bit, 1) != 0) BitsBE(b, ref bit, 6 + 4 + 14 + 10);   // reaction block
            }
        }
    }

    /// sub = full sub-packet incl 4-byte header. id already decoded.
    /// Only packets whose layouts are verified against the LSB s2c structs are parsed,
    /// so WorldState never holds garbage. Others are ignored until their offsets are confirmed.
    public static void Dispatch(int id, ReadOnlySpan<byte> sub, WorldState w)
    {
        // Harden the parse thread: an unhandled exception in a handler (e.g. a bad offset / short packet)
        // aborts the whole process (SIGABRT/exit 134). Catch + log which packet threw instead of crashing.
        try { DispatchInner(id, sub, w); }
        catch (System.Exception ex) { Log.Always($"[parse] 0x{id:X} threw {ex.GetType().Name}: {ex.Message}"); }
    }

    static void DispatchInner(int id, ReadOnlySpan<byte> sub, WorldState w)
    {
        switch (id)
        {
            case 0x00A: ZoneIn(sub, w); break;     // GP_SERV_COMMAND_LOGIN (zone-in)
            case 0x00D: EntityUpdate(sub, w, isPc: true); break;  // GP_SERV_CHAR_PC (name @0x5A, after GrapIDTbl)
            case 0x00E: EntityUpdate(sub, w, isPc: false); break; // CEntityUpdatePacket (NPC/mob; name @0x34)
            case 0x01B: JobInfo(sub, w); break;    // GP_SERV_COMMAND_JOB_INFO (job/level/maxHP/MP/stats)
            case 0x037: CharStatus(sub, w); break; // CCharStatusPacket (HP%, status, effect icons)
            case 0x0AA: MagicData(sub, w); break;  // GP_SERV_COMMAND_MAGIC_DATA (known-spell bitmap)
            case 0x0DD: GroupMember(sub, w, isAttr: false); break; // group_list: Hpp@body25 (b[29])
            case 0x0DF: GroupMember(sub, w, isAttr: true);  break; // group_attr: NO GAttr field -> Hpp@body18 (b[22])
            case 0x061: CliStatus(sub, w); break;  // GP_SERV_COMMAND_CLISTATUS (max HP/MP, job)
            case 0x028: Battle2(sub, w); break;    // GP_SERV_COMMAND_BATTLE2 (action packet: actor->targets, melee/WS/JA/spell)
            case 0x029: BattleMessage(sub, w); break; // error/result reason codes (engage rejections etc.)
            case 0x032: EventStart(sub, w, false); break; // GP_SERV_COMMAND_EVENT (npc menu/cutscene)
            case 0x034: EventStart(sub, w, true); break;  // GP_SERV_COMMAND_EVENTNUM (event with numeric params)
            case 0x062: Skills2(sub, w); break;           // GP_SERV_COMMAND_CLISTATUS2 (skill levels)
            case 0x01E: ItemNum(sub, w); break;           // GP_SERV_COMMAND_ITEM_NUM (quantity-only update; how gil grants arrive)
            case 0x01F: ItemList(sub, w); break;          // GP_SERV_COMMAND_ITEM_LIST (one inventory item)
            case 0x020: ItemAttr(sub, w); break;          // GP_SERV_COMMAND_ITEM_ATTR (item w/ extdata; different layout)
            case 0x06F: CombineAns(sub, w); break;        // GP_SERV_COMMAND_COMBINE_ANS (synthesis result)
            case 0x04B: PostBoxResult(sub, w); break;     // GP_SERV_COMMAND_PBX_RESULT (delivery box reply/ack)
            case 0x04C: AucResp(sub, w); break;           // GP_SERV_COMMAND_AUC (bid result: Result@6, ItemNo@12)
            case 0x03C: ShopList(sub, w); break;          // GP_SERV_COMMAND_SHOP_LIST (a vendor's items)
            case 0x03D: ShopSell(sub, w); break;          // GP_SERV_COMMAND_SHOP_SELL (sell appraise: Price@4, slot@8)
            case 0x0DC: PartyInvite(sub, w); break;       // GP_SERV_COMMAND_GROUP_SOLICIT_REQ (party invite received; inviter name@0x0C)
            case 0x017: ChatStd(sub, w); break;           // GP_SERV_COMMAND_CHAT_STD (incoming chat; party chat = fleet message bus)
            case 0x105: BazaarItem(sub, w); break;        // GP_SERV_COMMAND_BAZAAR_LIST (one item of a browsed player bazaar)
            case 0x056: QuestMissionLog(sub, w); break;   // GP_SERV_COMMAND_MISSION::OTHER (per-area quest accept/complete bitmaps)
            default:
                // EVERY opcode the server can send is KNOWN (ServerOpcodes, generated from the server's own
                // enum) — nothing arrives as a mystery hex id. An opcode landing here has no field-parser yet:
                // logged ONCE per id BY NAME (user rule: have them all; decide what to parse deliberately,
                // never by guesswork). UNKNOWN_0xXXX = not even in the server enum -> a real protocol surprise.
                if (_unhandledSeen.Add(id)) Log.Info($"[parse] {ServerOpcodes.NameOf(id)} (0x{id:X3}, {sub.Length}B) received — known opcode, no field-parser yet (first sighting)");
                break;
        }
    }

    static readonly HashSet<int> _unhandledSeen = new();

    // 0x0DD/0x0DF group packets: UniqueNo@4, Hp@8, Mp@12, Tp@16, Hpp@22, Mpp@23 (i32/u8).
    // For our own id this is the authoritative source of current absolute HP/MP/TP.
    static void GroupMember(ReadOnlySpan<byte> b, WorldState w, bool isAttr)
    {
        if (b.Length < 24) return;
        uint who = U32(b, 4);
        if (who != w.MyId)
        {
            // A party member's vitals. Record presence + HP/MP — confirms the party formed and feeds healing.
            if (!w.PartyMembers.TryGetValue(who, out var pm)) { pm = new PartyMember { Id = who }; w.PartyMembers[who] = pm; Log.Info($"[party] +member 0x{who:X} (roster now {w.PartyMembers.Count})"); }
            // Hpp/Mpp offset DIFFERS by packet: 0x0DD (group_list) has a 4-byte GAttr field so Hpp@body25 (b[29]);
            // 0x0DF (group_attr) has NO GAttr so Hpp@body18 (b[22]). Parsing 0x0DF at b[29] read garbage (the
            // 0/100 flicker that blocked the WHM from ever curing the WAR). Use the right offset per packet.
            // 0x0DD additionally carries ZoneNo@body28 (b[32]) — the server fills it ONLY when the member is in a
            // DIFFERENT zone, and then it zeroes the HP fields. So on a nonzero ZoneNo we record the member's zone
            // and DON'T write the zeroed vitals (Hpp==0 would read as "dead" when the member merely zoned — the
            // phantom-death regroup bug). 0x0DF is only sent for same-zone members and has no filled zone field.
            if (!isAttr && b.Length >= 34 && U16(b, 32) is ushort zn && zn != 0)
            {
                if (pm.Zone != zn) Log.Info($"[party] member 0x{who:X} is in zone {zn} (not ours)");
                pm.Zone = zn;
                pm.LastSeenMs = w.NowMs;
                return;
            }
            pm.Zone = 0;   // vitals present -> the member is in OUR zone
            int hOff = isAttr ? 22 : 29;
            if (b.Length >= hOff + 2) { pm.Hpp = b[hOff]; pm.Mpp = b[hOff + 1]; }
            pm.LastSeenMs = w.NowMs;
            return;
        }
        // SELF group-list entry (0x0DD only): stamp it — party disband/leave refreshes the roster with a self
        // entry, and consumers detect "refresh that never re-listed the partner" as the partner leaving.
        if (!isAttr) w.SelfGroupListMs = w.NowMs;
        // SELF vitals. Use the AUTHORITATIVE absolute Hp/Mp (b[8]/b[12]) and derive HP%/MP% from them —
        // do NOT trust the packet's Hpp byte (b[22]): these group packets frequently carry Hpp=0 for self
        // even while alive, which was intermittently zeroing w.Hpp -> combat.Dead=true -> the farm kill loop
        // exited instantly (no kills) and the bot thought it was dead (idle/death-loop). Ignore all-zero
        // self-vitals entirely; let 0x037 (CharStatus) own genuine death.
        uint gHp = U32(b, 8);
        if (gHp > 0)
        {
            w.Hp = gHp; w.Mp = U32(b, 12); w.Tp = U32(b, 16);
            if (w.MaxHp > 0) w.Hpp = (byte)System.Math.Min(100, (int)(w.Hp * 100 / w.MaxHp));
            if (w.MaxMp > 0) w.Mpp = (byte)System.Math.Min(100, (int)(w.Mp * 100 / w.MaxMp));
        }
    }

    // 0x017 chat_std (server layout: Kind@body0, Attr@1, Data@2-3, sName@4 (15 bytes), Mes@19 (null-terminated)).
    // We only record PARTY chat (Kind 4/15) — it's relayed cross-zone to all members, so the fleet uses it as a
    // tiny coordination bus (Reunion RALLY handshake). Say/shout/system lines are noise for a headless bot.
    static void ChatStd(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 24) return;
        byte kind = b[4];
        // Party (4/15) = fleet coordination bus. Tell (3) = the PARTYLESS channel: a relogged tank that
        // finds itself without a party can't party-chat the healer, whose roster went stale (phantom
        // party=1 idled both bots ~15 min, three times) — the REFORM handshake rides tells instead.
        // Shout (1) = the zone-wide party-recruitment channel (PartyFinder LFM/LFP).
        if (kind != 4 && kind != 15 && kind != 3 && kind != 1) return;
        string name = Str(b, 8, 15);
        string msg = Str(b, 23, System.Math.Min(150, b.Length - 23));
        if (name.Length == 0 || msg.Length == 0) return;
        if (kind == 3) w.Tells[name] = (msg, w.NowMs);
        else if (kind == 1) w.Shouts[name] = (msg, w.NowMs);
        else w.PartyChat[name] = (msg, w.NowMs);
        Log.Info($"[chat] {(kind == 3 ? "(tell) " : kind == 1 ? "(shout) " : "")}<{name}> {msg}");
    }

    // Null-terminated ASCII string at [off, off+max).
    static string Str(ReadOnlySpan<byte> b, int off, int max)
    {
        if (off >= b.Length) return "";
        int end = off;
        while (end < b.Length && end < off + max && b[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(b[off..end]);
    }

    // s2c 0x105 BAZAAR_LIST — one item of the player bazaar we're browsing (server layout: Price@body0 u32,
    // ItemNum@4 u32, TaxRate@8 u16, ItemNo@10 u16, ItemIndex@12 u8). Feeds the bot-to-bot item handoff.
    static void BazaarItem(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 17) return;
        uint price = U32(b, 4);
        uint num = U32(b, 8);
        ushort itemNo = U16(b, 14);
        byte idx = b[16];
        w.BazaarList[idx] = (itemNo, price, num);
        Log.Info($"[bazaar] browse: slot {idx} = item {itemNo} x{num} @ {price}g");
    }

    // 0x0AA magic_data: MagicDataTbl (m_SpellList bitmap) right after the 4-byte header.
    static void MagicData(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length <= 4) return;
        w.KnownSpellBits = b[4..].ToArray();
    }

    // 0x00D/0x00E entity update (entity_update.cpp, GP_SERV_CHAR_NPC): UniqueNo@4, ActIndex@8,
    // updatemask@0x0A (POS=0x01,HP=0x04,NAME=0x08). POS: rot@0x0B, x@0x0C, y@0x10(vert), z@0x14.
    // HP block: HPP@0x1E, mobHpFlag@0x25, namePrefix@0x27, allegiance@0x29. NAME: ascii@0x34.
    // Fields are only present when their mask bit is set, so gate each block on the mask and let
    // type/allegiance persist across pos-only updates.
    static void EntityUpdate(ReadOnlySpan<byte> b, WorldState w, bool isPc)
    {
        if (b.Length < 0x0B) return;
        uint id = U32(b, 4);
        if (!w.Entities.TryGetValue(id, out var e)) { e = new Entity { Id = id }; w.Entities[id] = e; }
        byte mask = b[0x0A];
        if (b.Length >= 10) e.Index = U16(b, 8);

        if ((mask & 0x01) != 0 && b.Length >= 0x18)        // UPDATE_POS
        {
            e.Rotation = b[0x0B];
            e.X = F32(b, 0x0C); e.Y = F32(b, 0x10); e.Z = F32(b, 0x14);
        }
        if ((mask & 0x04) != 0 && b.Length > 0x29)         // UPDATE_HP -> hp% (+ NPC type/allegiance)
        {
            e.Hpp = b[0x1E];
            if (!isPc)                                     // 0x27/0x29 are NPC-layout fields; for a PC those
            {                                              // bytes are Flags1/2 bits — garbage as type data
                e.NamePrefix = b[0x27];
                e.Allegiance = b[0x29];
                e.TypeKnown = true;
            }
        }
        // UPDATE_NAME: the ascii name offset DIFFERS by packet. NPC (0x00E): @0x34. PC (GP_SERV_CHAR_PC
        // 0x00D): @0x5A — after CostumeId/Flags4-6/GrapIDTbl[9] (server char_update.cpp struct). Reading PCs
        // at 0x34 yielded garbage -> PC names NEVER populated -> InviteIfPresent found nobody (live trio bug).
        int nameOff = isPc ? 0x5A : 0x34;
        if ((mask & 0x08) != 0 && b.Length > nameOff)
        {
            int end = nameOff;
            while (end < b.Length && end < nameOff + 16 && b[end] != 0) end++;
            if (end > nameOff) e.Name = System.Text.Encoding.ASCII.GetString(b[nameOff..end]);
        }
        e.LastSeenMs = w.NowMs;
    }

    // 0x01b JOB_INFO (s2c/0x01b_job_info.h, GP_MYROOM_DANCER): mjob@8, sjob@11,
    // job_lev[16]@16 (level per job), bp_base[7]@32 (STR..CHR), hpmax@60(i32), mpmax@64(i32).
    static void JobInfo(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 68) return;
        w.MainJob = b[8];
        w.SubJob = b[11];
        if (w.MainJob < 16) w.MainJobLevel = b[16 + w.MainJob];
        if (w.SubJob < 16) w.SubJobLevel = b[16 + w.SubJob];
        for (byte j = 1; j < 16; j++) w.JobLevels[j] = b[16 + j];   // full per-job table (seesaw leveling reads it)
        for (int i = 0; i < 7; i++) w.Stats[i] = U16(b, 32 + i * 2);
        w.MaxHp = (int)U32(b, 60);
        w.MaxMp = (int)U32(b, 64);
    }

    // 0x037 CCharStatusPacket (GP_SERV_SERVERSTATUS, char_status.cpp): BufStatus[32]@4,
    // UniqueNo@36, Flags0@40 (uint32 bitfield; hpp = bits 16-23), server_status@48.
    static void CharStatus(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 52) return;
        uint who = U32(b, 36);
        if (who != w.MyId) return;            // only track our own char for now
        uint flags0 = U32(b, 40);
        w.Hpp = (byte)((flags0 >> 16) & 0xFF);
        w.VisibleGmLevel = (byte)((flags0 >> 29) & 0x7);   // flags0_t.GmLevel:3 (top bits) — !togglegm visibility
        w.ServerStatus = b[48];
        b.Slice(4, 32).CopyTo(w.StatusIcons);
    }

    // 0x0DC GP_SERV_COMMAND_GROUP_SOLICIT_REQ: another PC invited us to a party. Inviter name is sName@0x0C
    // (16 bytes, not always null-terminated). Flag the pending invite; the brain answers with 0x074.
    static void PartyInvite(ReadOnlySpan<byte> b, WorldState w)
    {
        w.PartyInvitePending = true;
        if (b.Length >= 0x1C) w.PartyInviterName = System.Text.Encoding.ASCII.GetString(b.Slice(0x0C, 16)).Split('\0')[0].Trim();
    }

    // 0x00A GP_SERV_COMMAND_LOGIN — zone-in. Body offsets (after 4-byte GP_SERV_HEADER), from
    // s2c/0x00a_login.h GP_SERV_POS_HEAD{ UniqueNo@4, ActIndex@8, pad@10, dir@11, x@12, z@16, y@20 }.
    // y is the VERTICAL axis in LSB; z is horizontal. (The "small value = vertical" heuristic is
    // unreliable and mislabeled this before — proven via NavMesh.NearestDetour: only x@12,z@16,y@20
    // lands on the mesh.) WorldState is (X, Y=vertical, Z) to match the navmesh + entity_update.
    static void ZoneIn(ReadOnlySpan<byte> b, WorldState w)
    {
        w.MyId = U32(b, 4);
        if (b.Length >= 10) w.MyIndex = U16(b, 8);   // ActIndex = our targid, for self-targeted job abilities
        if (b.Length >= 24)
        {
            if (Environment.GetEnvironmentVariable("XI_DEBUG") == "1")
                Log.Info($"    [0x00A floats] x@12={F32(b,12):F3} z@16={F32(b,16):F3} y@20={F32(b,20):F3}");
            w.X = F32(b, 12); w.Z = F32(b, 16); w.Y = F32(b, 20);
        }
        // Zone id at offset 48. Reflect it as-is (including 0) — masking a 0 hid that a homepoint with no
        // home point set warps the char into zone-0 limbo. ZoneNo=0 here = unset home point / limbo.
        if (b.Length >= 52) w.ZoneId = (ushort)U32(b, 48);
        Log.Info($"[zone-in] len={b.Length} zone@48={w.ZoneId}");
        w.InZone = true;
    }

    // Event start. 0x32 EVENT: UniqueNo@4, ActIndex@8, EventNum@10, EventPara(id)@12.
    // 0x34 EVENTNUM: UniqueNo@4, num[8]@8 (32B), ActIndex@40, EventNum@42, EventPara(id)@44.
    static void EventStart(ReadOnlySpan<byte> b, WorldState w, bool num)
    {
        int idxOff = num ? 40 : 8, idOff = num ? 44 : 12;
        if (b.Length < idOff + 2) return;
        w.EventNpcId = U32(b, 4);
        w.EventNpcIndex = U16(b, idxOff);
        w.EventId = U16(b, idOff);
        w.EventActive = true;
        Log.Info($"[event] start id={w.EventId} npc=0x{w.EventNpcId:X}#{w.EventNpcIndex} (0x{(num ? 0x34 : 0x32):x2})");
    }

    // 0x062 GP_SERV_COMMAND_CLISTATUS2: CommandRecast[31]@4 (124B), then skill_base[64] (u16) @128.
    // skill_base[id]: bits 0-14 = level, bit 15 = capped. id: 1=H2H,3=Sword,5=Axe,32=Divine..39=Ninjutsu.
    static void Skills2(ReadOnlySpan<byte> b, WorldState w)
    {
        const int off = 128;
        if (b.Length < off + 64 * 2) return;
        for (int i = 0; i < 64; i++) w.Skills[i] = U16(b, off + i * 2);
    }

    // 0x06F GP_SERV_COMMAND_COMBINE_ANS: Result@4(u8), Count@6(u8), ItemNo@8(u16). The authoritative
    // synth-complete signal (success/fail/break); arrives at the end of the synth animation.
    static readonly string[] _synthResult = { "Success", "Failed", "Interrupted", "CancelBadRecipe", "Cancel", "?5", "SkillTooLow", "CancelRareItem" };
    static void CombineAns(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 10) return;
        byte res = b[4];
        w.SynthItemNo = U16(b, 8);
        w.SynthCount = b[6];
        w.SynthResult = res;
        string name = res < _synthResult.Length ? _synthResult[res] : res == 13 ? "MustWaitLonger" : res == 14 ? "InterruptedCritical" : $"code{res}";
        Log.Info($"[synth] result={name} item={w.SynthItemNo} x{w.SynthCount}");
    }

    // 0x01E GP_SERV_COMMAND_ITEM_NUM: ItemNum(qty)@4, Category(container)@8, ItemIndex(slot)@9. Quantity-only
    // update (no item id) — gil grants and stack-count changes arrive here. (container 0, slot 0) is gil.
    static void ItemNum(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 10) return;
        uint qty = U32(b, 4); byte container = b[8], slot = b[9];
        if (container == 0 && slot == 0) { w.Gil = qty; return; }   // LOC_INVENTORY slot 0 = gil
        // Stack-merge/consume deltas arrive here (the 0x03A sort sends one per affected slot). Ignoring
        // them left GHOST slots: the bag read 22/30 after a sort that had actually emptied 7 slots.
        var key = (container, slot);
        if (qty == 0) { w.Inventory.Remove(key); w.InventoryQty.Remove(key); }
        else if (w.Inventory.ContainsKey(key)) w.InventoryQty[key] = (ushort)qty;
    }

    // Shared inventory upsert for the (itemid-bearing) 0x01F/0x020 packets: resolve the (container,slot) key,
    // remove on a zeroed qty/itemid, else set both the id and the quantity. 0x01E (ItemNum) does NOT use this —
    // it's quantity-only (no itemid) and only touches slots that already exist, so it keeps its own body.
    static void UpsertInventory(WorldState w, byte container, byte slot, ushort itemId, uint qty)
    {
        var key = (container, slot);
        if (qty == 0 || itemId == 0) { w.Inventory.Remove(key); w.InventoryQty.Remove(key); }
        else { w.Inventory[key] = itemId; w.InventoryQty[key] = (ushort)qty; }
    }

    // 0x01F GP_SERV_COMMAND_ITEM_LIST: ItemNum(qty)@4, ItemNo(itemid)@8, Category(container)@10, ItemIndex(slot)@11.
    static void ItemList(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 12) return;
        uint qty = U32(b, 4); ushort itemId = U16(b, 8); byte container = b[10], slot = b[11];
        if (itemId == 65535) w.Gil = qty;   // gil is the item at inv slot 0; qty = the gil balance
        UpsertInventory(w, container, slot, itemId, qty);
    }

    // 0x020 GP_SERV_COMMAND_ITEM_ATTR: ItemNum(qty)@4, Price@8, ItemNo(itemid)@12, Category@14, ItemIndex(slot)@15.
    // (Different layout than 0x01F — it has an extra Price field. Inventory is sent across BOTH packets.)
    static void ItemAttr(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 16) return;
        uint qty = U32(b, 4); ushort itemId = U16(b, 12); byte container = b[14], slot = b[15];
        if (itemId == 65535) w.Gil = qty;   // gil balance
        UpsertInventory(w, container, slot, itemId, qty);
    }

    // 0x04C GP_SERV_COMMAND_AUC: the server's reply to our 0x04E Bid. Command@4, Result@6, ItemNo@12.
    // Result: 0x01 bought, 0xC5 no listing at/below bid, 0xE5 inventory full OR rare item already owned.
    static void AucResp(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 14) return;
        w.AucResultItem = U16(b, 12);
        w.AucResult = b[6];
        string meaning = b[6] == 0x01 ? "bought" : b[6] == 0xC5 ? "no-listing" : b[6] == 0xE5 ? "inventory-full/rare" : $"0x{b[6]:X2}";
        Log.Info($"[auc] result={meaning} item={w.AucResultItem}");
    }

    // 0x03D GP_SERV_COMMAND_SHOP_SELL: reply to our 0x084 SHOP_SELL_REQ. Price@4(u32), PropertyItemIndex@8(u8).
    // Sent ONLY when the item is sellable (server stages it); the price confirms we can then send 0x085.
    static void ShopSell(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 9) return;
        w.SellAppraiseSlot = b[8];
        w.SellAppraisePrice = (int)U32(b, 4);
        Log.Info($"[sell-appraise] slot {b[8]} price {U32(b, 4)}");
    }

    // 0x029 GP_SERV_COMMAND_BATTLE_MESSAGE (s2c/0x029): UniqueNoCas@4, UniqueNoTar@8, Data@12,
    // Data2@16, ActIndexCas@20, ActIndexTar@22, MessageNum@24 (the reason/result code). Logging it
    // surfaces WHY an action (e.g. engage) was rejected instead of guessing.
    // 0x04B GP_SERV_COMMAND_PBX_RESULT — delivery box reply: Command@4, BoxNo@5, PostWorkNo(slot)@6.
    // The server replies on a SUCCESSFUL action only, so recording (cmd<<8)|slot lets Delivery confirm
    // a Set landed (and thus that the outgoing slot was free + the receiver resolved).
    static void PostBoxResult(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 7) return;
        w.DboxAck = (b[4] << 8) | b[6];
    }

    // 0x03C GP_SERV_COMMAND_SHOP_LIST — hdr(4) ShopItemOffsetIndex@4 Flags@6 pad@7, then GP_SHOP[N]@8,
    // each 12 bytes: ItemPrice@0(u32) ItemNo@4(u16) ShopIndex@6(u8). N = (len-8)/12. May arrive in
    // chunks; we just upsert each entry by its shop slot index (IShop clears Shop before opening).
    static void ShopList(ReadOnlySpan<byte> b, WorldState w)
    {
        for (int o = 8; o + 12 <= b.Length; o += 12)
        {
            uint price = U32(b, o);
            ushort itemId = U16(b, o + 4);
            byte idx = b[o + 6];
            if (itemId != 0) w.Shop[idx] = (itemId, price);
        }
    }

    // Skill id -> display name (server SKILLTYPE enum), for skill-up logging.
    static readonly Dictionary<int, string> _skillNames = new()
    {
        // melee/ranged (1-31)
        [1] = "H2H", [2] = "Dagger", [3] = "Sword", [4] = "GreatSword", [5] = "Axe", [6] = "GreatAxe",
        [7] = "Scythe", [8] = "Polearm", [9] = "Katana", [10] = "GreatKatana", [11] = "Club", [12] = "Staff",
        [25] = "Archery", [26] = "Marksmanship", [27] = "Throwing", [28] = "Guard", [29] = "Evasion",
        [30] = "Shield", [31] = "Parry",
        // magic (32-45)
        [32] = "Divine", [33] = "Healing", [34] = "Enhancing", [35] = "Enfeebling", [36] = "Elemental",
        [37] = "Dark", [38] = "Summoning", [39] = "Ninjutsu", [40] = "Singing", [41] = "StringInstr",
        [42] = "WindInstr", [43] = "BlueMagic", [44] = "Geomancy", [45] = "Handbell",
        // crafts (48-56)
        [48] = "Fishing", [49] = "Woodworking", [50] = "Smithing", [51] = "Goldsmithing",
        [52] = "Clothcraft", [53] = "Leathercraft", [54] = "Bonecraft", [55] = "Alchemy", [56] = "Cooking",
    };
    static string SkillName(int id) => _skillNames.TryGetValue(id, out var n) ? n : $"skill{id}";

    static void BattleMessage(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 26) return;
        ushort msg = U16(b, 24);
        uint tar = U32(b, 8), data = U32(b, 12), data2 = U32(b, 16);
        // SKILL_GAIN (38): "<target>'s <skill> skill rises X points." param=skillID, value=amount (0.1-level
        // units). Fires on every weapon/magic/craft skill-up; accumulate so brains can report gains.
        if (msg == 38 && data < 64)
        {
            w.SkillGains[(int)data] += (int)data2;
            Log.Info($"[skill-up] {SkillName((int)data)} +{data2 / 10.0:0.0} (session +{w.SkillGains[(int)data] / 10.0:0.0})");
            return;
        }
        // /check reply (0x0DD): param(Data)=mob level, value(Data2)=64+difficulty. Capture it when
        // we're awaiting a con for this target (Data2 in the check range).
        if (w.ConTargetId != 0 && w.ConDifficulty < 0 && tar == w.ConTargetId && data2 is >= 64 and <= 71)
        {
            w.ConMobLevel = (byte)data;
            w.ConDifficulty = (int)(data2 - 64);
            Log.Info($"[con] mob 0x{tar:X} level={w.ConMobLevel} difficulty={w.ConDifficulty}");
            return;
        }
        Log.Info($"[battle-msg] num={msg} cas=0x{U32(b, 4):X} tar=0x{tar:X} data={data} data2={data2}");
    }

    // 0x056 GP_SERV_COMMAND_MISSION::OTHER (s2c/0x056_mission_other.h): PacketData{ uint32 Data[8]@4 (256-bit
    // bitmap), uint16 Port@36 (the LogType) }. For quests the server memcpy's the raw m_questLog[area].current
    // (QuestOffer ports 0x50-0x88…) or .complete (QuestComplete ports 0x90-0xC8…) bitmap into Data. Quest N is
    // set when Data[N/8] bit (N%8) is 1. We store the bitmap per port so the bot can READ accept/complete state.
    static readonly Dictionary<ushort, string> _questLogName = new()
    {
        [0x50] = "SandOria(active)", [0x58] = "Bastok(active)", [0x60] = "Windurst(active)", [0x68] = "Jeuno(active)",
        [0x70] = "OtherAreas(active)", [0x78] = "Outlands(active)", [0x80] = "AhtUrghan(active)", [0x88] = "CrystalWar(active)",
        [0xE0] = "Abyssea(active)", [0xF0] = "Adoulin(active)", [0x100] = "Coalition(active)",
        [0x90] = "SandOria(done)", [0x98] = "Bastok(done)", [0xA0] = "Windurst(done)", [0xA8] = "Jeuno(done)",
        [0xB0] = "OtherAreas(done)", [0xB8] = "Outlands(done)", [0xC0] = "AhtUrghan(done)", [0xC8] = "CrystalWar(done)",
        [0xE8] = "Abyssea(done)", [0xF8] = "Adoulin(done)", [0x108] = "Coalition(done)",
    };
    static void QuestMissionLog(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 38) return;
        ushort port = U16(b, 36);
        if (!_questLogName.TryGetValue(port, out var name)) return; // a mission-log variant (not a quest port)
        var bits = b.Slice(4, 32).ToArray();
        w.QuestLog[port] = bits;
        var set = new List<int>();
        for (int n = 0; n < 256; n++) if ((bits[n >> 3] & (1 << (n & 7))) != 0) set.Add(n);
        Log.Info($"[quest-log] {name} 0x{port:X}: {(set.Count == 0 ? "none" : string.Join(",", set))}");
    }

    // 0x061 GP_SERV_COMMAND_CLISTATUS — CLISTATUS{ hpmax@4(i32), mpmax@8(i32), mjob_no@12(u8), mjob_lv@13(u8),
    // sjob_no@14, sjob_lv@15, exp_now@16(i16), exp_next@18(i16) }. exp_now/exp_next give level progress.
    static void CliStatus(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 14) return;
        w.MaxHp = (int)U32(b, 4);
        w.MaxMp = (int)U32(b, 8);
        w.MainJob = b[12];
        w.MainJobLevel = b[13];
        if (b.Length >= 20)
        {
            w.ExpNow = U16(b, 16);
            w.ExpNext = U16(b, 18);
            Log.Always($"[exp] lvl {w.MainJobLevel}: {w.ExpNow}/{w.ExpNext}");
        }
    }
}
