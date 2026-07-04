using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

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
            summary.Errors.Add(message);
        }

        int exitCode = 1;
        try
        {
            Log("Information", $"FlowLauncher started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("Information", $"Base directory: {basePath}");
            Log("Information", $"Config file exists: {File.Exists(configPath)} | Path: {configPath}");

            var flowType = configuration["Flow:Type"]?.Trim();
            if (string.IsNullOrWhiteSpace(flowType))
            {
                LogError("Configuration missing: Flow:Type must be set to 'Desktop' or 'Cloud'.");
                exitCode = 1;
            }
            else
            {
                flowType = flowType.ToLowerInvariant();
                summary.FlowType = flowType;
                Log("Information", $"Flow type: {flowType}");

                if (flowType == "desktop")
                    exitCode = await RunDesktopFlow(configuration, Log, LogError, summary);
                else if (flowType == "cloud")
                    exitCode = await RunCloudFlow(configuration, Log, LogError, summary);
                else
                {
                    LogError($"Unknown Flow:Type '{flowType}'. Use 'Desktop' or 'Cloud'.");
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

    static async Task<int> RunDesktopFlow(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError, FlowSummary summary)
    {
        var flowName = config["Flow:Name"];
        var workflowId = config["Flow:WorkflowId"];
        var environmentId = config["Flow:EnvironmentId"];
        var autoLogin = config.GetValue<bool?>("Flow:AutoLogin") ?? false;
        var runId = config["Flow:RunId"];
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 30;
        var showProgress = config.GetValue<bool?>("Flow:ShowProgress") ?? true;
        var progressTimeoutMinutes = config.GetValue<int?>("Flow:ProgressTimeoutMinutes") ?? timeoutMinutes;
        var forceRestartPad = config.GetValue<bool?>("Flow:ForceRestartPad") ?? false;
        var autoConfirmDialog = config.GetValue<bool?>("Flow:AutoConfirmDialog") ?? true;

        if (string.IsNullOrWhiteSpace(runId))
            runId = Guid.NewGuid().ToString();

        summary.RunId = runId;
        summary.ProgressTimeoutMinutes = progressTimeoutMinutes;

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

        var confirmationChanged = ConfigureExternalRunConfirmation(config, log);
        summary.ConfirmationChanged = confirmationChanged;
        summary.PadRestarted = forceRestartPad;

        if (confirmationChanged || forceRestartPad)
            EnsurePadRestarted(forceRestartPad, log);

        log("Information", $"PAD console host path: {padPath}");
        summary.PadPath = padPath;

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

        var inputEntries = config.GetSection("Flow:Inputs").GetChildren().ToList();
        if (inputEntries.Count > 0)
        {
            var jsonObject = new System.Text.Json.Nodes.JsonObject();
            foreach (var entry in inputEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.Value))
                    continue;

                var raw = entry.Value.Trim();

                // Explicitly handle C#-style booleans before trying JSON parse.
                if (bool.TryParse(raw, out var boolValue))
                {
                    jsonObject[entry.Key] = boolValue;
                    continue;
                }

                try
                {
                    // Try to parse as a JSON literal (number, null, object, array).
                    jsonObject[entry.Key] = System.Text.Json.Nodes.JsonNode.Parse(raw);
                }
                catch
                {
                    // If it isn't valid JSON (e.g. a plain string like "hello"),
                    // treat it as a JSON string value.
                    jsonObject[entry.Key] = raw;
                }
            }

            if (jsonObject.Count > 0)
            {
                var inputsJson = jsonObject.ToJsonString();
                var escapedInputs = Uri.EscapeDataString(inputsJson);
                queryParts.Add($"inputArguments={escapedInputs}");
                log("Information", $"Flow inputs: {inputsJson}");
            }
        }

        if (autoLogin)
            queryParts.Add("autologin=true");

        queryParts.Add($"runId={runId}");

        var runUrl = "ms-powerautomate:/console/flow/run?" + string.Join("&", queryParts);

        log("Information", $"Starting desktop flow with timeout {timeoutMinutes} minutes.");
        log("Information", $"Run URL: {runUrl}");

        // ------------------------------------------------------------------
        // Launch PAD.Console.Host.exe directly with the ms-powerautomate: URI
        // as its argument. UseShellExecute=true ensures the process starts
        // normally (not redirected), which is required for PAD to properly
        // handle the URI and run the flow rather than opening it in edit mode.
        // ------------------------------------------------------------------
        var psi = new ProcessStartInfo
        {
            FileName = padPath,
            Arguments = runUrl,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(padPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = psi };

        var started = process.Start();
        if (!started)
        {
            logError("Failed to start PAD.Console.Host.exe with the run URI.", null);
            return 1;
        }

        // Give PAD a moment to process the URI and start the flow.
        await Task.Delay(2000);

        var flowLabel = flowName ?? workflowId;
        summary.FlowIdentifier = flowLabel;
        summary.DispatchSucceeded = true;
        summary.PadPath = padPath;

        log("Information", $"Desktop flow '{flowLabel}' dispatched to PAD.Console.Host.exe.");

        if (autoConfirmDialog)
        {
            // Launch in parallel so it doesn't block progress display.
            _ = Task.Run(async () => await AutoConfirmRunFlowDialog(60, log, summary));
        }

        if (showProgress)
            await ShowDesktopFlowProgress(runId!, progressTimeoutMinutes, log, summary);

        return 0;
    }

    static async Task ShowDesktopFlowProgress(string runId, int timeoutMinutes, Action<string, string> log, FlowSummary summary)
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
            var msg = "Could not locate the run log folder within the timeout window; progress display unavailable. The flow may still be running in the background.";
            log("Warning", msg);
            summary.Warnings.Add(msg);
            return;
        }

        log("Information", $"Run folder detected: {runFolder}");
        summary.RunFolderPath = runFolder;
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
        var timeoutMsg = $"Timed out after {timeoutMinutes} minutes waiting for the desktop flow run to finish. It may still be running.";
        log("Warning", timeoutMsg);
        summary.Warnings.Add(timeoutMsg);
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

    static bool ConfigureExternalRunConfirmation(IConfiguration config, Action<string, string> log)
    {
        var disableConfirmation = config.GetValue<bool?>("Flow:DisableExternalConfirmation") ?? false;
        if (!disableConfirmation)
            return false;

        const string keyPath = @"SOFTWARE\Microsoft\Power Automate Desktop";

        try
        {
            using (var hklmKey = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                var enforced = hklmKey?.GetValue("ConfigureExternalRuns") as int?;
                if (enforced == 1)
                {
                    log("Warning", "Admin policy (HKLM\\...\\Power Automate Desktop\\ConfigureExternalRuns=1) enforces the confirmation dialog on this machine; it cannot be disabled by FlowLauncher. Contact your Power Platform admin.");
                    return false;
                }
                if (enforced == 2)
                {
                    log("Warning", "Admin policy (HKLM\\...\\Power Automate Desktop\\ConfigureExternalRuns=2) blocks external flow invocation entirely on this machine. The flow will not run.");
                    return false;
                }
            }

            using var hkcuKey = Registry.CurrentUser.CreateSubKey(keyPath);
            var existing = hkcuKey.GetValue("EnableAskBeforeRunningAFlowExternally") as int?;
            if (existing == 0)
            {
                log("Information", "Registry setting EnableAskBeforeRunningAFlowExternally is already 0 (dialog already disabled).");
                return false;
            }

            hkcuKey.SetValue("EnableAskBeforeRunningAFlowExternally", 0, RegistryValueKind.DWord);
            log("Information", "Disabled Power Automate's 'confirm external flow invocation' dialog via HKCU registry setting (EnableAskBeforeRunningAFlowExternally=0). NOTE: this is a machine-wide user setting that persists after this run and lowers the security bar for externally-triggered flows on this account.");
            return true;
        }
        catch (Exception ex)
        {
            log("Warning", $"Failed to update the external-run confirmation registry setting: {ex.Message}");
            return false;
        }
    }

    static void EnsurePadRestarted(bool forceRestart, Action<string, string> log)
    {
        var padProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PAD.Console.Host",
            "PAD.Designer",
            "PowerAutomateDesktop",
            "PowerAutomateDesktop.Console"
        };

        var padProcesses = Process.GetProcesses()
            .Where(p =>
            {
                try { return padProcessNames.Contains(p.ProcessName); }
                catch { return false; }
            })
            .ToList();

        if (padProcesses.Count == 0)
        {
            log("Information", "No PAD processes detected; registry change should take effect immediately.");
            return;
        }

        var processNames = string.Join(", ", padProcesses.Select(p => $"{p.ProcessName} (PID {p.Id})"));
        log("Warning", $"PAD processes are currently running: {processNames}. Power Automate caches the confirmation-dialog setting in memory, so the dialog may still appear unless PAD is restarted.");

        if (!forceRestart)
        {
            log("Information", "Set Flow:ForceRestartPad=true to have FlowLauncher automatically terminate running PAD processes before triggering the flow. This ensures the registry change is picked up, but may interrupt other active flows.");
            return;
        }

        log("Warning", "Flow:ForceRestartPad=true — terminating running PAD processes so the new registry setting takes effect...");
        foreach (var proc in padProcesses)
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(5000);
                log("Information", $"Terminated {proc.ProcessName} (PID {proc.Id}).");
            }
            catch (Exception ex)
            {
                log("Warning", $"Failed to terminate {proc.ProcessName} (PID {proc.Id}): {ex.Message}");
            }
        }

        // Give PAD a moment to fully shut down before we try to start a new flow.
        Thread.Sleep(2000);
        log("Information", "PAD processes terminated. Proceeding with flow dispatch.");
    }

    static async Task<int> RunCloudFlow(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError, FlowSummary summary)
    {
        var triggerUrl = config["Flow:HttpTriggerUrl"];
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 5;
        summary.TriggerUrl = triggerUrl;

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
            summary.HttpStatusCode = (int)response.StatusCode;
            summary.HasAsyncPolling = response.StatusCode == System.Net.HttpStatusCode.Accepted && response.Headers.Location != null;

            var showProgress = config.GetValue<bool?>("Flow:ShowProgress") ?? true;
            if (showProgress && response.StatusCode == System.Net.HttpStatusCode.Accepted && response.Headers.Location != null)
            {
                await PollCloudFlowStatus(response.Headers.Location.ToString(), timeoutMinutes, log, summary);
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

    static async Task PollCloudFlowStatus(string statusUrl, int timeoutMinutes, Action<string, string> log, FlowSummary summary)
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
                    summary.FinalAsyncStatus = status;
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Error while polling flow status (will retry): {ex.Message}";
                log("Warning", msg);
                summary.Warnings.Add(msg);
            }

            await Task.Delay(2000);
        }

        Console.WriteLine();
        var timeoutMsg = $"Timed out after {timeoutMinutes} minutes waiting for the cloud flow to reach a terminal state.";
        log("Warning", timeoutMsg);
        summary.Warnings.Add(timeoutMsg);
    }

    // ------------------------------------------------------------------
    // UI-Automation fallback: auto-confirm PAD dialogs via PowerShell.
    // Uses UIAutomationClient which works with modern WinUI/WPF/WebView2
    // dialogs that Win32 FindWindow cannot reliably detect.
    // ------------------------------------------------------------------

    static async Task AutoConfirmRunFlowDialog(int timeoutSeconds, Action<string, string> log, FlowSummary summary)
    {
        log("Information", $"Auto-confirm: spawning PowerShell UI automation watcher (up to {timeoutSeconds}s).");

        // Write the PowerShell script to a temp file to avoid escaping issues.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"flowlauncher_autoconfirm_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildAutoConfirmPsScript());

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.EnvironmentVariables["AUTO_CONFIRM_TIMEOUT"] = timeoutSeconds.ToString();

            using var ps = new Process { StartInfo = psi };
            ps.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var line = e.Data.Trim();

                if (line.StartsWith("DIALOG_DETECTED|"))
                    log("Information", $"Auto-confirm: detected dialog '{line["DIALOG_DETECTED|".Length..]}'.");
                else if (line.StartsWith("BUTTON_CLICKED|"))
                {
                    log("Information", $"Auto-confirm: clicked button '{line["BUTTON_CLICKED|".Length..]}'.");
                    summary.DialogAutoConfirmed = true;
                }
                else if (line.StartsWith("BUTTON_CLICK_FAILED|"))
                    log("Warning", $"Auto-confirm: button click failed: {line["BUTTON_CLICK_FAILED|".Length..]}");
                else if (line == "ENTER_KEY_SENT")
                {
                    log("Information", "Auto-confirm: Enter key sent as fallback.");
                    summary.DialogAutoConfirmed = true;
                }
                else if (line.StartsWith("ENTER_KEY_FAILED|"))
                    log("Warning", $"Auto-confirm: Enter key failed: {line["ENTER_KEY_FAILED|".Length..]}");
                else if (line == "AUTO_CONFIRM_DONE")
                    log("Information", "Auto-confirm: PowerShell watcher finished.");
            };
            ps.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    log("Warning", $"Auto-confirm PS stderr: {e.Data.Trim()}");
            };

            ps.Start();
            ps.BeginOutputReadLine();
            ps.BeginErrorReadLine();

            await ps.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            log("Warning", $"Auto-confirm: failed to run PowerShell watcher: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { /* ignored */ }
        }
    }

    static string BuildAutoConfirmPsScript() => """
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$confirmed = @{}
$deadline = (Get-Date).AddSeconds($env:AUTO_CONFIRM_TIMEOUT)

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800

    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

    foreach ($win in $windows) {
        $winId = $win.Current.NativeWindowHandle
        if ($confirmed.ContainsKey($winId)) { continue }

        $name = $win.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        $isPadDialog = $false
        $patterns = @('external source','attempting to run','run flow','Run Flow','Power Automate','input','Input','confirm','Confirm','parameters','Parameters','variables','Variables','enter values','Enter values')
        foreach ($p in $patterns) {
            if ($name -like "*$p*") { $isPadDialog = $true; break }
        }
        if (-not $isPadDialog) { continue }

        Write-Host "DIALOG_DETECTED|$name"

        $btnCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCondition)

        $clicked = $false
        foreach ($btn in $buttons) {
            $btnName = $btn.Current.Name
            $invokePatterns = @('Run flow','Run Flow','Run','OK','Confirm','Continue','Yes','Start','Accept','Submit')
            foreach ($ip in $invokePatterns) {
                if ($btnName -like "*$ip*") {
                    try {
                        $invoker = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                        $invoker.Invoke()
                        Write-Host "BUTTON_CLICKED|$btnName"
                        $clicked = $true
                        break
                    } catch {
                        Write-Host "BUTTON_CLICK_FAILED|$btnName|$($_.Exception.Message)"
                    }
                }
            }
            if ($clicked) { break }
        }

        if (-not $clicked) {
            try {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                Write-Host "ENTER_KEY_SENT"
            } catch {
                Write-Host "ENTER_KEY_FAILED|$($_.Exception.Message)"
            }
        }

        $confirmed[$winId] = $true
        Start-Sleep -Milliseconds 1500
    }
}
Write-Host "AUTO_CONFIRM_DONE"
""";
}

