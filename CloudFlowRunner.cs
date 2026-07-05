using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FlowLauncher;

static class CloudFlowRunner
{
    public static async Task<int> RunAsync(IConfiguration config, Action<string, string> log, Action<string, Exception?> logError, FlowSummary summary)
    {
        var triggerUrl = config["Flow:HttpTriggerUrl"];
        var flowName = config["Flow:Name"];
        var timeoutMinutes = config.GetValue<int?>("Flow:TimeoutMinutes") ?? 5;
        summary.TriggerUrl = triggerUrl;

        if (string.IsNullOrWhiteSpace(triggerUrl))
        {
            logError("Configuration missing: Flow:HttpTriggerUrl is required for cloud flows.", null);
            Console.WriteLine("  ❌ Configuration missing: Flow:HttpTriggerUrl is required for cloud flows.");
            return 1;
        }

        Console.WriteLine($"  📋 Flow:     {flowName ?? "Cloud Flow"}");
        Console.WriteLine($"  🔧 Type:     Cloud");
        Console.WriteLine($"  ⏱️  Timeout:  {timeoutMinutes} min");
        Console.WriteLine();

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
            summary.FlowIdentifier = flowName ?? "Cloud Flow";
            summary.HttpStatusCode = (int)response.StatusCode;
            summary.HasAsyncPolling = response.StatusCode == System.Net.HttpStatusCode.Accepted && response.Headers.Location != null;

            Console.WriteLine("  🚀 Cloud flow triggered");
            Console.WriteLine();

            var showProgress = config.GetValue<bool?>("Flow:ShowProgress") ?? true;
            if (showProgress && response.StatusCode == System.Net.HttpStatusCode.Accepted && response.Headers.Location != null)
            {
                await PollStatus(response.Headers.Location.ToString(), timeoutMinutes, log, summary);
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

    static async Task PollStatus(string statusUrl, int timeoutMinutes, Action<string, string> log, FlowSummary summary)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        var startTime = DateTime.UtcNow;
        var lastStatus = string.Empty;

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────────────────────┐");
        Console.WriteLine("  │          CLOUD FLOW STATUS MONITOR          │");
        Console.WriteLine("  └─────────────────────────────────────────────┘");
        Console.WriteLine();

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
                catch (JsonException) { }

                if (!string.Equals(status, lastStatus, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  📊 Status: {status}");
                    log("Information", $"Cloud flow status changed: {status}");
                    lastStatus = status;
                }
                else
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    Console.Write($"\r  ⏳ Status: {status} │ elapsed {elapsed:mm\\:ss}    ");
                }

                if (status is "Succeeded" or "Failed" or "Cancelled" or "Aborted")
                {
                    Console.WriteLine();
                    var icon = status == "Succeeded" ? "✅" : "❌";
                    Console.WriteLine($"  {icon} Cloud flow reached terminal state: {status}");
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
        Console.WriteLine($"  ⚠️  {timeoutMsg}");
        log("Warning", timeoutMsg);
        summary.Warnings.Add(timeoutMsg);
    }
}
