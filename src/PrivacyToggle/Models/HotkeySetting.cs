using System.Text.Json.Serialization;
using System.Windows.Input;

namespace PrivacyToggle.Models;

/// <summary>The things PrivacyToggle switches: what a shortcut, a badge or an alert is about.</summary>
public enum PrivacyDevice { Microphone, Camera, Network }

/// <summary>
/// A key combination such as Ctrl+Alt+M. Modifiers are "Ctrl", "Alt", "Shift", "Win";
/// Key is a WPF <see cref="System.Windows.Input.Key"/> name.
/// </summary>
public sealed class HotkeySetting
{
    public List<string> Modifiers { get; set; } = new();
    public string Key { get; set; } = "";

    public static HotkeySetting Default => new() { Modifiers = new() { "Ctrl", "Alt" }, Key = "M" };
    public static HotkeySetting CameraDefault => new() { Modifiers = new() { "Ctrl", "Alt" }, Key = "V" };
    public static HotkeySetting NetworkDefault => new() { Modifiers = new() { "Ctrl", "Alt" }, Key = "N" };

    public static HotkeySetting DefaultFor(PrivacyDevice target) => target switch
    {
        PrivacyDevice.Camera => CameraDefault,
        PrivacyDevice.Network => NetworkDefault,
        _ => Default,
    };

    [JsonIgnore] public bool Ctrl => Has("Ctrl");
    [JsonIgnore] public bool Alt => Has("Alt");
    [JsonIgnore] public bool Shift => Has("Shift");
    [JsonIgnore] public bool Win => Has("Win");

    [JsonIgnore]
    public Key WpfKey => Enum.TryParse<Key>(Key, true, out var k) ? k : System.Windows.Input.Key.None;

    [JsonIgnore]
    public int VirtualKey => WpfKey == System.Windows.Input.Key.None ? 0 : KeyInterop.VirtualKeyFromKey(WpfKey);

    [JsonIgnore]
    public bool IsValid => VirtualKey != 0 && (Ctrl || Alt || Shift || Win);

    private bool Has(string m) => Modifiers.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));

    public static HotkeySetting From(ModifierKeys mods, Key key)
    {
        var h = new HotkeySetting { Key = key.ToString() };
        if (mods.HasFlag(ModifierKeys.Control)) h.Modifiers.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) h.Modifiers.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) h.Modifiers.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) h.Modifiers.Add("Win");
        return h;
    }

    public bool SameAs(HotkeySetting other) => ToString() == other.ToString();

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add(PrivacyToggle.Properties.Resources.Key_Ctrl);
        if (Alt) parts.Add(PrivacyToggle.Properties.Resources.Key_Alt);
        if (Shift) parts.Add(PrivacyToggle.Properties.Resources.Key_Shift);
        if (Win) parts.Add(PrivacyToggle.Properties.Resources.Key_Win);
        parts.Add(KeyDisplayName(WpfKey));
        return string.Join("+", parts);
    }

    public static string KeyDisplayName(Key key) => key switch
    {
        System.Windows.Input.Key.None => PrivacyToggle.Properties.Resources.Key_Unknown,
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
        >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num" + (key - System.Windows.Input.Key.NumPad0),
        System.Windows.Input.Key.OemTilde => PrivacyToggle.Properties.Resources.Key_OemTilde,
        System.Windows.Input.Key.OemMinus => PrivacyToggle.Properties.Resources.Key_OemMinus,
        System.Windows.Input.Key.OemPlus => PrivacyToggle.Properties.Resources.Key_OemPlus,
        System.Windows.Input.Key.OemOpenBrackets => PrivacyToggle.Properties.Resources.Key_OemOpenBrackets,
        System.Windows.Input.Key.OemCloseBrackets => PrivacyToggle.Properties.Resources.Key_OemCloseBrackets,
        System.Windows.Input.Key.OemPipe => PrivacyToggle.Properties.Resources.Key_OemPipe,
        System.Windows.Input.Key.OemSemicolon => PrivacyToggle.Properties.Resources.Key_OemSemicolon,
        System.Windows.Input.Key.OemQuotes => PrivacyToggle.Properties.Resources.Key_OemQuotes,
        System.Windows.Input.Key.OemComma => PrivacyToggle.Properties.Resources.Key_OemComma,
        System.Windows.Input.Key.OemPeriod => PrivacyToggle.Properties.Resources.Key_OemPeriod,
        System.Windows.Input.Key.OemQuestion => PrivacyToggle.Properties.Resources.Key_OemQuestion,
        System.Windows.Input.Key.Return => PrivacyToggle.Properties.Resources.Key_Enter,
        System.Windows.Input.Key.Back => PrivacyToggle.Properties.Resources.Key_Backspace,
        System.Windows.Input.Key.Capital => PrivacyToggle.Properties.Resources.Key_CapsLock,
        System.Windows.Input.Key.Next => PrivacyToggle.Properties.Resources.Key_PageDown,
        System.Windows.Input.Key.Prior => PrivacyToggle.Properties.Resources.Key_PageUp,
        System.Windows.Input.Key.Snapshot => PrivacyToggle.Properties.Resources.Key_PrintScreen,
        _ => key.ToString(),
    };
}
