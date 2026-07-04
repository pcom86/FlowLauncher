# FlowLauncher

A lightweight, self-contained Windows console application for triggering Power Automate flows (Desktop or Cloud) on a schedule via Windows Task Scheduler.

FlowLauncher is designed for organizations that need to automate Power Automate flow execution outside of the Power Automate scheduling UI — for example, integrating with existing Windows Task Scheduler infrastructure, chaining flows into larger automation pipelines, or running flows on machines without requiring users to be logged in interactively at flow-trigger time.

## Features

- **Desktop Flows**: Triggers Power Automate Desktop (PAD) flows via `PAD.Console.Host.exe`
- **Cloud Flows**: Triggers cloud flows via HTTP trigger URLs
- **Self-Contained**: Single-folder deployment with no .NET runtime required on the target machine
- **Configurable**: JSON-based configuration with environment variable and command-line overrides
- **Scheduled**: Includes a PowerShell installer script that registers a Windows Scheduled Task
- **Logging**: Console and file logging for monitoring and troubleshooting

## Requirements

- **Build Machine**: .NET 10 SDK (or later)
- **Target Machine**: Windows 10/11 (x64)
- **For Desktop Flows**: Power Automate Desktop installed
- **For Cloud Flows**: HTTP trigger URL from your cloud flow

## Quick Start

### 1. Build

Open a terminal in the project folder and run:

```powershell
dotnet publish -c Release -r win-x64 --self-contained
```

This produces a single `FlowLauncher.exe` (and supporting files) in:

```
bin\Release\net10.0\win-x64\publish\
```

### 2. Configure

Edit `appsettings.json` to match your flow.

#### Desktop Flow Example

```json
{
  "Flow": {
    "Type": "Desktop",
    "Name": "My Desktop Flow",
    "WorkflowId": "",
    "EnvironmentId": "",
    "AutoLogin": false,
    "RunId": "",
    "PadConsoleHostPath": "",
    "TimeoutMinutes": 30,
    "Inputs": {
      "InputVariable1": "Hello from FlowLauncher"
    }
  },
  "Logging": {
    "LogPath": "C:\\Logs\\FlowLauncher.log"
  }
}
```

> **How desktop flow triggering works**: FlowLauncher invokes `PAD.Console.Host.exe` with a single `ms-powerautomate:/console/flow/run?...` run URL — this is the only invocation format PAD's console host supports. There is **no** synchronous "wait for flow completion" via this method: the host process hands off to Power Automate and exits almost immediately, so a `0` exit code only confirms the *handoff* succeeded, not that the flow finished successfully. To verify actual completion, set `Flow:RunId` to a GUID and inspect the corresponding log folder under `C:\Users\[User]\AppData\Local\Microsoft\Power Automate Desktop\Console\Scripts\[Flow ID]\Runs\[Run ID]`.
>
> Leave `PadConsoleHostPath` empty to let FlowLauncher auto-detect the install location (MSI or Microsoft Store). Set `WorkflowId`/`EnvironmentId` instead of `Name` if you have Premium licensing and prefer ID-based addressing (more stable than flow display names).

#### Cloud Flow Example

```json
{
  "Flow": {
    "Type": "Cloud",
    "HttpTriggerUrl": "https://prod-00.westus.logic.azure.com:443/workflows/...",
    "TimeoutMinutes": 5,
    "HttpHeaders": {
      "x-api-key": "your-api-key"
    },
    "HttpPayload": {
      "source": "FlowLauncher",
      "triggeredAt": "auto"
    }
  },
  "Logging": {
    "LogPath": "C:\\Logs\\FlowLauncher.log"
  }
}
```

### 3. Deploy & Schedule

Run the setup script as Administrator:

```powershell
# Daily at 8:00 AM
.\Setup-ScheduledTask.ps1 -TaskName "Run-My-Flow" -Schedule Daily -StartTime "08:00"

# Every 30 minutes
.\Setup-ScheduledTask.ps1 -TaskName "Run-My-Flow" -Schedule Minute -IntervalMinutes 30

# Weekly on Mon/Wed/Fri at 7:30 AM
.\Setup-ScheduledTask.ps1 -TaskName "Run-My-Flow" -Schedule Weekly -StartTime "07:30" -DaysOfWeek @("Monday","Wednesday","Friday")
```

The script copies the published files to `C:\Program Files\FlowLauncher` and creates the scheduled task.

## Manual Deployment

If you prefer to deploy manually:

1. Copy the contents of `bin\Release\net10.0\win-x64\publish\` to your target folder (e.g., `C:\Tools\FlowLauncher`).
2. Place your `appsettings.json` in that folder.
3. Create a Windows Scheduled Task that runs `FlowLauncher.exe` from that folder.

## Configuration Reference

| Setting | Description | Required |
|---------|-------------|----------|
| `Flow:Type` | `Desktop` or `Cloud` | Yes |
| `Flow:Name` | Desktop flow display name (used unless `WorkflowId` is set) | For Desktop, unless `WorkflowId` set |
| `Flow:WorkflowId` | Desktop flow ID (Premium license required); takes precedence over `Name` | No |
| `Flow:EnvironmentId` | Power Automate environment ID | No |
| `Flow:AutoLogin` | Sign in silently with current Windows account | No (default: false) |
| `Flow:RunId` | GUID used to name the on-disk run log folder | No |
| `Flow:PadConsoleHostPath` | Full path to `PAD.Console.Host.exe`; leave empty to auto-detect | No |
| `Flow:TimeoutMinutes` | Max wait time before abort | No (default: 30 Desktop / 5 Cloud) |
| `Flow:Inputs` | Key-value inputs passed to desktop flow via `inputArguments` | No |
| `Flow:HttpTriggerUrl` | Cloud flow HTTP trigger URL | For Cloud |
| `Flow:HttpHeaders` | Custom headers for HTTP request | No |
| `Flow:HttpPayload` | JSON payload for HTTP POST | No |
| `Logging:LogPath` | Path to log file | No |

## Environment Variable Overrides

Any config value can be overridden via environment variables using double-underscore separators:

```powershell
$env:Flow__Type = "Cloud"
$env:Flow__HttpTriggerUrl = "https://..."
```

## Command-Line Overrides

Pass arguments when running the executable:

```powershell
FlowLauncher.exe --Flow:Type=Desktop --Flow:Name="Another Flow"
```

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Configuration error, timeout, or execution failure |
| Other | Exit code forwarded from `PAD.Console.Host.exe` |

## Troubleshooting

- **PAD.Console.Host.exe not found**: Verify Power Automate Desktop is installed and update `PadConsoleHostPath` in config.
- **Task fails silently**: Check the log file at `C:\Logs\FlowLauncher.log` or run the executable manually from a command prompt.
- **Permission denied**: Ensure the scheduled task runs as a user that has access to Power Automate Desktop and any required network resources.
- **Flow not found**: Use the exact flow name as shown in Power Automate Desktop (case-sensitive).
