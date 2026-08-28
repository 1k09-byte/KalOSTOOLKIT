using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

// Commands: dump <pid>, click <pid> <name-substring> [index], clickonly <pid> <name-substring> [index],
//           clickn <pid> <name-substring> <index>, type <pid> <name-substring> <text>, cdpeval <wsurl> <js>,
//           regwatch <modid> <seconds>
string cmd = args[0];

if (cmd == "regwatch")
{
    UiaProbe.RegWatch.Run(args[1], int.Parse(args[2]));
    return;
}

if (cmd == "cdpeval")
{
    Console.WriteLine(UiaProbe.Cdp.EvalAsync(args[1], args[2]).GetAwaiter().GetResult());
    return;
}

int pid = int.Parse(args[1]);
string? typeFilter = args.Length > 4 ? args[4] : null;

using var app = Application.Attach(pid);
using var automation = new UIA3Automation();
var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(5));

if (cmd == "dump")
{
    Console.WriteLine($"WINDOW: {window.Title} (pid {pid})");
    Dump(window, 0);
    return;
}

if (cmd == "key")
{
    try { window.Focus(); } catch { }
    try { Win32.ShowWindow(window.Properties.NativeWindowHandle, 9); Win32.SetForegroundWindow(window.Properties.NativeWindowHandle); } catch { }
    System.Threading.Thread.Sleep(200);
    Keyboard.Type(VirtualKeyShort.ESCAPE);
    Console.WriteLine("SENT KEY");
    return;
}

string target = args[2];
bool matchEmpty = target == "::empty::";
var matches = FindAll(window, el =>
{
    string name = el.Properties.Name.ValueOrDefault ?? "";
    string aid = el.Properties.AutomationId.ValueOrDefault ?? "";
    bool nameMatches = matchEmpty ? name.Length == 0 && aid.Length == 0
                                  : name.Contains(target, StringComparison.OrdinalIgnoreCase)
                                    || aid.Contains(target, StringComparison.OrdinalIgnoreCase);
    bool typeMatches = typeFilter == null || (el.Properties.ControlType.ValueOrDefault.ToString() ?? "").Contains(typeFilter, StringComparison.OrdinalIgnoreCase);
    return nameMatches && typeMatches;
});
Console.WriteLine($"MATCHES for '{target}': {matches.Count}");
foreach (var m in matches)
    Console.WriteLine($"  {m.Properties.ControlType.ValueOrDefault} | Name='{m.Properties.Name.ValueOrDefault}' | Aid='{m.Properties.AutomationId.ValueOrDefault}'");

if ((cmd == "clickonly" || cmd == "click" || cmd == "clickn") && matches.Count > 0)
{
    int clickIndex = 0;
    if (cmd == "clickn" && args.Length > 3 && int.TryParse(args[3], out int n)) clickIndex = n;
    if (clickIndex >= matches.Count) { Console.WriteLine($"INDEX {clickIndex} OUT OF RANGE ({matches.Count})"); return; }
    var el = matches[clickIndex];
    if (cmd == "clickonly")
    {
        try { window.Focus(); } catch { }
        try { Win32.ShowWindow(window.Properties.NativeWindowHandle, 9); Win32.SetForegroundWindow(window.Properties.NativeWindowHandle); } catch { }
        System.Threading.Thread.Sleep(400);
        if (el.TryGetClickablePoint(out _))
        {
            el.Click();
            Console.WriteLine("CLICKED (mouse)");
        }
        else Console.WriteLine("NO CLICKABLE POINT");
        return;
    }
    try { window.Focus(); } catch { }
    System.Threading.Thread.Sleep(300);
    var inv = el.Patterns.Invoke.PatternOrDefault;
    if (inv != null)
    {
        try { inv.Invoke(); Console.WriteLine("INVOKED"); return; } catch { }
    }
    if (el.TryGetClickablePoint(out _))
    {
        el.Click();
        Console.WriteLine("CLICKED");
    }
    else
    {
        Console.WriteLine("NO CLICKABLE POINT / NO INVOKE");
    }
}

if (cmd == "type" && matches.Count > 0)
{
    var el = matches[0];
    el.Focus();
    var val = el.Patterns.Value.PatternOrDefault;
    if (val != null) { val.SetValue(args[3]); Console.WriteLine("SET VALUE"); }
    else Console.WriteLine("NO VALUE PATTERN");
}

if (cmd == "focusenter" && matches.Count > 0)
{
    var el = matches[0];
    try { window.Focus(); } catch { }
    try { Win32.ShowWindow(window.Properties.NativeWindowHandle, 9); Win32.SetForegroundWindow(window.Properties.NativeWindowHandle); } catch { }
    el.Focus();
    System.Threading.Thread.Sleep(400);
    Keyboard.Type(VirtualKeyShort.RETURN);
    Console.WriteLine("FOCUS+ENTER");
}

if (cmd == "point")
{
    foreach (var m in matches)
    {
        var r = m.Properties.BoundingRectangle.ValueOrDefault;
        var cp = m.TryGetClickablePoint(out var p) ? p.ToString() : "none";
        Console.WriteLine($"{m.Properties.ControlType.ValueOrDefault} | Name='{m.Properties.Name.ValueOrDefault}' | Rect={r} | Clickable={cp}");
    }
}

if (cmd == "value" && matches.Count > 0)
{
    var el = matches[0];
    var val = el.Patterns.Value.PatternOrDefault;
    if (val != null) Console.WriteLine("VALUE:\n" + val.Value);
    else Console.WriteLine("NO VALUE PATTERN");
}

if (cmd == "keytype" && matches.Count > 0)
{
    var el = matches[0];
    try { window.Focus(); } catch { }
    try { Win32.ShowWindow(window.Properties.NativeWindowHandle, 9); Win32.SetForegroundWindow(window.Properties.NativeWindowHandle); } catch { }
    el.Focus();
    System.Threading.Thread.Sleep(300);
    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, (VirtualKeyShort)0x41); // Ctrl+A
    System.Threading.Thread.Sleep(200);
    Keyboard.Type(args[3]);
    Console.WriteLine("TYPED");
}

List<AutomationElement> FindAll(AutomationElement root, Func<AutomationElement, bool> pred)
{
    var found = new List<AutomationElement>();
    Walk(root);
    return found;

    void Walk(AutomationElement el)
    {
        if (pred(el)) found.Add(el);
        foreach (var child in el.FindAllChildren())
            Walk(child);
    }
}

void Dump(AutomationElement el, int depth)
{
    if (depth > 16) return;
    var ct = el.Properties.ControlType.ValueOrDefault;
    var name = el.Properties.Name.ValueOrDefault ?? "";
    var aid = el.Properties.AutomationId.ValueOrDefault ?? "";
    var cls = el.Properties.ClassName.ValueOrDefault ?? "";
    Console.WriteLine($"{new string(' ', depth * 2)}{ct} | Name='{name}' | Aid='{aid}' | Class='{cls}'");
    try
    {
        foreach (var child in el.FindAllChildren())
            Dump(child, depth + 1);
    }
    catch { }
}

internal static class Win32
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
