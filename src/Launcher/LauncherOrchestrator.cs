using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static class LauncherOrchestrator
{
    public static async Task<ScenarioResult> RunScenarioAsync(Scenario scenario, string nodeDll, string workDir)
    {
        using var listener = new TcpListener(IPAddress.Loopback, scenario.OrchestratorPort);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reportTask = CollectReportsAsync(listener, scenario.N, cts.Token);

        var nodeProcesses = new List<NodeProcess>();
        try
        {
            for (var i = 0; i < scenario.N; i++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{nodeDll}\" --n {scenario.N} --index {i} --basePort {scenario.BasePort} --orchPort {scenario.OrchestratorPort}",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var process = Process.Start(psi);
                if (process is null)
                {
                    return ScenarioResult.Fail(Array.Empty<int>());
                }

                nodeProcesses.Add(new NodeProcess(
                    process,
                    process.StandardOutput.ReadToEndAsync(),
                    process.StandardError.ReadToEndAsync()));
            }

            var reports = await reportTask;
            var gotAllReports = reports.Count == scenario.N;
            var expectedUids = nodeProcesses.Select(np => np.Process.Id).ToArray();
            var expectedWinner = expectedUids.Length == 0 ? -1 : expectedUids.Max();

            foreach (var node in nodeProcesses)
            {
                var exited = node.Process.WaitForExit(5000);
                if (!exited)
                {
                    try { node.Process.Kill(true); } catch { }
                }
            }

            var allExited = nodeProcesses.All(p => p.Process.HasExited && p.Process.ExitCode == 0);
            var winnerValues = reports.Select(r => r.Winner).Distinct().ToArray();
            var sameWinner = winnerValues.Length == 1;
            var winner = sameWinner ? winnerValues[0] : -1;
            var winnerIsExpected = winner == expectedWinner;

            var totalMessages = reports.Sum(r => r.Messages);
            var rounds = reports.Count == 0 ? 0 : reports.Max(r => r.Rounds);
            if (reports.Any(r => r.Uid == winner))
            {
                rounds = reports.First(r => r.Uid == winner).Rounds;
            }

            var ok = gotAllReports && allExited && sameWinner && winnerIsExpected;
            if (!ok)
            {
                var errors = await Task.WhenAll(nodeProcesses.Select(n => n.Stderr));
                var nonEmpty = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();
                if (nonEmpty.Length > 0)
                {
                    Console.Error.WriteLine("NodeErrors:");
                    Console.Error.WriteLine(string.Join(Environment.NewLine, nonEmpty));
                }
            }

            return new ScenarioResult(expectedUids, winner, rounds, totalMessages, ok);
        }
        finally
        {
            foreach (var node in nodeProcesses.Where(p => !p.Process.HasExited))
            {
                try { node.Process.Kill(true); } catch { }
            }

            listener.Stop();
        }
    }

    public static async Task<bool> BuildNodeAsync(string nodeProject, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{nodeProject}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(await stdout);
            Console.Error.WriteLine(await stderr);
            return false;
        }

        return true;
    }

    public static string? ResolveNodeDllPath(string repoRoot)
    {
        var debugDir = Path.Combine(repoRoot, "src", "Node", "bin", "Debug");
        if (!Directory.Exists(debugDir))
        {
            return null;
        }

        var tfmDir = Directory.GetDirectories(debugDir, "net*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (tfmDir is null)
        {
            return null;
        }

        var dllPath = Path.Combine(tfmDir, "Node.dll");
        return File.Exists(dllPath) ? dllPath : null;
    }

    public static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var slnPath = Path.Combine(dir.FullName, "HSLeaderElection.sln");
            if (File.Exists(slnPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static async Task<List<Report>> CollectReportsAsync(TcpListener listener, int expected, CancellationToken ct)
    {
        var reports = new List<Report>();
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        while (!ct.IsCancellationRequested && reports.Count < expected)
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
                continue;
            }

            using (client)
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    continue;
                }

                NodeMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<NodeMessage>(line, json);
                }
                catch
                {
                    continue;
                }

                if (msg is null || msg.Type != "REPORT" || msg.Winner is null || msg.Rounds is null || msg.Messages is null)
                {
                    continue;
                }

                reports.Add(new Report(msg.Uid, msg.Winner.Value, msg.Rounds.Value, msg.Messages.Value));
            }
        }

        return reports;
    }
}
