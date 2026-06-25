using System.Buffers.Binary;

namespace XiHeadless.Game;

/// Parses the inbound sub-packets we care about into WorldState.
/// Offsets are body-relative (after the 4-byte sub-packet header) per the LSB s2c structs.
public static class PacketParsers
{
    static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    /// sub = full sub-packet incl 4-byte header. id already decoded.
    /// Only packets whose layouts are verified against the LSB s2c structs are parsed,
    /// so WorldState never holds garbage. Others are ignored until their offsets are confirmed.
    public static void Dispatch(int id, ReadOnlySpan<byte> sub, WorldState w)
    {
        switch (id)
        {
            case 0x00A: ZoneIn(sub, w); break;     // GP_SERV_COMMAND_LOGIN (zone-in)
            case 0x00D: EntityUpdate(sub, w); break; // CCharUpdatePacket (PC)
            case 0x00E: EntityUpdate(sub, w); break; // CEntityUpdatePacket (NPC/mob)
            case 0x01B: JobInfo(sub, w); break;    // GP_SERV_COMMAND_JOB_INFO (job/level/maxHP/MP/stats)
            case 0x037: CharStatus(sub, w); break; // CCharStatusPacket (HP%, status, effect icons)
            case 0x0AA: MagicData(sub, w); break;  // GP_SERV_COMMAND_MAGIC_DATA (known-spell bitmap)
            case 0x0DD: GroupMember(sub, w); break; // group_list (member HP/MP/TP)
            case 0x0DF: GroupMember(sub, w); break; // group_attr (self/member HP/MP/TP)
            case 0x061: CliStatus(sub, w); break;  // GP_SERV_COMMAND_CLISTATUS (max HP/MP, job)
            case 0x029: BattleMessage(sub, w); break; // error/result reason codes (engage rejections etc.)
            case 0x032: EventStart(sub, w, false); break; // GP_SERV_COMMAND_EVENT (npc menu/cutscene)
            case 0x034: EventStart(sub, w, true); break;  // GP_SERV_COMMAND_EVENTNUM (event with numeric params)
            case 0x062: Skills2(sub, w); break;           // GP_SERV_COMMAND_CLISTATUS2 (skill levels)
            case 0x01F: ItemList(sub, w); break;          // GP_SERV_COMMAND_ITEM_LIST (one inventory item)
            case 0x020: ItemAttr(sub, w); break;          // GP_SERV_COMMAND_ITEM_ATTR (item w/ extdata; different layout)
            case 0x04B: PostBoxResult(sub, w); break;     // GP_SERV_COMMAND_PBX_RESULT (delivery box reply/ack)
            case 0x03C: ShopList(sub, w); break;          // GP_SERV_COMMAND_SHOP_LIST (a vendor's items)
        }
    }

    // 0x0DD/0x0DF group packets: UniqueNo@4, Hp@8, Mp@12, Tp@16, Hpp@22, Mpp@23 (i32/u8).
    // For our own id this is the authoritative source of current absolute HP/MP/TP.
    static void GroupMember(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 24) return;
        uint who = U32(b, 4);
        if (who != w.MyId) return; // TODO: store other party members for healing/assist
        w.Hp = U32(b, 8); w.Mp = U32(b, 12); w.Tp = U32(b, 16);
        w.Hpp = b[22]; w.Mpp = b[23];
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
    static void EntityUpdate(ReadOnlySpan<byte> b, WorldState w)
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
        if ((mask & 0x04) != 0 && b.Length > 0x29)         // UPDATE_HP -> hp% + type/allegiance
        {
            e.Hpp = b[0x1E];
            e.NamePrefix = b[0x27];
            e.Allegiance = b[0x29];
            e.TypeKnown = true;
        }
        if ((mask & 0x08) != 0 && b.Length >= 0x35)        // UPDATE_NAME -> ascii name at 0x34
        {
            int end = 0x34;
            while (end < b.Length && end < 0x34 + 16 && b[end] != 0) end++;
            if (end > 0x34) e.Name = System.Text.Encoding.ASCII.GetString(b[0x34..end]);
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
        w.ServerStatus = b[48];
        b.Slice(4, 32).CopyTo(w.StatusIcons);
    }

