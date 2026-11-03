using System.Text.RegularExpressions;

namespace i3vm4windows.Config.Extensions;

public static class StringInputExtensions
{
    private static string[] _keys =
    [
        "None", "Cancel", "Back", "Tab", "LineFeed", "Clear", "Enter", "Return",
        "Pause", "Capital", "CapsLock", "HangulMode", "KanaMode", "JunjaMode",
        "FinalMode", "HanjaMode", "KanjiMode", "Escape", "ImeConvert",
        "ImeNonConvert", "ImeAccept", "ImeModeChange", "Space", "PageUp", "Prior",
        "Next", "PageDown", "End", "Home", "Left", "Up", "Right", "Down",
        "Select", "Print", "Execute", "PrintScreen", "Snapshot", "Insert", "Delete",
        "Help", "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "LWin", "RWin", "Apps", "Sleep",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5",
        "NumPad6", "NumPad7", "NumPad8", "NumPad9", "Multiply", "Add",
        "Separator", "Subtract", "Decimal", "Divide",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11",
        "F12", "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21",
        "F22", "F23", "F24",
        "NumLock", "Scroll", "LeftShift", "RightShift", "LeftCtrl", "RightCtrl",
        "LeftAlt", "RightAlt", "BrowserBack", "BrowserForward", "BrowserRefresh",
        "BrowserStop", "BrowserSearch", "BrowserFavorites", "BrowserHome",
        "VolumeMute", "VolumeDown", "VolumeUp", "MediaNextTrack",
        "MediaPreviousTrack", "MediaStop", "MediaPlayPause", "LaunchMail",
        "SelectMedia", "LaunchApplication1", "LaunchApplication2",
        "Oem1", "OemSemicolon", "OemPlus", "OemComma", "OemMinus", "OemPeriod",
        "Oem2", "OemQuestion", "Oem3", "OemTilde", "AbntC1", "AbntC2", "Oem4",
        "OemOpenBrackets", "Oem5", "OemPipe", "Oem6", "OemCloseBrackets",
        "Oem7", "OemQuotes", "Oem8", "Oem102", "OemBackslash", "ImeProcessed",
        "System", "DbeAlphanumeric", "OemAttn", "DbeKatakana", "OemFinish",
        "DbeHiragana", "OemCopy", "DbeSbcsChar", "OemAuto", "DbeDbcsChar",
        "OemEnlw", "DbeRoman", "OemBackTab", "Attn", "DbeNoRoman", "CrSel",
        "DbeEnterWordRegisterMode", "DbeEnterImeConfigureMode", "ExSel",
        "DbeFlushString", "EraseEof", "DbeCodeInput", "Play", "DbeNoCodeInput",
        "Zoom", "DbeDetermineString", "NoName", "DbeEnterDialogConversionMode",
        "Pa1", "OemClear", "DeadCharProcessed"
    ];
    
    public static bool TryConvertToEnum<TEnum>(this string? name, out TEnum value)
        where TEnum: struct, Enum
    {
        value = default;
        var normalizedName = name.NormalizeKey();
        if (string.IsNullOrEmpty(normalizedName))
        {
            return false;
        }

        TEnum? enumValue = Enum.GetValues<TEnum>()
            .FirstOrDefault(k => k.ToString()
                .Equals(normalizedName, StringComparison.InvariantCultureIgnoreCase));

        if (enumValue.HasValue)
        {
            value = enumValue.Value;
            return true;
        }

        return false;
    }

    public static string? NormalizeKey(this string? name)
    {
        if (Regex.IsMatch(name ?? string.Empty, @"^\d$"))
        {
            name = $"D{name}";
        }

        return _keys.FirstOrDefault(k => k.Equals(name, StringComparison.InvariantCultureIgnoreCase));
    }

    public static bool TryParseKeys(this string keys, out Modifiers modifiers, out string key)
    {
        key = string.Empty;
        modifiers = Modifiers.None;
        var parts = keys.Split('+');

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "mod1" or "alt":
                    modifiers |= Modifiers.Alt;
                    break;
                case "mod4" or "win" or "super":
                    modifiers |= Modifiers.Win;
                    break;
                case "shift":
                    modifiers |= Modifiers.Shift;
                    break;
                case "ctrl" or "control":
                    modifiers |= Modifiers.Control;
                    break;
                default:
                    key = "";
                    return false;
            }
        }

        var normalizedKey = parts[^1].NormalizeKey();
        if (string.IsNullOrEmpty(normalizedKey))
        {
            return false;
        }

        key = normalizedKey;
        return true;
    }
}