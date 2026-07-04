namespace FlowLauncher;

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
        var duration = Duration;
        var resultIcon = ExitCode == 0 ? "✅" : "❌";
        var resultText = ExitCode == 0 ? "SUCCESS" : "FAILED";

        Console.WriteLine();
        Console.WriteLine("  ╔═════════════════════════════════════════════╗");
        Console.WriteLine("  ║          FLOW EXECUTION SUMMARY             ║");
        Console.WriteLine("  ╚═════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"  {resultIcon} Result:          {resultText} (exit code {ExitCode})");
        Console.WriteLine($"  📋 Flow:           {FlowIdentifier ?? "N/A"}");
        Console.WriteLine($"  🔧 Type:           {FlowType ?? "Unknown"}");
        Console.WriteLine($"  🕐 Started:        {StartTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  🏁 Ended:          {(EndTime.HasValue ? EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "N/A")}");
        Console.WriteLine($"  ⏱️  Duration:        {duration:hh\\:mm\\:ss}");
        Console.WriteLine("  ───────────────────────────────────────────────");

        if (FlowType?.Equals("desktop", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine();
            Console.WriteLine("  📦 Desktop Flow Details:");
            Console.WriteLine($"     PAD Path:       {PadPath ?? "N/A"}");
            Console.WriteLine($"     Run ID:         {RunId ?? "N/A"}");
            Console.WriteLine($"     Dispatched:     {(DispatchSucceeded ? "✅ Yes" : "❌ No")}");
            Console.WriteLine($"     Dialog Confirmed: {(DialogAutoConfirmed ? "✅ Yes (auto)" : "❌ No")}");
            Console.WriteLine($"     Registry:       {(ConfirmationChanged ? "Modified" : "Unchanged")}");
            Console.WriteLine($"     PAD Restarted:  {(PadRestarted ? "Yes" : "No")}");
            if (!string.IsNullOrWhiteSpace(RunFolderPath))
                Console.WriteLine($"     Log Folder:     {RunFolderPath}");
        }
        else if (FlowType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine();
            Console.WriteLine("  ☁️ Cloud Flow Details:");
            Console.WriteLine($"     Trigger URL:    {TriggerUrl ?? "N/A"}");
            Console.WriteLine($"     HTTP Status:    {HttpStatusCode?.ToString() ?? "N/A"}");
            Console.WriteLine($"     Async Polling:  {(HasAsyncPolling ? "Yes" : "No")}");
            if (!string.IsNullOrWhiteSpace(FinalAsyncStatus))
                Console.WriteLine($"     Final Status:   {FinalAsyncStatus}");
        }

        if (Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  ❌ Errors ({Errors.Count}):");
            foreach (var error in Errors)
                Console.WriteLine($"     • {error}");
        }

        if (Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  ⚠️  Warnings ({Warnings.Count}):");
            foreach (var warning in Warnings)
                Console.WriteLine($"     • {warning}");
        }

        Console.WriteLine();
        Console.WriteLine("  ═══════════════════════════════════════════════");
        Console.WriteLine();
    }
}
