using System.Diagnostics;

readonly record struct Scenario(int N, int BasePort, int OrchestratorPort);

readonly record struct ScenarioResult(int[] Uids, int WinnerUid, int Rounds, long TotalMessages, bool Success)
{
    public static ScenarioResult Fail(int[] uids) => new(uids, -1, 0, 0, false);
}

readonly record struct Report(int Uid, int Winner, int Rounds, long Messages);
readonly record struct NodeProcess(Process Process, Task<string> Stdout, Task<string> Stderr);

sealed record LauncherArgs
{
    public int? N { get; init; }
    public int BasePort { get; init; } = 50000;
    public int OrchestratorPort { get; init; } = 40000;

    public static LauncherArgs Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            dict[key[2..]] = args[i + 1];
        }

        int? n = null;
        var basePort = 50000;
        var orchPort = 40000;

        if (dict.TryGetValue("n", out var nRaw) && int.TryParse(nRaw, out var nParsed))
        {
            n = nParsed;
        }

        if (dict.TryGetValue("basePort", out var bpRaw) && int.TryParse(bpRaw, out var bpParsed))
        {
            basePort = bpParsed;
        }

        if (dict.TryGetValue("orchPort", out var opRaw) && int.TryParse(opRaw, out var opParsed))
        {
            orchPort = opParsed;
        }

        return new LauncherArgs
        {
            N = n,
            BasePort = basePort,
            OrchestratorPort = orchPort
        };
    }
}

sealed record NodeMessage
{
    public string Type { get; init; } = string.Empty;
    public int Uid { get; init; }
    public int? Winner { get; init; }
    public int? Rounds { get; init; }
    public long? Messages { get; init; }
}