public class FlowSummary
{
    public DateTime StartTime { get; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public string? FlowType { get; set; }
    public string? FlowIdentifier { get; set; }
    public int ExitCode { get; set; }

    public string? PadPath { get; set; }
    public string? RunId { get; set; }
    public int? PadExitCode { get; set; }
    public bool DispatchSucceeded { get; set; }
    public bool ConfirmationChanged { get; set; }
    public bool PadRestarted { get; set; }
    public string? RunFolderPath { get; set; }
    public int? ProgressTimeoutMinutes { get; set; }
    public bool DialogAutoConfirmed { get; set; }

    public int? HttpStatusCode { get; set; }
    public string? FinalAsyncStatus { get; set; }
    public string? TriggerUrl { get; set; }
    public bool HasAsyncPolling { get; set; }

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.Now - StartTime;

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("  FLOW EXECUTION SUMMARY");
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"  Flow Type:        {FlowType ?? "Unknown"}");
        Console.WriteLine($"  Flow Identifier:  {FlowIdentifier ?? "N/A"}");
        Console.WriteLine($"  Start Time:       {StartTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  End Time:         {(EndTime.HasValue ? EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "N/A")}");
        Console.WriteLine($"  Duration:         {Duration:hh\\:mm\\:ss}");
        Console.WriteLine($"  Overall Result:   {(ExitCode == 0 ? "SUCCESS" : "FAILED")} (Exit Code: {ExitCode})");
        Console.WriteLine(new string('-', 70));

