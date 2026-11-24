using System.Text.Json;
using TrackPadLens.Native;

namespace TrackPadLens.Commands;

public enum ShortcutScope
{
    GlobalSystem,
    WorkspaceWindow,
    DoodleModeOnly
}

public sealed record ShortcutEntry(
    string ActionId,
    string DisplayName,
    string DefaultGesture,
    string CurrentGesture,
    ShortcutScope Scope);

/// <summary>
/// Manages context-sensitive keyboard shortcuts (Global vs Workspace-local vs Doodle-Mode-only),
/// conflict detection, profile JSON import/export, and default restoration without stealing
/// ordinary editing shortcuts from PowerPoint or other desktop apps.
/// </summary>
public sealed class ShortcutManager
{
    private readonly Dictionary<string, ShortcutEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ShortcutEntry> Entries => _entries.Values;

    public ShortcutManager()
    {
        ResetToDefaults();
    }

    public void ResetToDefaults()
    {
        _entries.Clear();
        RegisterDefault("Mode.Standard", "Standard Mode / Doodle Toggle", "Alt+D", ShortcutScope.GlobalSystem);
        RegisterDefault("Mode.TrackpadLens", "Trackpad Lens Mode", "Alt+T", ShortcutScope.GlobalSystem);
        RegisterDefault("Mode.Whiteboard", "Open Whiteboard / Study Workspace", "Alt+W", ShortcutScope.GlobalSystem);
        RegisterDefault("Capture.Region", "Region Screenshot Capture", "Alt+Shift+S", ShortcutScope.GlobalSystem);

        // Workspace-local bindings (never steal from external apps)
        RegisterDefault("Tool.Select", "Smart Selection", "V", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Tool.Pen", "Pen", "P", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Tool.Eraser", "Eraser", "E", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Tool.Highlighter", "Highlighter", "H", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Tool.Lasso", "Lasso Selection", "L", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Magic.Menu", "Magic Menu", "Ctrl+Space", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Magic.ConvertText", "Convert Handwriting to Text", "Ctrl+Shift+T", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Magic.SolveMath", "Solve Selected Equation", "Ctrl+Enter", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Edit.Undo", "Undo", "Ctrl+Z", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Edit.Redo", "Redo", "Ctrl+Y", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Edit.Copy", "Copy", "Ctrl+C", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Edit.Paste", "Paste", "Ctrl+V", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Edit.Duplicate", "Duplicate", "Ctrl+D", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Object.Group", "Group", "Ctrl+G", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Object.Ungroup", "Ungroup", "Ctrl+Shift+G", ShortcutScope.WorkspaceWindow);
        RegisterDefault("View.ZoomIn", "Zoom In", "Ctrl+OemPlus", ShortcutScope.WorkspaceWindow);
        RegisterDefault("View.ZoomOut", "Zoom Out", "Ctrl+OemMinus", ShortcutScope.WorkspaceWindow);
        RegisterDefault("View.FitCanvas", "Fit Canvas", "Ctrl+0", ShortcutScope.WorkspaceWindow);
        RegisterDefault("File.Save", "Save Workspace", "Ctrl+S", ShortcutScope.WorkspaceWindow);
        RegisterDefault("Page.New", "New Page", "Ctrl+N", ShortcutScope.WorkspaceWindow);
        RegisterDefault("View.Search", "Search Workspace", "Ctrl+F", ShortcutScope.WorkspaceWindow);
        RegisterDefault("View.ToggleToolbar", "Toggle Toolbar", "Tab", ShortcutScope.WorkspaceWindow);

        // Doodle-Mode-only quick keys (active only when overlay Doodle Mode is ON)
        RegisterDefault("Doodle.Eraser", "Doodle Stroke Eraser", "E", ShortcutScope.DoodleModeOnly);
        RegisterDefault("Doodle.Thin", "Doodle Thin Size", "1", ShortcutScope.DoodleModeOnly);
        RegisterDefault("Doodle.Medium", "Doodle Medium Size", "2", ShortcutScope.DoodleModeOnly);
        RegisterDefault("Doodle.Thick", "Doodle Thick Size", "3", ShortcutScope.DoodleModeOnly);
        RegisterDefault("Doodle.PenCycle", "Doodle Pen / Cycle Color", "D", ShortcutScope.DoodleModeOnly);
        RegisterDefault("Doodle.Clear", "Doodle Clear All Ink", "C", ShortcutScope.DoodleModeOnly);
    }

    private void RegisterDefault(string id, string name, string gesture, ShortcutScope scope)
    {
        _entries[id] = new ShortcutEntry(id, name, gesture, gesture, scope);
    }

    public string GetGesture(string actionId)
    {
        return _entries.TryGetValue(actionId, out var e) ? e.CurrentGesture : string.Empty;
    }

    public (bool Accepted, string? ConflictMessage) TryUpdateGesture(string actionId, string newGesture)
    {
        if (!_entries.TryGetValue(actionId, out var existing))
        {
            return (false, $"Unknown shortcut action '{actionId}'.");
        }

        string trimmed = newGesture.Trim();
        if (!HotkeyManager.TryParseShortcut(trimmed, out var parsed))
        {
            return (false, $"Invalid shortcut syntax: '{newGesture}'.");
        }

        // Detect conflicts within the same scope (or against GlobalSystem)
        foreach (var other in _entries.Values)
        {
            if (other.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase)) continue;
            bool scopesOverlap = other.Scope == existing.Scope ||
                                 other.Scope == ShortcutScope.GlobalSystem ||
                                 existing.Scope == ShortcutScope.GlobalSystem;
            if (!scopesOverlap) continue;

            if (HotkeyManager.TryParseShortcut(other.CurrentGesture, out var otherParsed) &&
                otherParsed.Modifiers == parsed.Modifiers &&
                otherParsed.VirtualKey == parsed.VirtualKey)
            {
                return (false, $"Conflicts with '{other.DisplayName}' ({other.CurrentGesture}).");
            }
        }

        _entries[actionId] = existing with { CurrentGesture = parsed.DisplayString };
        return (true, null);
    }

    public void ApplyOverrides(Dictionary<string, string>? overrides)
    {
        if (overrides == null) return;
        foreach (var (id, gesture) in overrides)
        {
            TryUpdateGesture(id, gesture);
        }
    }

    public Dictionary<string, string> ExportOverrides()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Values)
        {
            map[entry.ActionId] = entry.CurrentGesture;
        }
        return map;
    }

    public void ExportProfileToFile(string filePath)
    {
        var json = JsonSerializer.Serialize(ExportOverrides(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public List<string> ImportProfileFromFile(string filePath)
    {
        var warnings = new List<string>();
        string json = File.ReadAllText(filePath);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (map == null) return warnings;

        foreach (var (id, gesture) in map)
        {
            var (ok, err) = TryUpdateGesture(id, gesture);
            if (!ok && !string.IsNullOrEmpty(err))
            {
                warnings.Add($"{id}: {err}");
            }
        }
        return warnings;
    }
}
