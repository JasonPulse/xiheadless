namespace XiHeadless.Routines;

/// The MAIN/SUB seesaw every advanced-job brain composes (user, 07-04): subjobs earn no exp — the only way
/// to keep a sub at the required HALF of main is to periodically switch the sub job IN as main, level it to
/// main/2, and switch back. Example (PLD/WAR): level PLD; when WAR < ceil(PLD/2), switch main to WAR, grind
/// to the target, switch back to PLD/WAR and resume. Brains supply per-job LevelGrind configs; this routine
/// owns only the switching policy. Job changes need moogle access (JobChange routes via the Mog House).
public sealed class JobLeveling(IPerception p, IJobChange jobs, IZoning zoning)
{
    public sealed class Config
    {
        public byte MainJob;                                  // e.g. Job.Pld
        public byte SubJob;                                   // e.g. Job.War — packet-set (quest-free, server-accepted)
        public byte MainTarget;                               // stop level for the main (0 = open-ended)
        public Func<byte, LevelGrind.Config> GrindCfgFor = null!;   // per-job grind config (gear/cons/abilities), by job id
        public Func<byte, CancellationToken, Task> RunGrind = null!; // runs one LevelGrind session for the given job until its Done
        public string HomeCity = "Windurst_Woods";            // Mog House city for job changes (fleet is Windurst-nation)
        public string Tag = "joblevel";
    }

    void Log(Config c, string m) => Console.WriteLine($"[{c.Tag}] {m}");

    /// The level this character has attained on `job` (server tracks all jobs; 0x1B carries the table).
    public int LevelOf(byte job) => p.World.JobLevels.TryGetValue(job, out var l) ? l : 0;

    /// Required sub level for a given main level (sub caps at ceil(main/2) for full effect).
    public static int SubNeededFor(int mainLevel) => (mainLevel + 1) / 2;

    /// One seesaw decision: returns the job that should be MAIN right now.
    public byte PickPhase(Config c)
    {
        int main = LevelOf(c.MainJob), sub = LevelOf(c.SubJob);
        // The sub must keep up with where the main IS (not where it's going): grind the sub whenever it's
        // below half of the CURRENT main level; hysteresis of +1 so we don't flip every single main level.
        return sub < SubNeededFor(main) ? c.SubJob : c.MainJob;
    }

    /// Drive the seesaw until the main reaches MainTarget (with the sub kept at half throughout).
    public async Task RunAsync(Config c, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int main = LevelOf(c.MainJob), sub = LevelOf(c.SubJob);
            if (c.MainTarget > 0 && main >= c.MainTarget && sub >= SubNeededFor(main))
            {
                Log(c, $"goal met: {c.MainJob} {main} / {c.SubJob} {sub}");
                return;
            }

            byte phase = PickPhase(c);
            // Ensure the character IS main=phase, with the OTHER job as sub. Leveling the main => MainJob/
            // SubJob; leveling the sub-as-main => SubJob-main / MainJob-sub. Passing 0 left the previous sub
            // unchanged, which on the sub-as-main switch produced the ILLEGAL WAR/WAR (main==sub, no bonus) —
            // the server doesn't validate main!=sub, so we must always set the partner explicitly.
            byte partnerSub = phase == c.MainJob ? c.SubJob : c.MainJob;
            if (p.World.MainJob != phase || p.World.SubJob != partnerSub)
            {
                Log(c, $"switching main -> job {phase}/{partnerSub} (main {c.MainJob}={main}, sub {c.SubJob}={sub}, need sub {SubNeededFor(main)})");
                if (!await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, phase, partnerSub, c.HomeCity, ct))
                {
                    Log(c, "job change failed — retrying in 60s (need moogle access?)");
                    await Task.Delay(60_000, ct);
                    continue;
                }
            }
            // Grind one stint as this job. The brain's RunGrind returns when the stint's Done fires
            // (e.g. sub reached half-of-main, or main reached target) — then we re-decide.
            await c.RunGrind(phase, ct);
        }
    }
}
