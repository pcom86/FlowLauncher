using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

namespace FlowLauncher;

static class DialogAutoConfirm
{
    const int SW_RESTORE = 9;
    const int BM_CLICK = 0x00F5;
    const int GA_ROOT = 2;

    static readonly string[] TitleKeywords =
    [
        "external source", "attempting to run", "run flow", "Run Flow",
        "Power Automate", "input", "Input", "confirm", "Confirm",
        "parameters", "Parameters", "variables", "Variables",
        "enter values", "Enter values"
    ];

    static readonly string[] ButtonKeywords =
    [
        "Run flow", "Run Flow", "Run", "OK", "Confirm",
        "Continue", "Yes", "Start", "Accept", "Submit"
    ];

    public static async Task RunAsync(int timeoutSeconds, Action<string, string> log, FlowSummary summary)
    {
        log("Information", $"Auto-confirm: starting dialog watcher (up to {timeoutSeconds}s).");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var confirmed = new HashSet<IntPtr>();
        int dialogsConfirmed = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(800);

            try
            {
                var matches = new List<(IntPtr hWnd, string title)>();
                EnumWindows((hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    if (confirmed.Contains(hWnd)) return true;

                    var sb = new StringBuilder(512);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    var title = sb.ToString();
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    foreach (var kw in TitleKeywords)
                    {
                        if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add((hWnd, title));
                            break;
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (matches.Count == 0)
                {
                    var buttonHwnd = FindButtonInUnconfirmedWindows(confirmed);
                    if (buttonHwnd != IntPtr.Zero)
                    {
                        var parentHwnd = GetAncestor(buttonHwnd, GA_ROOT);
                        var parentTitle = GetWindowTitle(parentHwnd);
                        log("Information", $"Auto-confirm: found button in window '{parentTitle}'. Clicking...");

                        try
                        {
                            SendMessage(buttonHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                            summary.DialogAutoConfirmed = true;
                            dialogsConfirmed++;
                            log("Information", $"Auto-confirm: dialog #{dialogsConfirmed} confirmed via button click.");
                        }
                        catch (Exception ex)
                        {
                            log("Warning", $"Auto-confirm: button click failed: {ex.Message}");
                        }

                        if (parentHwnd != IntPtr.Zero)
                            confirmed.Add(parentHwnd);
                        await Task.Delay(1500);
                        continue;
                    }

                    continue;
                }

                foreach (var (hWnd, title) in matches)
                {
                    if (confirmed.Contains(hWnd)) continue;

                    log("Information", $"Auto-confirm: detected dialog '{title}' (hWnd={hWnd}).");

                    var clicked = TryClickButton(hWnd, log);

                    if (!clicked)
                    {
                        try
                        {
                            ShowWindow(hWnd, SW_RESTORE);
                            await Task.Delay(100);
                            SetForegroundWindow(hWnd);
                            await Task.Delay(300);
                            SendKeys.SendWait("{ENTER}");
                            log("Information", "Auto-confirm: Enter key sent as fallback.");
                            clicked = true;
                        }
                        catch (Exception ex)
                        {
                            log("Warning", $"Auto-confirm: Enter key fallback failed: {ex.Message}");
                        }
                    }

                    if (clicked)
                    {
                        summary.DialogAutoConfirmed = true;
                        dialogsConfirmed++;
                    }

                    confirmed.Add(hWnd);
                    await Task.Delay(1500);
                }
            }
            catch (Exception ex)
            {
                log("Warning", $"Auto-confirm: transient error: {ex.Message}");
            }
        }

        log("Information", $"Auto-confirm: finished. Confirmed {dialogsConfirmed} dialog(s) total.");
    }

    static bool TryClickButton(IntPtr parentHwnd, Action<string, string> log)
    {
        foreach (var kw in ButtonKeywords)
        {
            var btn = FindWindowEx(parentHwnd, IntPtr.Zero, "Button", kw);
            if (btn == IntPtr.Zero)
                btn = FindChildButtonByText(parentHwnd, kw);

            if (btn != IntPtr.Zero)
            {
                try
                {
                    SendMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    log("Information", $"Auto-confirm: clicked button '{kw}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    log("Warning", $"Auto-confirm: failed to click button '{kw}': {ex.Message}");
                }
            }
        }
        return false;
    }

    static IntPtr FindChildButtonByText(IntPtr parentHwnd, string text)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parentHwnd, (child, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(child, sb, sb.Capacity);
            if (sb.ToString().Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr FindButtonInUnconfirmedWindows(HashSet<IntPtr> confirmed)
    {
        IntPtr foundButton = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (confirmed.Contains(hWnd)) return true;

            foreach (var kw in ButtonKeywords)
            {
                var btn = FindWindowEx(hWnd, IntPtr.Zero, "Button", kw);
                if (btn == IntPtr.Zero)
                    btn = FindChildButtonByText(hWnd, kw);

                if (btn != IntPtr.Zero)
                {
                    foundButton = btn;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return foundButton;
    }

    static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "(none)";
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // --- P/Invoke declarations ---

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);
}