    // 0x00A GP_SERV_COMMAND_LOGIN — zone-in. Body offsets (after 4-byte GP_SERV_HEADER), from
    // s2c/0x00a_login.h GP_SERV_POS_HEAD{ UniqueNo@4, ActIndex@8, pad@10, dir@11, x@12, z@16, y@20 }.
    // y is the VERTICAL axis in LSB; z is horizontal. (The "small value = vertical" heuristic is
    // unreliable and mislabeled this before — proven via NavMesh.NearestDetour: only x@12,z@16,y@20
    // lands on the mesh.) WorldState is (X, Y=vertical, Z) to match the navmesh + entity_update.
    static void ZoneIn(ReadOnlySpan<byte> b, WorldState w)
    {
        w.MyId = U32(b, 4);
        if (b.Length >= 24)
        {
            if (Environment.GetEnvironmentVariable("XI_DEBUG") == "1")
                Console.WriteLine($"    [0x00A floats] x@12={F32(b,12):F3} z@16={F32(b,16):F3} y@20={F32(b,20):F3}");
            w.X = F32(b, 12); w.Z = F32(b, 16); w.Y = F32(b, 20);
        }
        if (b.Length >= 52) w.ZoneId = (ushort)U32(b, 48);
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
        Console.WriteLine($"[event] start id={w.EventId} npc=0x{w.EventNpcId:X}#{w.EventNpcIndex} (0x{(num ? 0x34 : 0x32):x2})");
    }

    // 0x062 GP_SERV_COMMAND_CLISTATUS2: CommandRecast[31]@4 (124B), then skill_base[64] (u16) @128.
    // skill_base[id]: bits 0-14 = level, bit 15 = capped. id: 1=H2H,3=Sword,5=Axe,32=Divine..39=Ninjutsu.
    static void Skills2(ReadOnlySpan<byte> b, WorldState w)
    {
        const int off = 128;
        if (b.Length < off + 64 * 2) return;
        for (int i = 0; i < 64; i++) w.Skills[i] = U16(b, off + i * 2);
    }

    // 0x01F GP_SERV_COMMAND_ITEM_LIST: ItemNum(qty)@4, ItemNo(itemid)@8, Category(container)@10, ItemIndex(slot)@11.
    static void ItemList(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 12) return;
        uint qty = U32(b, 4); ushort itemId = U16(b, 8); byte container = b[10], slot = b[11];
        var key = (container, slot);
        if (qty == 0 || itemId == 0) w.Inventory.Remove(key);
        else w.Inventory[key] = itemId;
    }

    // 0x020 GP_SERV_COMMAND_ITEM_ATTR: ItemNum(qty)@4, Price@8, ItemNo(itemid)@12, Category@14, ItemIndex(slot)@15.
    // (Different layout than 0x01F — it has an extra Price field. Inventory is sent across BOTH packets.)
    static void ItemAttr(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 16) return;
        uint qty = U32(b, 4); ushort itemId = U16(b, 12); byte container = b[14], slot = b[15];
        var key = (container, slot);
        if (qty == 0 || itemId == 0) w.Inventory.Remove(key);
        else w.Inventory[key] = itemId;
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

    static void BattleMessage(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 26) return;
        ushort msg = U16(b, 24);
        uint tar = U32(b, 8), data = U32(b, 12), data2 = U32(b, 16);
        // /check reply (0x0DD): param(Data)=mob level, value(Data2)=64+difficulty. Capture it when
        // we're awaiting a con for this target (Data2 in the check range).
        if (w.ConTargetId != 0 && w.ConDifficulty < 0 && tar == w.ConTargetId && data2 is >= 64 and <= 71)
        {
            w.ConMobLevel = (byte)data;
            w.ConDifficulty = (int)(data2 - 64);
            Console.WriteLine($"[con] mob 0x{tar:X} level={w.ConMobLevel} difficulty={w.ConDifficulty}");
            return;
        }
        Console.WriteLine($"[battle-msg] num={msg} cas=0x{U32(b, 4):X} tar=0x{tar:X} data={data} data2={data2}");
    }

    // 0x061 GP_SERV_COMMAND_CLISTATUS — CLISTATUS{ hpmax@4(i32), mpmax@8(i32),
    // mjob_no@12(u8), mjob_lv@13(u8) }.
    static void CliStatus(ReadOnlySpan<byte> b, WorldState w)
    {
        if (b.Length < 14) return;
        w.MaxHp = (int)U32(b, 4);
        w.MaxMp = (int)U32(b, 8);
        w.MainJob = b[12];
        w.MainJobLevel = b[13];
    }
}
