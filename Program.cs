using System.Diagnostics;
using System.Linq;
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
        var workflowId = config["Flow:WorkflowId"];
        var environmentId = config["Flow:EnvironmentId"];
        var autoLogin = config.GetValue<bool?>("Flow:AutoLogin") ?? false;
        var runId = config["Flow:RunId"];
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 30;
        var showProgress = config.GetValue<bool?>("Flow:ShowProgress") ?? true;
        var progressTimeoutMinutes = config.GetValue<int?>("Flow:ProgressTimeoutMinutes") ?? timeoutMinutes;

        if (string.IsNullOrWhiteSpace(runId))
            runId = Guid.NewGuid().ToString();

        if (string.IsNullOrWhiteSpace(flowName) && string.IsNullOrWhiteSpace(workflowId))
        {
            logError("Configuration missing: Flow:Name or Flow:WorkflowId is required for desktop flows.", null);
            return 1;
        }

        var padPath = ResolvePadConsoleHostPath(config, log);
        if (padPath == null)
        {
            logError("PAD.Console.Host.exe not found. Install Power Automate Desktop or set Flow:PadConsoleHostPath.", null);
            return 1;
        }

        log("Information", $"PAD console host path: {padPath}");

        // PAD.Console.Host.exe is invoked with a single ms-powerautomate: run URL,
        // NOT with custom flags like -run/-inputs. See:
        // https://learn.microsoft.com/power-automate/desktop-flows/run-desktop-flows-url-shortcuts
        var queryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(workflowId))
            queryParts.Add($"workflowId={workflowId}");
        else
            queryParts.Add($"workflowName={Uri.EscapeDataString(flowName!)}");

        if (!string.IsNullOrWhiteSpace(environmentId))
            queryParts.Add($"environmentid={environmentId}");

        var inputs = config.GetSection("Flow:Inputs").Get<Dictionary<string, object?>>();
        if (inputs != null && inputs.Count > 0)
        {
            var inputsJson = JsonSerializer.Serialize(inputs);
            // The run URL requires the JSON to have its double quotes backslash-escaped.
            var escapedInputs = inputsJson.Replace("\"", "\\\"");
            queryParts.Add($"inputArguments={escapedInputs}");
            log("Information", $"Flow inputs: {inputsJson}");
        }

        if (autoLogin)
            queryParts.Add("autologin=true");

        queryParts.Add($"runId={runId}");

        var runUrl = "ms-powerautomate:/console/flow/run?" + string.Join("&", queryParts);

        log("Information", $"Starting desktop flow with timeout {timeoutMinutes} minutes.");
        log("Information", $"Run URL: {runUrl}");
        log("Information", "Note: PAD.Console.Host.exe hands off to Power Automate and exits immediately; its exit code does not reflect the flow's actual run result. Use 'Flow:RunId' plus the on-disk logs to verify completion if needed.");

        var psi = new ProcessStartInfo
        {
            FileName = padPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(padPath) ?? AppContext.BaseDirectory
        };
        psi.ArgumentList.Add(runUrl);

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

        var flowLabel = flowName ?? workflowId;

        if (process.ExitCode != 0)
        {
            logError($"PAD.Console.Host.exe exited with non-zero code {process.ExitCode} while dispatching flow '{flowLabel}'. This indicates the handoff itself failed (e.g. malformed URL); it does not confirm whether the flow ran.", null);
            return process.ExitCode;
        }

        log("Information", $"Desktop flow '{flowLabel}' dispatched successfully to Power Automate.");

        if (showProgress)
            await ShowDesktopFlowProgress(runId!, progressTimeoutMinutes, log);

        return 0;
    }

    static async Task ShowDesktopFlowProgress(string runId, int timeoutMinutes, Action<string, string> log)
    {
        var scriptsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Power Automate Desktop", "Console", "Scripts");

        log("Information", $"Watching for run folder under '{scriptsRoot}' (RunId: {runId})");

        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        string? runFolder = null;

        while (DateTime.UtcNow < deadline && runFolder == null)
        {
            if (Directory.Exists(scriptsRoot))
            {
                try
                {
                    runFolder = Directory.EnumerateDirectories(scriptsRoot, runId, SearchOption.AllDirectories).FirstOrDefault();
                }
                catch (IOException) { /* transient FS race, retry */ }
            }

            if (runFolder != null)
                break;

            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            Console.Write($"\rWaiting for flow run to start... ({Math.Max(0, remaining):F0}s remaining)   ");
            await Task.Delay(1000);
        }

        Console.WriteLine();

        if (runFolder == null)
        {
            log("Warning", "Could not locate the run log folder within the timeout window; progress display unavailable. The flow may still be running in the background.");
            return;
        }

        log("Information", $"Run folder detected: {runFolder}");
        var actionsLogPath = Path.Combine(runFolder, "Actions.log");
        long lastPosition = 0;
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists(runFolder))
            {
                Console.WriteLine();
                log("Information", "Run folder was removed - the flow run has finished (Power Automate cleans up logs after completion).");
                return;
            }

            var readAnyLine = false;
            if (File.Exists(actionsLogPath))
            {
                try
                {
                    using var fs = new FileStream(actionsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        Console.WriteLine($"[Flow] {line}");
                        log("Information", $"[Flow Progress] {line}");
                        readAnyLine = true;
                    }
                    lastPosition = fs.Position;
                }
                catch (IOException)
                {
                    // Actions.log may be momentarily locked by PAD; retry on next tick.
                }
            }

            if (!readAnyLine)
            {
                var elapsed = DateTime.UtcNow - startTime;
                Console.Write($"\rFlow running... elapsed {elapsed:mm\\:ss}   ");
            }

            await Task.Delay(1000);
        }

        Console.WriteLine();
        log("Warning", $"Timed out after {timeoutMinutes} minutes waiting for the desktop flow run to finish. It may still be running.");
    }

    static string? ResolvePadConsoleHostPath(IConfiguration config, Action<string, string> log)
    {
        var configuredPath = config["Flow:PadConsoleHostPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            log("Information", $"Configured PAD console host path: {configuredPath} | Exists: {File.Exists(configuredPath)}");
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var candidates = new[]
        {
            @"C:\Program Files (x86)\Power Automate Desktop\dotnet\PAD.Console.Host.exe",
            @"C:\Program Files\Power Automate Desktop\dotnet\PAD.Console.Host.exe",
            @"C:\Program Files (x86)\Power Automate Desktop\PAD.Console.Host.exe",
            @"C:\Program Files\Power Automate Desktop\PAD.Console.Host.exe"
        };

        foreach (var candidate in candidates)
        {
            log("Information", $"Probing for PAD console host at: {candidate} | Exists: {File.Exists(candidate)}");
            if (File.Exists(candidate))
                return candidate;
        }

        var windowsAppsRoot = @"C:\Program Files\WindowsApps";
        if (Directory.Exists(windowsAppsRoot))
        {
            try
            {
                var storeMatch = Directory.GetDirectories(windowsAppsRoot, "Microsoft.PowerAutomateDesktop_*")
                    .SelectMany(dir => Directory.GetFiles(dir, "PAD.Console.Host.exe", SearchOption.AllDirectories))
                    .FirstOrDefault();

                if (storeMatch != null)
                {
                    log("Information", $"Found Microsoft Store PAD console host at: {storeMatch}");
                    return storeMatch;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // WindowsApps folder often blocks enumeration without elevated access; ignore and fall through.
            }
        }

        return null;
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

            var showProgress = config.GetValue<bool?>("Flow:ShowProgress") ?? true;
            if (showProgress && response.StatusCode == System.Net.HttpStatusCode.Accepted && response.Headers.Location != null)
            {
                await PollCloudFlowStatus(response.Headers.Location.ToString(), timeoutMinutes, log);
            }
            else if (showProgress)
            {
                log("Information", "No async status URL (202 + Location header) was returned, so live progress polling is unavailable for this trigger. The flow was still dispatched.");
            }

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

    static async Task PollCloudFlowStatus(string statusUrl, int timeoutMinutes, Action<string, string> log)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        var startTime = DateTime.UtcNow;
        var lastStatus = string.Empty;

        log("Information", $"Polling flow run status at: {statusUrl}");

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await client.GetAsync(statusUrl);
                var body = await resp.Content.ReadAsStringAsync();
                var status = "Unknown";

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("status", out var statusProp))
                        status = statusProp.GetString() ?? "Unknown";
                }
                catch (JsonException)
                {
                    // Response body isn't JSON or doesn't contain a status field; keep polling with "Unknown".
                }

                if (!string.Equals(status, lastStatus, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine();
                    Console.WriteLine($"[Flow] Status: {status}");
                    log("Information", $"Cloud flow status changed: {status}");
                    lastStatus = status;
                }
                else
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    Console.Write($"\rFlow status: {status} | elapsed {elapsed:mm\\:ss}   ");
                }

                if (status is "Succeeded" or "Failed" or "Cancelled" or "Aborted")
                {
                    Console.WriteLine();
                    log("Information", $"Cloud flow reached terminal state: {status}");
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                log("Warning", $"Error while polling flow status (will retry): {ex.Message}");
            }

            await Task.Delay(2000);
        }

        Console.WriteLine();
        log("Warning", $"Timed out after {timeoutMinutes} minutes waiting for the cloud flow to reach a terminal state.");
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
