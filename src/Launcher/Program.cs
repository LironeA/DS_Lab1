var parsed = LauncherArgs.Parse(args);
var scenarios = BuildScenarios(parsed);

var repoRoot = LauncherOrchestrator.FindRepoRoot(AppContext.BaseDirectory);
if (repoRoot is null)
{
    Console.Error.WriteLine("SelfCheck=FAIL (repo root not found)");
    return 1;
}

var nodeProject = Path.Combine(repoRoot, "src", "Node", "Node.csproj");
if (!await LauncherOrchestrator.BuildNodeAsync(nodeProject, repoRoot))
{
    Console.Error.WriteLine("SelfCheck=FAIL (Node build failed)");
    return 1;
}

var nodeDll = LauncherOrchestrator.ResolveNodeDllPath(repoRoot);
if (nodeDll is null)
{
    Console.Error.WriteLine("SelfCheck=FAIL (Node.dll not found after build)");
    return 1;
}

var allOk = true;
foreach (var scenario in scenarios)
{
    var result = await LauncherOrchestrator.RunScenarioAsync(scenario, nodeDll, repoRoot);
    allOk &= result.Success;

    Console.WriteLine($"N={scenario.N}");
    Console.WriteLine($"UIDs=[{string.Join(",", result.Uids)}]");
    Console.WriteLine($"WinnerUID={result.WinnerUid}");
    Console.WriteLine($"Rounds={result.Rounds}");
    Console.WriteLine($"TotalMessages={result.TotalMessages}");
    Console.WriteLine($"SelfCheck={(result.Success ? "PASS" : "FAIL")}");
    Console.WriteLine();
}

Console.WriteLine(allOk ? "OverallSelfCheck=PASS" : "OverallSelfCheck=FAIL");
return allOk ? 0 : 1;

static IReadOnlyList<Scenario> BuildScenarios(LauncherArgs parsed)
{
    var selectedN = parsed.N ?? PromptProcessCount();
    if (selectedN == 0)
    {
        return new[]
        {
            new Scenario(10, 51000, 41000),
            new Scenario(20, 52000, 42000),
            new Scenario(50, 53000, 43000),
            new Scenario(100, 54000, 44000),
            new Scenario(200, 55000, 45000)
        };
    }

    return new[] { new Scenario(selectedN, parsed.BasePort, parsed.OrchestratorPort) };
}

static int PromptProcessCount()
{
    while (true)
    {
        Console.Write("Enter process count N (0 = run default scenarios): ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out var n) && n >= 0)
        {
            return n;
        }

        Console.WriteLine("Invalid input. Please enter a non-negative integer.");
    }
}