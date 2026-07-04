using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace FlowLauncher;

static class DesktopFlowRunner
{
    public static async Task<int> RunAsync(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError, FlowSummary summary)
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
            Console.WriteLine("  ❌ PAD.Console.Host.exe not found.");
            return 1;
        }

        Console.WriteLine($"  📋 Flow:     {flowName ?? workflowId}");
        Console.WriteLine($"  🔧 Type:     Desktop");
        Console.WriteLine($"  ⏱️  Timeout:  {timeoutMinutes} min");
        Console.WriteLine();

        var confirmationChanged = ConfigureExternalRunConfirmation(config, log);
        summary.ConfirmationChanged = confirmationChanged;
        summary.PadRestarted = forceRestartPad;

        if (confirmationChanged || forceRestartPad)
            EnsurePadRestarted(forceRestartPad, log);

        log("Information", $"PAD console host path: {padPath}");
        summary.PadPath = padPath;

        var runUrl = BuildRunUrl(config, workflowId, flowName, environmentId, autoLogin, runId, log);

        log("Information", $"Starting desktop flow with timeout {timeoutMinutes} minutes.");
        log("Information", $"Run URL: {runUrl}");

        var cmdLine = $"/c \"\"{padPath}\" \"{runUrl}\"\"";
        log("Information", $"Command: cmd {cmdLine}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdLine,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(padPath) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = psi };

        var started = process.Start();
        if (!started)
        {
            logError("Failed to start PAD.Console.Host.exe with the run URI.", null);
            return 1;
        }

        await Task.Delay(2000);

        var flowLabel = flowName ?? workflowId;
        summary.FlowIdentifier = flowLabel;
        summary.DispatchSucceeded = true;
        summary.PadPath = padPath;

        log("Information", $"Desktop flow '{flowLabel}' dispatched to PAD.Console.Host.exe.");
        Console.WriteLine("  🚀 Flow dispatched to Power Automate Desktop");
        Console.WriteLine();

        Task? autoConfirmTask = null;
        if (autoConfirmDialog)
        {
            autoConfirmTask = Task.Run(async () => await DialogAutoConfirm.RunAsync(60, log, summary));
        }

        if (showProgress)
            await ShowProgress(runId!, progressTimeoutMinutes, log, summary);

        if (autoConfirmTask != null)
            await autoConfirmTask;

        return 0;
    }

    static string BuildRunUrl(IConfiguration config, string? workflowId, string? flowName, string? environmentId, bool autoLogin, string runId, Action<string, string> log)
    {
        var queryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(workflowId))
            queryParts.Add($"workflowId={workflowId}");
        else
            queryParts.Add($"workflowName={flowName}");

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

                if (bool.TryParse(raw, out var boolValue))
                {
                    jsonObject[entry.Key] = boolValue;
                    continue;
                }

                try
                {
                    jsonObject[entry.Key] = System.Text.Json.Nodes.JsonNode.Parse(raw);
                }
                catch
                {
                    jsonObject[entry.Key] = raw;
                }
            }

            if (jsonObject.Count > 0)
            {
                var inputsJson = jsonObject.ToJsonString();
                var escapedInputs = inputsJson.Replace("\"", "\\\"");
                queryParts.Add($"inputArguments={escapedInputs}");
                log("Information", $"Flow inputs: {inputsJson}");
            }
        }

        if (autoLogin)
            queryParts.Add("autologin=true");

        queryParts.Add($"runId={runId}");

        return "ms-powerautomate:/console/flow/run?" + string.Join("&", queryParts);
    }

    static async Task ShowProgress(string runId, int timeoutMinutes, Action<string, string> log, FlowSummary summary)
    {
        var scriptsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Power Automate Desktop", "Console", "Scripts");

        log("Information", $"Watching for run folder under '{scriptsRoot}' (RunId: {runId})");

        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        string? runFolder = null;

        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(scriptsRoot))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(scriptsRoot, "*", SearchOption.AllDirectories))
                    existingFolders.Add(d);
            }
            catch (IOException) { }
        }

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────────────────────┐");
        Console.WriteLine("  │          FLOW PROGRESS MONITOR              │");
        Console.WriteLine("  └─────────────────────────────────────────────┘");
        Console.WriteLine();

        while (DateTime.UtcNow < deadline && runFolder == null)
        {
            if (Directory.Exists(scriptsRoot))
            {
                try
                {
                    runFolder = Directory.EnumerateDirectories(scriptsRoot, runId, SearchOption.AllDirectories).FirstOrDefault();

                    if (runFolder == null)
                    {
                        foreach (var d in Directory.EnumerateDirectories(scriptsRoot, "*", SearchOption.AllDirectories))
                        {
                            if (!existingFolders.Contains(d))
                            {
                                runFolder = d;
                                log("Information", $"Found new run folder: {runFolder}");
                                break;
                            }
                        }
                    }
                }
                catch (IOException) { }
            }

            if (runFolder != null)
                break;

            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            Console.Write($"\r  ⏳ Waiting for flow to start... {Math.Max(0, remaining):F0}s remaining   ");
            await Task.Delay(2000);
        }

        Console.WriteLine();

        if (runFolder == null)
        {
            var msg = "Could not locate the run log folder within the timeout window. The flow may still be running in the background.";
            Console.WriteLine($"  ⚠️  {msg}");
            log("Warning", msg);
            summary.Warnings.Add(msg);
            return;
        }

        Console.WriteLine($"  ✅ Run folder detected");
        Console.WriteLine($"  📁 {runFolder}");
        Console.WriteLine();
        Console.WriteLine("  ┌─ Flow Actions ──────────────────────────────┐");

        log("Information", $"Run folder detected: {runFolder}");
        summary.RunFolderPath = runFolder;
        var actionsLogPath = Path.Combine(runFolder, "Actions.log");
        long lastPosition = 0;
        var startTime = DateTime.UtcNow;
        var lastLogTime = DateTime.UtcNow;
        int stepCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists(runFolder))
            {
                Console.WriteLine("  └─────────────────────────────────────────────┘");
                Console.WriteLine();
                Console.WriteLine($"  ✅ Flow completed — run folder cleaned up by Power Automate.");
                log("Information", "Run folder was removed - the flow run has finished.");
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

                        log("Information", $"[Flow Progress] {line}");

                        var display = ExtractActionName(line);
                        if (display == null)
                            continue;

                        stepCount++;
                        var time = DateTime.Now.ToString("HH:mm:ss");
                        var truncated = display.Length > 38 ? display[..35] + "..." : display;
                        Console.WriteLine($"  │ #{stepCount,3} │ {time} │ {truncated}");
                        readAnyLine = true;
                        lastLogTime = DateTime.UtcNow;
                    }
                    lastPosition = fs.Position;
                }
                catch (IOException) { }
            }

            if (!readAnyLine)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var sinceLastLog = DateTime.UtcNow - lastLogTime;
                Console.Write($"\r  │ {stepCount} actions completed │ elapsed {elapsed:mm\\:ss} │ idle {sinceLastLog:ss}s    ");
            }

            await Task.Delay(2000);
        }

        Console.WriteLine("  └─────────────────────────────────────────────┘");
        Console.WriteLine();
        var timeoutMsg = $"Timed out after {timeoutMinutes} minutes. The flow may still be running.";
        Console.WriteLine($"  ⚠️  {timeoutMsg}");
        log("Warning", timeoutMsg);
        summary.Warnings.Add(timeoutMsg);
    }

    static string? ExtractActionName(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                foreach (var field in new[] { "actionName", "ActionName", "name", "Name", "action", "Action", "description", "Description", "message", "Message" })
                {
                    if (root.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return val;
                    }
                }
                return null;
            }
            catch (JsonException) { }
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("log ") || lower.StartsWith("timestamp:") ||
            lower.StartsWith("run id:") || lower.StartsWith("flow id:") ||
            lower.StartsWith("machine:") || lower.StartsWith("user:") ||
            lower.StartsWith("---") || lower.StartsWith("==="))
            return null;

        return trimmed;
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
            catch (UnauthorizedAccessException) { }
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
            log("Information", "Disabled Power Automate's 'confirm external flow invocation' dialog via HKCU registry setting (EnableAskBeforeRunningAFlowExternally=0).");
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
        log("Warning", $"PAD processes are currently running: {processNames}.");

        if (!forceRestart)
        {
            log("Information", "Set Flow:ForceRestartPad=true to have FlowLauncher automatically terminate running PAD processes before triggering the flow.");
            return;
        }

        log("Warning", "Flow:ForceRestartPad=true — terminating running PAD processes...");
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

        Thread.Sleep(2000);
        log("Information", "PAD processes terminated. Proceeding with flow dispatch.");
    }
}
