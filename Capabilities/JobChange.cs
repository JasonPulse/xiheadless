using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Change main/support job. Must be done inside the Mog House (or a zone with a Nomad Moogle) — the
/// brain gets there via IDelivery.EnterMogHouse first. The support-job slot only works once that job
/// is unlocked (the subjob-unlock quest), and the server rejects locked jobs.
public interface IJobChange
{
    // mainJob/supportJob are JOBTYPE ids (see Job); 0 leaves that slot unchanged. Waits for the server
    // to apply it (0x1B JOB_INFO). Returns true once WorldState reflects the requested job(s).
    Task<bool> ChangeJob(byte mainJob, byte supportJob, CancellationToken ct = default);
}

/// JOBTYPE ids (server enum), so job changes read by name.
public static class Job
{
    public const byte None = 0, War = 1, Mnk = 2, Whm = 3, Blm = 4, Rdm = 5, Thf = 6, Pld = 7, Drk = 8,
                      Bst = 9, Brd = 10, Rng = 11, Sam = 12, Nin = 13, Drg = 14, Smn = 15, Blu = 16,
                      Cor = 17, Pup = 18, Dnc = 19, Sch = 20, Geo = 21, Run = 22;
}

/// Builds 0x100 GP_CLI_COMMAND_MYROOM_JOB: hdr(4) MainJobIndex@4 SupportJobIndex@5. 8 bytes = 2 words
/// (PacketSize[0x100]=0x04 -> 8 bytes). 0 in a slot = leave it unchanged.
internal static class JobPacket
{
    public static byte[] Build(byte mainJob, byte supportJob)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x100 | (2 << 9)));
        p[4] = mainJob;
        p[5] = supportJob;
        return p;
    }
}

public sealed class JobChange(ISession s, IDelivery delivery) : IJobChange
{
    public async Task<bool> ChangeJob(byte mainJob, byte supportJob, CancellationToken ct = default)
    {
        // Need Moogle-menu access. A zone with an Explorer/Nomad Moogle (MISC_MOGMENU) allows it
        // directly; otherwise enter the Mog House first (any city has one or the other).
        ushort zone = s.State.ZoneId;
        bool entered = false;
        if (!Game.Zonelines.HasMogMenu(zone))
        {
            Console.WriteLine($"[job] zone {zone} has no Explorer Moogle — entering Mog House");
            if (!await delivery.EnterMogHouse(ct)) { Console.WriteLine("[job] no Moogle access here — move to a city"); return false; }
            entered = true;
        }

        Console.WriteLine($"[job] requesting main={mainJob} sub={supportJob}");
        s.Enqueue(JobPacket.Build(mainJob, supportJob));
        bool ok = false;
        for (int t = 0; t < 8000 && !ok; t += 200)   // server reapplies + resends 0x1B JOB_INFO
        {
            await Task.Delay(200, ct);
            bool mainOk = mainJob == 0 || s.State.MainJob == mainJob;
            bool subOk = supportJob == 0 || s.State.SubJob == supportJob;
            if (mainOk && subOk) { Console.WriteLine($"[job] now {s.State.MainJob}/{s.State.SubJob}"); ok = true; }
        }
        // Always leave the Mog House if we entered it — otherwise we strand the bot inside (zone 0),
        // where it can't route out and every later login starts stuck in the Mog House.
        if (entered) { await delivery.ExitMogHouse(ct); Console.WriteLine("[job] exited Mog House"); }
        if (!ok) Console.WriteLine($"[job] not confirmed (now {s.State.MainJob}/{s.State.SubJob}) — job locked?");
        return ok;
    }
}
