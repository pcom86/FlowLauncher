#requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs FlowLauncher and registers a Windows Scheduled Task to run a Power Automate flow.
.DESCRIPTION
    Copies the published binaries to a target directory and creates a scheduled task
    that invokes FlowLauncher.exe on the desired schedule.
.PARAMETER InstallDir
    Directory where FlowLauncher will be installed. Default: C:\Program Files\FlowLauncher
.PARAMETER TaskName
    Name of the scheduled task. Default: FlowLauncher-RunPowerAutomate
.PARAMETER Schedule
    Schedule frequency: Daily, Hourly, Minute, Weekly, Monthly, AtStartup, AtLogon. Default: Daily
.PARAMETER StartTime
    Start time for Daily/Weekly tasks in HH:mm format. Default: 08:00
.PARAMETER IntervalMinutes
    Interval in minutes for Minute schedule. Default: 60
.PARAMETER DaysOfWeek
    Days for Weekly schedule. Default: Monday,Wednesday,Friday
.PARAMETER UserName
    User account to run the task as. Default: current user
.PARAMETER RunWithHighestPrivilege
    Run with highest privileges. Default: true
.PARAMETER ConfigFile
    Path to the appsettings.json to use. Default: appsettings.json in the publish folder
.EXAMPLE
    .\Setup-ScheduledTask.ps1 -TaskName "Morning-Flow" -Schedule Daily -StartTime "07:30"
.EXAMPLE
    .\Setup-ScheduledTask.ps1 -Schedule Hourly -IntervalMinutes 30
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\FlowLauncher",
    [string]$TaskName = "FlowLauncher-RunPowerAutomate",
    [ValidateSet("Daily","Hourly","Minute","Weekly","Monthly","AtStartup","AtLogon")]
    [string]$Schedule = "Daily",
    [string]$StartTime = "08:00",
    [int]$IntervalMinutes = 60,
    [string[]]$DaysOfWeek = @("Monday","Wednesday","Friday"),
    [string]$UserName = $env:USERNAME,
    [bool]$RunWithHighestPrivilege = $true,
    [string]$ConfigFile = ""
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$Message, [string]$Level = "Info") {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "Error" { "Red" }
        "Warning" { "Yellow" }
        default { "Cyan" }
    }
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

# Determine publish source
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$publishDir = Join-Path $scriptDir "bin\Release\net10.0\win-x64\publish"

if (-not (Test-Path $publishDir)) {
    $publishDir = Join-Path $scriptDir "publish"
}

if (-not (Test-Path $publishDir)) {
    Write-Log "Publish directory not found at expected path: $publishDir" "Error"
    Write-Log "Please run: dotnet publish -c Release -r win-x64 --self-contained" "Error"
    exit 1
}

$exePath = Join-Path $publishDir "FlowLauncher.exe"
if (-not (Test-Path $exePath)) {
    Write-Log "FlowLauncher.exe not found in publish directory." "Error"
    exit 1
}

Write-Log "Found published binaries at: $publishDir"

# Create install directory
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Log "Created install directory: $InstallDir"
} else {
    Write-Log "Install directory already exists: $InstallDir"
}

# Copy files
Write-Log "Copying files to $InstallDir..."
Get-ChildItem -Path $publishDir | ForEach-Object {
    $dest = Join-Path $InstallDir $_.Name
    Copy-Item -Path $_.FullName -Destination $dest -Force -Recurse
    Write-Log "  Copied: $($_.Name)"
}

# Handle custom config
$installedConfig = Join-Path $InstallDir "appsettings.json"
if ($ConfigFile -and (Test-Path $ConfigFile)) {
    Copy-Item -Path $ConfigFile -Destination $installedConfig -Force
    Write-Log "Installed custom configuration: $ConfigFile"
}

# Verify exe
$installedExe = Join-Path $InstallDir "FlowLauncher.exe"
if (-not (Test-Path $installedExe)) {
    Write-Log "Installation failed. FlowLauncher.exe not found at $installedExe" "Error"
    exit 1
}

Write-Log "Installation complete. Executable: $installedExe"

# Build scheduled task
Write-Log "Creating scheduled task '$TaskName'..."

$action = New-ScheduledTaskAction -Execute $installedExe -WorkingDirectory $InstallDir

$principal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive
if ($RunWithHighestPrivilege) {
    $principal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive -RunLevel Highest
}

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RunOnlyIfNetworkAvailable:$false

$trigger = switch ($Schedule) {
    "Daily" {
        New-ScheduledTaskTrigger -Daily -At $StartTime
    }
    "Hourly" {
        $time = [DateTime]::ParseExact($StartTime, "HH:mm", $null)
        New-ScheduledTaskTrigger -Once -At $time -RepetitionInterval (New-TimeSpan -Hours 1)
    }
    "Minute" {
        $time = Get-Date
        $time = $time.Date + [TimeSpan]::FromMinutes(1)
        New-ScheduledTaskTrigger -Once -At $time -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
    }
    "Weekly" {
        New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DaysOfWeek -At $StartTime
    }
    "Monthly" {
        $time = [DateTime]::ParseExact($StartTime, "HH:mm", $null)
        New-ScheduledTaskTrigger -Once -At $time
    }
    "AtStartup" {
        New-ScheduledTaskTrigger -AtStartup
    }
    "AtLogon" {
        New-ScheduledTaskTrigger -AtLogon -User $UserName
    }
    default {
        New-ScheduledTaskTrigger -Daily -At $StartTime
    }
}

# Remove existing task if present
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Log "Removed existing task: $TaskName"
}

$task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal
Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

Write-Log "Scheduled task '$TaskName' created successfully."
Write-Log "  Schedule: $Schedule"
if ($Schedule -in @("Daily","Hourly","Weekly","Monthly")) {
    Write-Log "  Start time: $StartTime"
}
if ($Schedule -eq "Minute") {
    Write-Log "  Interval: $IntervalMinutes minutes"
}
if ($Schedule -eq "Weekly") {
    Write-Log "  Days: $($DaysOfWeek -join ', ')"
}
Write-Log "  Run as: $UserName"
Write-Log "  Highest privileges: $RunWithHighestPrivilege"
Write-Log "  Working directory: $InstallDir"

Write-Log ""
Write-Log "To test the task manually, run:" "Warning"
Write-Log "  Start-ScheduledTask -TaskName '$TaskName'" "Warning"
Write-Log "Or run the executable directly:" "Warning"
Write-Log "  $installedExe" "Warning"
