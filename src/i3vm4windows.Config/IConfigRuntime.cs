using i3vm4windows.Core;

namespace i3vm4windows.Config;

/// <summary>
/// What a parsed command can ask the app to do when its binding fires:
/// the statements' vocabulary, implemented by the app against the
/// window manager, the binding modes and the process launcher. The
/// interface stays free of Windows dependencies — the implementation
/// owns the platform.
/// </summary>
public interface IConfigRuntime
{
    void FocusRelative(int offset);
    void MoveRelative(int offset);
    void SwitchWorkspace(int index);
    void SwitchBackAndForth();
    void MoveFocusedToWorkspace(int index);
    void MoveActiveWorkspaceToNextMonitor();
    void ToggleOrientation();
    void SetTabbed();
    void SetSplit();
    void CloseFocused();
    void Refresh();

    /// <summary>reload: restarts the whole application, reloading the
    /// configuration; exec autostarts do not run again, exec_always do —
    /// the same as the tray menu's reload.</summary>
    void Restart();
    void ToggleFloating();
    void ToggleScratchpad(WindowRule rule);
    void ResizeFocused(ResizeAxis axis, int delta);
    void SetMode(string name);
    void Launch(string command);
}
