using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TrackPadLens.Native;

public enum HotkeyAction
{
    ToggleDoodleMode = 1001,
    Undo = 1002,
    SaveSnapshot = 1003,
    ClearInk = 1004,
    DoodleSelectEraser = 1005,
    DoodleThicknessThin = 1006,
    DoodleThicknessMedium = 1007,
    DoodleThicknessThick = 1008,
    DoodlePenCycle = 1009,
    OpenTrackpadLensMode = 1010,
    OpenWhiteboardWorkspace = 1011,
    RegionScreenshotCapture = 1012
}

public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string DisplayString);

public sealed record HotkeyRegistrationStatus(
    HotkeyAction Action,
    string Shortcut,
    bool Registered,
    string? ConflictMessage);

/// <summary>
/// Manages Win32 RegisterHotKey / UnregisterHotKey bindings with duplicate prevention,
/// conflict diagnostics, and guaranteed cleanup on shutdown.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private HwndSource? _hotkeyWindow;
    private readonly Dictionary<int, HotkeyAction> _registeredIds = new();
    private readonly Dictionary<HotkeyAction, HotkeyRegistrationStatus> _statuses = new();
    private readonly HashSet<(uint Modifiers, uint Vk)> _activeCombos = new();
    private bool _disposed;

    public event Action<HotkeyAction>? HotkeyPressed;
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationStatus> Statuses => _statuses;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hotkeyWindow != null) return;

        var parameters = new HwndSourceParameters("TrackPadLensHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };

        _hotkeyWindow = new HwndSource(parameters);
        _hotkeyWindow.AddHook(WndProc);
    }

    public HotkeyRegistrationStatus Register(HotkeyAction action, string shortcutText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hotkeyWindow == null) Initialize();

        Unregister(action);

        if (!TryParseShortcut(shortcutText, out var binding))
        {
            var invalid = new HotkeyRegistrationStatus(action, shortcutText, false, $"Invalid shortcut syntax: '{shortcutText}'.");
            _statuses[action] = invalid;
            return invalid;
        }

        if (_activeCombos.Contains((binding.Modifiers, binding.VirtualKey)))
        {
            var dup = new HotkeyRegistrationStatus(action, binding.DisplayString, false, "Duplicate shortcut already assigned to another TrackPadLens action.");
            _statuses[action] = dup;
            return dup;
        }

        int id = (int)action;
        bool ok = Win32Interop.RegisterHotKey(
            _hotkeyWindow!.Handle,
            id,
            binding.Modifiers | Win32Interop.MOD_NOREPEAT,
            binding.VirtualKey);

        if (ok)
        {
            _registeredIds[id] = action;
            _activeCombos.Add((binding.Modifiers, binding.VirtualKey));
            var status = new HotkeyRegistrationStatus(action, binding.DisplayString, true, null);
            _statuses[action] = status;
            return status;
        }

        int err = Marshal.GetLastWin32Error();
        var conflict = new HotkeyRegistrationStatus(
            action,
            binding.DisplayString,
            false,
            $"Registration conflict (Win32 error {err}): another application may already claim {binding.DisplayString}.");
        _statuses[action] = conflict;
        return conflict;
    }

    public void Unregister(HotkeyAction action)
    {
        int id = (int)action;
        if (_hotkeyWindow != null && _registeredIds.Remove(id))
        {
            Win32Interop.UnregisterHotKey(_hotkeyWindow.Handle, id);
            if (_statuses.TryGetValue(action, out var prev) && TryParseShortcut(prev.Shortcut, out var binding))
            {
                _activeCombos.Remove((binding.Modifiers, binding.VirtualKey));
            }
        }
        _statuses.Remove(action);
    }

    public void UnregisterAll()
    {
        if (_hotkeyWindow != null)
        {
            foreach (int id in _registeredIds.Keys.ToList())
            {
                Win32Interop.UnregisterHotKey(_hotkeyWindow.Handle, id);
            }
        }
        _registeredIds.Clear();
        _activeCombos.Clear();
        _statuses.Clear();
    }

    /// <summary>
    /// Parses shortcuts like "Alt+D", "Ctrl+Z", "Ctrl+Shift+S", or "Escape".
    /// Pure helper for both production registration and unit tests.
    /// </summary>
    public static bool TryParseShortcut(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var normalizedTokens = new List<string>();
        foreach (string raw in parts)
        {
            string token = raw.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Interop.MOD_CONTROL;
                if (!normalizedTokens.Contains("Ctrl")) normalizedTokens.Add("Ctrl");
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Interop.MOD_ALT;
                if (!normalizedTokens.Contains("Alt")) normalizedTokens.Add("Alt");
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Interop.MOD_SHIFT;
                if (!normalizedTokens.Contains("Shift")) normalizedTokens.Add("Shift");
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Interop.MOD_WIN;
                if (!normalizedTokens.Contains("Win")) normalizedTokens.Add("Win");
            }
            else
            {
                if (vk != 0) return false; // More than one primary key
                string displayToken = token;
                if (token.Equals("Esc", StringComparison.OrdinalIgnoreCase)) token = "Escape";
                else if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
                {
                    displayToken = token;
                    token = "D" + token;
                }

                if (!Enum.TryParse<Key>(token, ignoreCase: true, out var wpfKey) || wpfKey == Key.None)
                {
                    return false;
                }

                vk = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
                if (vk == 0) return false;
                normalizedTokens.Add(displayToken.Length == 1 && char.IsDigit(displayToken[0]) ? displayToken : wpfKey.ToString());
            }
        }

        if (vk == 0) return false;

        binding = new HotkeyBinding(modifiers, vk, string.Join("+", normalizedTokens));
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredIds.TryGetValue(id, out var action))
            {
                handled = true;
                HotkeyPressed?.Invoke(action);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        if (_hotkeyWindow != null)
        {
            _hotkeyWindow.RemoveHook(WndProc);
            _hotkeyWindow.Dispose();
            _hotkeyWindow = null;
        }
    }
}
