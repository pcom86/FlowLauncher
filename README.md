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
| `Flow:DisableExternalConfirmation` | Suppress the "An external process is trying to start the flow" dialog | No (default: false) |
| `Flow:ForceRestartPad` | Terminate running PAD processes so registry changes take effect | No (default: false) |
| `Flow:AutoConfirmDialog` | Auto-click the PAD confirmation dialog if it still appears | No (default: true) |
| `Flow:PadConsoleHostPath` | Full path to `PAD.Console.Host.exe`; leave empty to auto-detect | No |
| `Flow:TimeoutMinutes` | Max wait time before abort | No (default: 30 Desktop / 5 Cloud) |
| `Flow:ShowProgress` | Display live flow progress on the console | No (default: true) |
| `Flow:ProgressTimeoutMinutes` | Max time to wait/display progress after dispatch (Desktop only) | No (default: `TimeoutMinutes`) |
| `Flow:Inputs` | Key-value inputs passed to desktop flow via `inputArguments` | No |
| `Flow:HttpTriggerUrl` | Cloud flow HTTP trigger URL | For Cloud |
| `Flow:HttpHeaders` | Custom headers for HTTP request | No |
| `Flow:HttpPayload` | JSON payload for HTTP POST | No |
| `Logging:LogPath` | Path to log file | No |

## Disabling the External Invocation Confirmation Dialog

By default, Power Automate Desktop shows a **"Run flow: An external source is attempting to run the following flow..."** dialog whenever a flow is triggered via URL (which is how FlowLauncher invokes desktop flows). This blocks unattended/scheduled execution.

### Step 1: Disable the registry setting

Set `Flow:DisableExternalConfirmation` to `true`. FlowLauncher will write:

| Hive | Key | Name | Value |
|---|---|---|---|
| `HKEY_CURRENT_USER` | `SOFTWARE\Microsoft\Power Automate Desktop` | `EnableAskBeforeRunningAFlowExternally` | `0` (DWORD) |

