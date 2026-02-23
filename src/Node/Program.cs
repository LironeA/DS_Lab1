var config = NodeConfig.Parse(args);
if (config is null)
{
    Console.Error.WriteLine("Usage: --n <int> --index <int> --basePort <int> --orchPort <int>");
    return 1;
}

var runtime = new NodeRuntime(config);
await runtime.RunAsync();
return 0;
