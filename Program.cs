using Microsoft.Extensions.Configuration;

namespace FlowLauncher;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var summary = new FlowSummary();

        var basePath = AppContext.BaseDirectory;
        var configPath = Path.Combine(basePath, "appsettings.json");

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddCommandLine(args)
            .AddEnvironmentVariables();

        var configuration = configurationBuilder.Build();

        var logPath = configuration["Logging:LogPath"];
        var fileLogger = !string.IsNullOrWhiteSpace(logPath)
            ? new FileLogger(logPath)
            : null;

        void Log(string level, string message)
        {
            fileLogger?.Write(level, message);
        }

        void LogError(string message, Exception? ex = null)
        {
            fileLogger?.Write("Error", message + (ex != null ? $" | Exception: {ex.Message}" : ""));
            summary.Errors.Add(message);
        }

        int exitCode = 1;
        try
        {
            Log("Information", $"FlowLauncher started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("Information", $"Base directory: {basePath}");
            Log("Information", $"Config file exists: {File.Exists(configPath)} | Path: {configPath}");

            Console.WriteLine();
            Console.WriteLine("  ╔═════════════════════════════════════════════╗");
            Console.WriteLine("  ║              FLOW LAUNCHER                   ║");
            Console.WriteLine("  ╚═════════════════════════════════════════════╝");
            Console.WriteLine();

            var flowType = configuration["Flow:Type"]?.Trim();
            if (string.IsNullOrWhiteSpace(flowType))
            {
                LogError("Configuration missing: Flow:Type must be set to 'Desktop' or 'Cloud'.");
                Console.WriteLine("  ❌ Configuration missing: Flow:Type must be set to 'Desktop' or 'Cloud'.");
                exitCode = 1;
            }
            else
            {
                flowType = flowType.ToLowerInvariant();
                summary.FlowType = flowType;
                Log("Information", $"Flow type: {flowType}");

                if (flowType == "desktop")
                    exitCode = await DesktopFlowRunner.RunAsync(configuration, Log, LogError, summary);
                else if (flowType == "cloud")
                    exitCode = await CloudFlowRunner.RunAsync(configuration, Log, LogError, summary);
                else
                {
                    LogError($"Unknown Flow:Type '{flowType}'. Use 'Desktop' or 'Cloud'.");
                    Console.WriteLine($"  ❌ Unknown Flow:Type '{flowType}'. Use 'Desktop' or 'Cloud'.");
                    exitCode = 1;
                }
            }
        }
        catch (Exception ex)
        {
            LogError("Unhandled exception occurred.", ex);
            exitCode = 1;
        }
        finally
        {
            summary.ExitCode = exitCode;
            summary.EndTime = DateTime.Now;
            summary.Print();
            fileLogger?.Dispose();
        }

        return exitCode;
    }
}
