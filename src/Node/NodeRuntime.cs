using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

sealed class NodeRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly NodeConfig _config;
    private readonly Channel<InboundEnvelope> _inbox = Channel.CreateUnbounded<InboundEnvelope>();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, AckState> _phaseAcks = new();
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _active = true;
    private int _phase;
    private int? _winnerUid;
    private long _messagesSent;
    private int _roundsForReport;
    private int _completionFlag;

    public NodeRuntime(NodeConfig config)
    {
        _config = config;
    }

    public async Task RunAsync()
    {
        var listenerTask = RunListenerAsync(_cts.Token);
        var processTask = ProcessInboxAsync(_cts.Token);

        // Allow all node listeners to start before the first HS probes.
        await Task.Delay(2000, _cts.Token);
        _ = Task.Run(() => RunPhasesAsync(_cts.Token));

        await _done.Task;

        _cts.Cancel();
        try { await Task.WhenAll(listenerTask, processTask); } catch { }
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _config.MyPort);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8);

                        string? line;
                        while ((line = await reader.ReadLineAsync(ct)) is not null)
                        {
                            NetMessage? msg;
                            try
                            {
                                msg = JsonSerializer.Deserialize<NetMessage>(line, JsonOptions);
                            }
                            catch
                            {
                                continue;
                            }

                            if (msg is null)
                            {
                                continue;
                            }

                            var sourceSide = ResolveSide(msg.SenderIndex);
                            await _inbox.Writer.WriteAsync(new InboundEnvelope(msg, sourceSide), ct);
                        }
                    }
                }, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        while (await _inbox.Reader.WaitToReadAsync(ct))
        {
            while (_inbox.Reader.TryRead(out var envelope))
            {
                await HandleMessageAsync(envelope.Message);
                if (_completionFlag == 1)
                {
                    return;
                }
            }
        }
    }

    private async Task RunPhasesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _active && _winnerUid is null)
        {
            var currentPhase = _phase;
            lock (_lock)
            {
                _phaseAcks[currentPhase] = new AckState();
            }

            var distance = 1 << currentPhase;
            await SendToSideAsync(Side.Left, new NetMessage
            {
                Type = "OUT",
                Uid = _config.Uid,
                Phase = currentPhase,
                Ttl = distance,
                Dir = "L"
            });

            await SendToSideAsync(Side.Right, new NetMessage
            {
                Type = "OUT",
                Uid = _config.Uid,
                Phase = currentPhase,
                Ttl = distance,
                Dir = "R"
            });

            var timeout = TimeSpan.FromSeconds(5);
            var started = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && _winnerUid is null)
            {
                if (IsPhaseComplete(currentPhase))
                {
                    _phase++;
                    _roundsForReport = Math.Max(_roundsForReport, _phase);
                    break;
                }

                if (DateTime.UtcNow - started > timeout)
                {
                    _active = false;
                    return;
                }

                await Task.Delay(25, ct);
            }
        }
    }

    private bool IsPhaseComplete(int phase)
    {
        lock (_lock)
        {
            if (!_phaseAcks.TryGetValue(phase, out var ack))
            {
                return false;
            }

            return ack.Left && ack.Right;
        }
    }

    private async Task HandleMessageAsync(NetMessage message)
    {
        switch (message.Type)
        {
            case "OUT":
                await HandleOutAsync(message);
                break;
            case "IN":
                await HandleInAsync(message);
                break;
            case "ANNOUNCE":
                await HandleAnnounceAsync(message);
                break;
        }
    }

    private async Task HandleOutAsync(NetMessage message)
    {
        if (message.Uid < _config.Uid)
        {
            return;
        }

        if (message.Uid == _config.Uid)
        {
            _winnerUid = _config.Uid;
            _roundsForReport = Math.Max(_roundsForReport, (message.Phase ?? _phase) + 1);

            await SendToSideAsync(Side.Right, new NetMessage
            {
                Type = "ANNOUNCE",
                Uid = _config.Uid,
                Winner = _config.Uid,
                Dir = "R"
            });

            await SendToSideAsync(Side.Left, new NetMessage
            {
                Type = "ANNOUNCE",
                Uid = _config.Uid,
                Winner = _config.Uid,
                Dir = "L"
            });

            await CompleteAsync();
            return;
        }

        var ttl = message.Ttl ?? 0;
        if (ttl > 1)
        {
            await SendInDirectionAsync(message.Dir, new NetMessage
            {
                Type = "OUT",
                Uid = message.Uid,
                Phase = message.Phase,
                Ttl = ttl - 1,
                Dir = message.Dir
            });
            return;
        }

        var backSide = GetReturnSideForIn(message.Dir);
        await SendToSideAsync(backSide, new NetMessage
        {
            Type = "IN",
            Uid = message.Uid,
            Phase = message.Phase,
            Dir = message.Dir
        });
    }

    private async Task HandleInAsync(NetMessage message)
    {
        if (message.Uid == _config.Uid)
        {
            var phase = message.Phase ?? _phase;
            lock (_lock)
            {
                if (!_phaseAcks.TryGetValue(phase, out var ack))
                {
                    ack = new AckState();
                    _phaseAcks[phase] = ack;
                }

                if (message.Dir == "L")
                {
                    ack.Left = true;
                }
                else if (message.Dir == "R")
                {
                    ack.Right = true;
                }
            }

            return;
        }

        var backSide = GetReturnSideForIn(message.Dir);
        await SendToSideAsync(backSide, new NetMessage
        {
            Type = "IN",
            Uid = message.Uid,
            Phase = message.Phase,
            Dir = message.Dir
        });
    }

    private async Task HandleAnnounceAsync(NetMessage message)
    {
        var winner = message.Winner ?? message.Uid;
        _winnerUid ??= winner;

        var announceSide = message.Dir == "L" ? Side.Left : Side.Right;
        await SendToSideAsync(announceSide, new NetMessage
        {
            Type = "ANNOUNCE",
            Uid = message.Uid,
            Winner = winner,
            Dir = message.Dir
        });

        await CompleteAsync();
    }

    private async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completionFlag, 1) == 1)
        {
            return;
        }

        await SendReportAsync();
        _done.TrySetResult(true);
    }

    private async Task SendReportAsync()
    {
        var winner = _winnerUid ?? -1;
        var report = new NetMessage
        {
            Type = "REPORT",
            Uid = _config.Uid,
            Winner = winner,
            Rounds = _roundsForReport,
            Messages = Interlocked.Read(ref _messagesSent)
        };

        await SendTcpMessageAsync(_config.OrchestratorPort, report, retries: 100, retryDelayMs: 100);
    }

    private async Task SendInDirectionAsync(string? dir, NetMessage message)
    {
        var side = dir == "L" ? Side.Left : Side.Right;
        await SendToSideAsync(side, message);
    }

    private async Task SendToSideAsync(Side side, NetMessage message)
    {
        if (side == Side.Unknown)
        {
            return;
        }

        var targetIndex = side == Side.Left ? _config.LeftIndex : _config.RightIndex;
        var targetPort = _config.BasePort + targetIndex;

        var sendMessage = message with { SenderIndex = _config.Index };
        await SendTcpMessageAsync(targetPort, sendMessage, retries: 200, retryDelayMs: 50);
    }

    private static Side GetReturnSideForIn(string? outDir)
    {
        return outDir == "L" ? Side.Right : Side.Left;
    }

    private async Task SendTcpMessageAsync(int port, NetMessage message, int retries, int retryDelayMs)
    {
        var payload = JsonSerializer.Serialize(message, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);

        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = client.GetStream();
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                Interlocked.Increment(ref _messagesSent);
                return;
            }
            catch
            {
                await Task.Delay(retryDelayMs);
            }
        }
    }

    private Side ResolveSide(int? senderIndex)
    {
        if (senderIndex == _config.LeftIndex)
        {
            return Side.Left;
        }

        if (senderIndex == _config.RightIndex)
        {
            return Side.Right;
        }

        return Side.Unknown;
    }
}
