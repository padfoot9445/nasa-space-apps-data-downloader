namespace FireData.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args) => Commands.RunAsync(args);
}
