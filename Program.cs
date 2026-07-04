using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowLauncher;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var basePath = AppContext.BaseDirectory;
        var configPath = Path.Combine(basePath, "appsettings.json");

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddCommandLine(args)
            .AddEnvironmentVariables();

        var configuration = configurationBuilder.Build();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        var logPath = configuration["Logging:LogPath"];
        var fileLogger = !string.IsNullOrWhiteSpace(logPath)
            ? new FileLogger(logPath)
            : null;

        void Log(string level, string message)
        {
            logger.LogInformation(message);
            fileLogger?.Write(level, message);
        }

        void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                logger.LogError(ex, message);
            else
                logger.LogError(message);
            fileLogger?.Write("Error", message + (ex != null ? $" | Exception: {ex.Message}" : ""));
        }

        try
        {
            Log("Information", $"FlowLauncher started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("Information", $"Base directory: {basePath}");
            Log("Information", $"Config file exists: {File.Exists(configPath)} | Path: {configPath}");

            var flowType = configuration["Flow:Type"]?.Trim();
            if (string.IsNullOrWhiteSpace(flowType))
            {
                LogError("Configuration missing: Flow:Type must be set to 'Desktop' or 'Cloud'.");
                return 1;
            }

            flowType = flowType.ToLowerInvariant();
            Log("Information", $"Flow type: {flowType}");

            if (flowType == "desktop")
                return await RunDesktopFlow(configuration, Log, LogError);
            if (flowType == "cloud")
                return await RunCloudFlow(configuration, Log, LogError);

            LogError($"Unknown Flow:Type '{flowType}'. Use 'Desktop' or 'Cloud'.");
            return 1;
        }
        catch (Exception ex)
        {
            LogError("Unhandled exception occurred.", ex);
            return 1;
        }
        finally
        {
            fileLogger?.Dispose();
        }
    }

    static async Task<int> RunDesktopFlow(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError)
    {
        var flowName = config["Flow:Name"];
        var padPath = config["Flow:PadConsoleHostPath"]
            ?? @"C:\Program Files (x86)\Power Automate Desktop\PAD.Console.Host.exe";
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 30;

        if (string.IsNullOrWhiteSpace(flowName))
        {
            logError("Configuration missing: Flow:Name is required for desktop flows.", null);
            return 1;
        }

        log("Information", $"PAD console host path: {padPath}");
        log("Information", $"PAD console host exists: {File.Exists(padPath)}");

        if (!File.Exists(padPath))
        {
            logError($"PAD.Console.Host.exe not found at '{padPath}'. Install Power Automate Desktop or set Flow:PadConsoleHostPath.", null);
            return 1;
        }

        var arguments = $"-run \"{flowName}\"";

        var inputs = config.GetSection("Flow:Inputs").Get<Dictionary<string, string?>>();
        if (inputs != null && inputs.Count > 0)
        {
            var inputsJson = JsonSerializer.Serialize(inputs);
            arguments += $" -inputs {inputsJson}";
            log("Information", $"Flow inputs: {inputsJson}");
        }

        log("Information", $"Starting desktop flow '{flowName}' with timeout {timeoutMinutes} minutes.");
        log("Information", $"Process arguments: {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = padPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(padPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = psi };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                errorBuilder.AppendLine(e.Data);
        };

        var started = process.Start();
        if (!started)
        {
            logError("Failed to start PAD.Console.Host.exe process.", null);
            return 1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutMs = timeoutMinutes * 60 * 1000;
        var completed = process.WaitForExit(timeoutMs);

        if (!completed)
        {
            logError($"Desktop flow '{flowName}' timed out after {timeoutMinutes} minutes.", null);
            try { process.Kill(); } catch { /* ignored */ }
            return 1;
        }

        var output = outputBuilder.ToString().Trim();
        var error = errorBuilder.ToString().Trim();

        if (!string.IsNullOrWhiteSpace(output))
            log("Information", $"PAD stdout: {output}");
        if (!string.IsNullOrWhiteSpace(error))
            log("Warning", $"PAD stderr: {error}");

        log("Information", $"PAD exit code: {process.ExitCode}");

        if (process.ExitCode != 0)
        {
            logError($"Desktop flow '{flowName}' exited with code {process.ExitCode}.", null);
            return process.ExitCode;
        }

        log("Information", $"Desktop flow '{flowName}' completed successfully.");
        return 0;
    }

    static async Task<int> RunCloudFlow(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError)
    {
        var triggerUrl = config["Flow:HttpTriggerUrl"];
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 5;

        if (string.IsNullOrWhiteSpace(triggerUrl))
        {
            logError("Configuration missing: Flow:HttpTriggerUrl is required for cloud flows.", null);
            return 1;
        }

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);

        var headers = config.GetSection("Flow:HttpHeaders").Get<Dictionary<string, string?>>();
        if (headers != null)
        {
            foreach (var header in headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    log("Information", $"Added HTTP header: {header.Key}");
                }
            }
        }

        HttpContent? content = null;
        var payloadSection = config.GetSection("Flow:HttpPayload");
        if (payloadSection.Exists())
        {
            var payload = payloadSection.Get<Dictionary<string, object?>>();
            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload);
                content = new StringContent(json, Encoding.UTF8, "application/json");
                log("Information", $"HTTP payload: {json}");
            }
        }

        log("Information", $"Triggering cloud flow at: {triggerUrl}");

        try
        {
            using var response = content != null
                ? await client.PostAsync(triggerUrl, content)
                : await client.PostAsync(triggerUrl, null);

            var responseBody = await response.Content.ReadAsStringAsync();
            log("Information", $"HTTP status: {(int)response.StatusCode} {response.StatusCode}");
            log("Information", $"HTTP response: {responseBody}");

            if (!response.IsSuccessStatusCode)
            {
                logError($"Cloud flow trigger failed. Status: {(int)response.StatusCode} {response.StatusCode}, Body: {responseBody}", null);
                return 1;
            }

            log("Information", "Cloud flow triggered successfully.");
            return 0;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || client.Timeout.TotalMilliseconds > 0)
        {
            logError($"Cloud flow request timed out after {timeoutMinutes} minutes.", ex);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            logError($"HTTP request failed: {ex.Message}", ex);
            return 1;
        }
    }
}

public class FileLogger : IDisposable
{
    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileLogger(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(path, append: true, encoding: Encoding.UTF8);
        _writer.AutoFlush = true;
    }

    public void Write(string level, string message)
    {
        _lock.Wait();
        try
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _lock.Dispose();
    }
}
