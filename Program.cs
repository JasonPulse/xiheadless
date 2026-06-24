// XiHeadless launcher. A bot is configured by exactly THREE env vars; the brain (code) does the rest:
//   XIBOT_ACCOUNT  — FFXI account name
//   XIBOT_PASSWORD — account password (secret)
//   XIBOT_BRAIN    — which brain to run (e.g. War, Rmt); see BrainRegistry (defaults to War)
// The character is auto-selected from the account (fleet = one char per account); the server host
// is fixed in BotHost. Dev only: a numeric first arg bounds the run in seconds (for testing),
// "cleanup" deletes the account's chars, and diagnostic subcommands short-circuit in Diagnostics.

if (Diagnostics.Run(args) is int devCode) return devCode;

string? account = Environment.GetEnvironmentVariable("XIBOT_ACCOUNT");
string? password = Environment.GetEnvironmentVariable("XIBOT_PASSWORD");
string brain = Environment.GetEnvironmentVariable("XIBOT_BRAIN") ?? "War";

if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("set XIBOT_ACCOUNT, XIBOT_PASSWORD, and XIBOT_BRAIN");
    Console.Error.WriteLine("brains: " + string.Join(", ", BrainRegistry.Names));
    return 1;
}

if (args.Length > 0 && args[0] == "cleanup") return await BotHost.Cleanup(account, password);
if (args.Length > 0 && args[0] == "provision") return await BotHost.Provision(account, password);

if (!BrainRegistry.Exists(brain))
{
    Console.Error.WriteLine($"unknown brain '{brain}'. available: " + string.Join(", ", BrainRegistry.Names));
    return 1;
}

int? runSeconds = args.Length > 0 && int.TryParse(args[0], out var rs) ? rs : null; // dev: bound the run
return await BotHost.Run(account, password, brain, runSeconds);
