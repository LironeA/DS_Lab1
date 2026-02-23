sealed class NodeConfig
{
    public required int N { get; init; }
    public required int Index { get; init; }
    public required int BasePort { get; init; }
    public required int OrchestratorPort { get; init; }
    public required int Uid { get; init; }

    public int MyPort => BasePort + Index;
    public int LeftIndex => (Index - 1 + N) % N;
    public int RightIndex => (Index + 1) % N;

    public static NodeConfig? Parse(string[] args)
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

        if (!TryRead(dict, "n", out var n) ||
            !TryRead(dict, "index", out var index) ||
            !TryRead(dict, "basePort", out var basePort) ||
            !TryRead(dict, "orchPort", out var orchPort))
        {
            return null;
        }

        return new NodeConfig
        {
            N = n,
            Index = index,
            Uid = Environment.ProcessId,
            BasePort = basePort,
            OrchestratorPort = orchPort
        };
    }

    private static bool TryRead(Dictionary<string, string> dict, string key, out int value)
    {
        if (dict.TryGetValue(key, out var raw) && int.TryParse(raw, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}

record NetMessage
{
    public string Type { get; init; } = string.Empty;
    public int Uid { get; init; }
    public int? Phase { get; init; }
    public int? Ttl { get; init; }
    public string? Dir { get; init; }
    public int? Winner { get; init; }
    public int? Rounds { get; init; }
    public long? Messages { get; init; }
    public int? SenderIndex { get; init; }
}

record InboundEnvelope(NetMessage Message, Side SourceSide);

sealed class AckState
{
    public bool Left;
    public bool Right;
}

enum Side
{
    Unknown,
    Left,
    Right
}