        if (FlowType?.Equals("desktop", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"  PAD Path:         {PadPath ?? "N/A"}");
            Console.WriteLine($"  Run ID:           {RunId ?? "N/A"}");
            Console.WriteLine($"  PAD Exit Code:    {PadExitCode?.ToString() ?? "N/A"}");
            Console.WriteLine($"  Dispatch Status:  {(DispatchSucceeded ? "Dispatched" : "Failed")}");
            Console.WriteLine($"  Confirmation:     {(ConfirmationChanged ? "Disabled (registry changed)" : "Unchanged")}");
            Console.WriteLine($"  PAD Restarted:    {(PadRestarted ? "Yes" : "No")}");
            Console.WriteLine($"  Dialog Confirmed: {(DialogAutoConfirmed ? "Yes (auto-clicked)" : "No")}");
            if (!string.IsNullOrWhiteSpace(RunFolderPath))
                Console.WriteLine($"  Run Log Folder:   {RunFolderPath}");
        }
        else if (FlowType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"  Trigger URL:      {TriggerUrl ?? "N/A"}");
            Console.WriteLine($"  HTTP Status:      {HttpStatusCode?.ToString() ?? "N/A"}");
            Console.WriteLine($"  Async Polling:    {(HasAsyncPolling ? "Yes" : "No")}");
            if (!string.IsNullOrWhiteSpace(FinalAsyncStatus))
                Console.WriteLine($"  Final Status:     {FinalAsyncStatus}");
        }

        if (Errors.Count > 0)
        {
            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"  ERRORS ({Errors.Count}):");
            foreach (var error in Errors)
                Console.WriteLine($"    - {error}");
        }

        if (Warnings.Count > 0)
        {
            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"  WARNINGS ({Warnings.Count}):");
            foreach (var warning in Warnings)
                Console.WriteLine($"    - {warning}");
        }

        Console.WriteLine(new string('=', 70));
        Console.WriteLine();
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
