using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Change main/support job. Must be done inside the Mog House (or a zone with a Nomad Moogle) — the
/// brain gets there via IDelivery.EnterMogHouse first. The support-job slot only works once that job
/// is unlocked (the subjob-unlock quest), and the server rejects locked jobs.
public interface IJobChange
{
    // mainJob/supportJob are JOBTYPE ids (see Job); 0 leaves that slot unchanged. Waits for the server
    // to apply it (0x1B JOB_INFO). Returns true once WorldState reflects the requested job(s).
    Task<bool> ChangeJob(byte mainJob, byte supportJob, CancellationToken ct = default);
}