This is equivalent to disabling **Display confirmation dialog when invoking flows externally** in the PAD console settings, and is the officially documented mechanism (see [Governance in Power Automate for desktop](https://learn.microsoft.com/power-automate/desktop-flows/governance#configure-power-automate-for-desktop-confirmation-dialog-when-invoking-flows-using-a-url-or-desktop-shortcut)).

> [!WARNING]
> Disabling this dialog is a security-relevant change: it allows **any** external URL/shortcut (not just FlowLauncher) to silently trigger flows on this Windows account going forward. Only enable it on trusted, dedicated automation machines.

### Step 2: Restart Power Automate Desktop

**Critical**: Power Automate Desktop **caches the confirmation-dialog setting in memory** when it starts. Simply changing the registry value is not enough — if PAD is already running, it will continue to show the dialog until it is restarted.

FlowLauncher handles this automatically:
- If no PAD processes are running, the registry change takes effect immediately.
- If PAD processes **are** running, FlowLauncher logs a warning and explains the situation.
- Set `Flow:ForceRestartPad` to `true` to have FlowLauncher **automatically terminate** any running PAD processes before triggering the flow. This ensures the new registry setting is picked up, but may interrupt other active flows.

**Recommended approach for scheduled tasks**: Set both `DisableExternalConfirmation: true` and `ForceRestartPad: true` in your `appsettings.json` so the scheduled task can reliably run unattended without manual intervention.

**Important constraints**:
- **Admin policy overrides this.** If your Power Platform admin has set `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Power Automate Desktop\ConfigureExternalRuns` to `1` (dialog enforced) or `2` (external runs blocked entirely), FlowLauncher cannot override it — it will log a warning and leave the setting untouched.
- **Licensing**: some community reports indicate the underlying console setting is only exposed with a Premium/Process license; without one, the dialog may still require manual confirmation for **unattended** runs regardless of this registry value, since unattended execution itself requires an unattended RDP session/Process license. See [Power Automate pricing](https://www.microsoft.com/power-platform/products/power-automate/pricing).

### Step 3: Auto-confirm fallback (UI automation)

Even with the registry setting disabled and PAD restarted, newer PAD versions (2.42+) introduced a **separate "Run flow" dialog** that appears when flows use cloud connectors. This dialog is **NOT** controlled by the `EnableAskBeforeRunningAFlowExternally` registry key.

To handle this, FlowLauncher includes a **UI automation fallback** (enabled by default via `Flow:AutoConfirmDialog: true`):

1. After dispatching the flow, FlowLauncher watches for a top-level window with the title **"Run flow"**.
2. If the dialog appears within 30 seconds, FlowLauncher automatically brings it to the foreground and sends the **Enter** key to confirm it.
3. This works for both the external-invocation confirmation and the newer connections dialog.

> [!NOTE]
> This fallback requires the FlowLauncher process to be running on the **interactive desktop session** (i.e., a logged-in user session with a visible desktop). It does **not** work in a non-interactive session (e.g., a Windows service or Task Scheduler running as SYSTEM without "Run only when user is logged on"). Set `AutoConfirmDialog: false` to disable this behavior if you do not want FlowLauncher to interact with the UI.

## Passing Input Variables to Desktop Flows

Use the `Flow:Inputs` section in `appsettings.json` to pass input variables to your Power Automate Desktop flow. This prevents PAD from showing an input dialog and asking for values interactively.

### Example

```json
{
  "Flow": {
    "Inputs": {
      "CustomerName": "Acme Corp",
      "OrderId": 12345,
      "IsPriority": true,
      "Config": "{\"Department\":\"Sales\"}"
    }
  }
}
```

### How it works

FlowLauncher reads each key-value pair from `Flow:Inputs` and passes them to PAD via the `inputArguments` query parameter in the `ms-powerautomate:` run URL. The values are automatically typed:

| Config Value | Becomes in JSON | PAD Variable Type |
|---|---|---|
| `"hello"` | `"hello"` (string) | Text |
| `12345` | `12345` (number) | Number |
| `true` / `false` | `true` / `false` (boolean) | Boolean |
| `"{\"key\":\"val\"}"` | `{"key":"val"}` (object) | Custom Object |

### Requirements for this to work

1. **The flow must define input variables** — In PAD, open your flow, go to **Variables**, and create variables with the **Input** direction. The variable names must match the keys in `Flow:Inputs` exactly (case-insensitive in PAD but case-sensitive in URL).

2. **Do NOT use "Display input dialog" actions** — If your flow contains an action that explicitly asks the user for input (e.g., "Display input dialog" or "Ask for text"), PAD will still show a dialog regardless of `inputArguments`. Remove such actions and use input variables instead.

3. **PAD Console Host must support inputArguments** — This is supported in PAD 2.14+. If you are on an older version, inputs will be ignored and PAD may show a dialog.

### Passing complex values

For objects and arrays, write the JSON directly as a **string** in the config file:

```json
"Config": "{\"Department\":\"Sales\",\"Region\":\"EMEA\"}"
```

This is serialized into the URL as a proper JSON object that PAD can map to a custom object variable.

## Live Progress Display

When `Flow:ShowProgress` is `true` (default):

- **Desktop flows**: FlowLauncher watches `%LOCALAPPDATA%\Microsoft\Power Automate Desktop\Console\Scripts\[FlowId]\Runs\[RunId]\Actions.log` and streams each new log line to the console as the flow executes. When Power Automate removes the run folder (which happens once the run finishes and its logs are pushed to the cloud), FlowLauncher reports the run as complete. If the folder can't be located within the timeout, a warning is logged but the flow may still be running.
- **Cloud flows**: if the HTTP trigger responds with `202 Accepted` and a `Location` header (the standard Azure Logic Apps async pattern), FlowLauncher polls that URL every 2 seconds and prints the run's `status` field until it reaches a terminal state (`Succeeded`, `Failed`, `Cancelled`, `Aborted`) or the timeout elapses. If the trigger responds synchronously (e.g. `200 OK` with no `Location` header), there is nothing to poll and this step is skipped.

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
