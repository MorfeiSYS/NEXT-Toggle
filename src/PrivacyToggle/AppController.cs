using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using PrivacyToggle.Interop;
using PrivacyToggle.Models;
using PrivacyToggle.Services;
using PrivacyToggle.Tray;
using PrivacyToggle.Views;

namespace PrivacyToggle;

/// <summary>Owns the services and the tray icons and holds the app's behaviour.</summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MicState, System.Drawing.Icon> _micIcons = new();
    private readonly Dictionary<CameraState, System.Drawing.Icon> _camIcons = new();
    private readonly Dictionary<NetworkState, System.Drawing.Icon> _netIcons = new();
    private TaskbarIcon? _micTray, _camTray, _netTray;
    private TrayMenu? _micMenu, _camMenu, _netMenu;
    private SettingsWindow? _settingsWindow;
    private TaskbarSpeedWindow? _taskbarSpeed;
    private readonly Dictionary<OverlayCorner, OverlayWindow> _overlays = new(); // badge windows, one per corner in use
    private readonly Dictionary<OverlayCorner, NotifyBadgeWindow> _notifyBadges = new(); // short-lived switch badges, per corner
    private bool _hotkeySuspended;
    private bool _disposed;

    public SettingsService Settings { get; } = new();
    public MicService Mic { get; private set; } = null!;
    public CameraService Camera { get; private set; } = null!;
    public NetworkService Network { get; private set; } = null!;

    /// <summary>Which apps use the network and how much (Network page).</summary>
    public NetworkUsageService NetworkUsage { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public PushToTalkService PushToTalk { get; private set; } = null!;
    public SoundService Sounds { get; } = new();

    /// <summary>Which apps used the camera/microphone and when (Activity page).</summary>
    public UsageTracker Usage { get; private set; } = null!;

    /// <summary>Non-null when the mic hotkey could not be activated (e.g. used by another app).</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>Non-null when the camera hotkey could not be activated.</summary>
    public string? CameraHotkeyError { get; private set; }

    /// <summary>Non-null when the network hotkey could not be activated.</summary>
    public string? NetworkHotkeyError { get; private set; }

    /// <summary>Mic/camera/network state or hotkey status changed; the Settings window refreshes on this.</summary>
    public event Action? StateChanged;

    /// <summary>The microphone list changed.</summary>
    public event Action? DevicesChanged;

    /// <summary>The camera list changed.</summary>
    public event Action? CameraDevicesChanged;

    /// <summary>The network adapter list (or an adapter's connection) changed.</summary>
    public event Action? NetworkAdaptersChanged;

    private AppSettings S => Settings.Current;

    public AppController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <param name="minimizedArg">--minimized: start in the tray only (Windows startup).</param>
    /// <param name="showArg">--show: always open Settings, even with "Start minimized" on (used by the installer).</param>
    /// <param name="promoteTrayArg">--promote-tray: show the tray icons on the taskbar instead of the overflow (used by the installer).</param>
    public void Start(bool minimizedArg, bool showArg = false, bool promoteTrayArg = false)
    {
        Settings.Load();
        LocalizationService.Apply(S.Language);
        ThemeService.Initialize(S.Theme);
        LoadIcons();

        Mic = new MicService(_dispatcher) { EnforceMute = S.EnforceMute };
        Mic.SetTargets(S.DeviceMode, S.SelectedDeviceIds);
        Mic.SetMuted(S.HotkeyMode == HotkeyMode.PushToTalk || S.OnStartState switch
        {
            OnStartState.Muted => true,
            OnStartState.Unmuted => false,
            _ => S.LastMuted,
        });
        Mic.StateChanged += OnMicStateChanged;
        Mic.DevicesChanged += () => DevicesChanged?.Invoke();

        Camera = new CameraService(_dispatcher) { EnforceOff = S.EnforceCameraOff };
        Camera.SetTargets(S.CameraDeviceMode, S.SelectedCameraIds, apply: false);
        Camera.StateChanged += OnCameraStateChanged;
        Camera.DevicesChanged += () => CameraDevicesChanged?.Invoke();
        _ = Camera.StartAsync(S.CameraOnStartState switch
        {
            CameraStartState.Off => true,
            CameraStartState.On => false,
            _ => S.LastCameraOff,
        });

        Network = new NetworkService(_dispatcher) { EnforceOff = S.EnforceNetworkOff };
        Network.SetTargets(S.NetworkDeviceMode, S.SelectedNetworkIds, apply: false);
        Network.StateChanged += OnNetworkStateChanged;
        Network.AdaptersChanged += () => NetworkAdaptersChanged?.Invoke();
        _ = Network.StartAsync(S.NetworkOnStartState switch
        {
            CameraStartState.Off => true,
            CameraStartState.On => false,
            _ => S.LastNetworkOff,
        });

        NetworkUsage = new NetworkUsageService(_dispatcher, Settings.FolderPath);
        Usage = new UsageTracker(_dispatcher, Settings.FolderPath) { CameraInUse = Camera.InUseAsync };
        UpdateBlocked();

        // The Run key is the source of truth (the installer may have created it);
        // while the option is on, keep its path pointing at this EXE.
        S.StartWithWindows = S.StartWithWindows || StartupService.IsEnabled();
        if (S.StartWithWindows) StartupService.RefreshPath();
        Settings.Save();

        UpdateTrays();
        if (promoteTrayArg) PromoteTrayIcons();
        UpdateTaskbarSpeed();

        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += id =>
        {
            if (id == HotkeyService.MicId) UserToggle();
            else if (id == HotkeyService.CameraId) UserToggleCamera();
            else if (id == HotkeyService.NetworkId) UserToggleNetwork();
        };
        PushToTalk = new PushToTalkService(_dispatcher);
        PushToTalk.HeldChanged += held => SetMutedByUser(!held, notify: false);
        ApplyHotkeys();

        OnMicStateChanged();

        if (HotkeyError != null)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_HotkeyUnavailable, HotkeyError);
        else if (CameraHotkeyError != null)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_HotkeyUnavailable, CameraHotkeyError);
        else if (NetworkHotkeyError != null)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_HotkeyUnavailable, NetworkHotkeyError);

        if (showArg || (!minimizedArg && !S.StartMinimized))
            ShowSettings();
    }

    // ---------------- tray ----------------

    private void LoadIcons()
    {
        int size = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
        System.Drawing.Icon Load(string name)
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
            using var stream = info!.Stream;
            return new System.Drawing.Icon(stream, new System.Drawing.Size(size, size));
        }
        _micIcons[MicState.Live] = Load("mic-on.ico");
        _micIcons[MicState.Muted] = Load("mic-off.ico");
        _micIcons[MicState.NoDevice] = Load("mic-none.ico");
        _camIcons[CameraState.On] = Load("cam-on.ico");
        _camIcons[CameraState.Off] = Load("cam-off.ico");
        _camIcons[CameraState.NoDevice] = Load("cam-none.ico");
        _camIcons[CameraState.Unavailable] = _camIcons[CameraState.NoDevice];
        _netIcons[NetworkState.On] = Load("net-on.ico");
        _netIcons[NetworkState.Off] = Load("net-off.ico");
        _netIcons[NetworkState.NoDevice] = Load("net-none.ico");
        _netIcons[NetworkState.Unavailable] = _netIcons[NetworkState.NoDevice];
    }

    /// <summary>Creates or removes each tray icon to match the settings.</summary>
    private void UpdateTrays()
    {
        if (S.ShowMicTrayIcon && _micTray == null)
        {
            _micMenu = new TrayMenu(this);
            _micTray = new TaskbarIcon
            {
                Id = StableTrayId("microphone"),
                Icon = (System.Drawing.Icon)_micIcons[Mic.State].Clone(),
                ToolTipText = PrivacyToggle.Properties.Resources.Tray_DefaultMicTooltip,
                ContextMenu = _micMenu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(OnMicTrayClick),
                // Never wait for a possible double-click: the click acts at once (see OnMicTrayDoubleClick).
                NoLeftClickDelay = true,
            };
            _micTray.ForceCreate(enablesEfficiencyMode: false);
        }
        else if (!S.ShowMicTrayIcon && _micTray != null)
        {
            _micTray.Dispose();
            _micTray = null;
            _micMenu = null;
        }

        if (S.ShowCameraTrayIcon && _camTray == null)
        {
            _camMenu = new TrayMenu(this);
            _camTray = new TaskbarIcon
            {
                Id = StableTrayId("camera"),
                Icon = (System.Drawing.Icon)_camIcons[Camera.State].Clone(),
                ToolTipText = PrivacyToggle.Properties.Resources.Tray_DefaultCamTooltip,
                ContextMenu = _camMenu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(UserToggleCamera),
            };
            _camTray.ForceCreate(enablesEfficiencyMode: false);
        }
        else if (!S.ShowCameraTrayIcon && _camTray != null)
        {
            _camTray.Dispose();
            _camTray = null;
            _camMenu = null;
        }

        if (S.ShowNetworkTrayIcon && _netTray == null)
        {
            _netMenu = new TrayMenu(this);
            _netTray = new TaskbarIcon
            {
                Id = StableTrayId("network"),
                Icon = (System.Drawing.Icon)_netIcons[Network.State].Clone(),
                ToolTipText = PrivacyToggle.Properties.Resources.Tray_DefaultNetTooltip,
                ContextMenu = _netMenu,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(UserToggleNetwork),
                NoLeftClickDelay = true,
            };
            _netTray.ForceCreate(enablesEfficiencyMode: false);
        }
        else if (!S.ShowNetworkTrayIcon && _netTray != null)
        {
            _netTray.Dispose();
            _netTray = null;
            _netMenu = null;
        }

        ApplyClickMode();
        UpdateVisuals();
    }

    /// <summary>
    /// Each tray icon needs its own fixed ID: icons without one share the same default identity,
    /// so the second replaces the first. Windows ties an ID to the EXE path, so derive it from both.
    /// </summary>
    private static Guid StableTrayId(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"PrivacyToggle|{name}|{Environment.ProcessPath}".ToUpperInvariant()));
        return new Guid(bytes);
    }

    /// <summary>
    /// Windows 11 puts new tray icons in the overflow menu. Explorer keeps each icon's choice in
    /// HKCU\Control Panel\NotifyIconSettings\&lt;n&gt; (IconGuid = our tray ID, IsPromoted = 1 shows it on the
    /// taskbar) and applies changes live. Explorer writes the entry shortly after the icon is added, so retry
    /// for a few seconds. Only done after installing; later the user's own choice is left alone.
    /// </summary>
    private void PromoteTrayIcons()
    {
        var pending = new HashSet<Guid> { StableTrayId("microphone"), StableTrayId("camera") };
        if (_netTray != null) pending.Add(StableTrayId("network"));
        int attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            try
            {
                using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                if (root == null) { timer.Stop(); return; } // Windows 10: no such setting
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name, writable: true);
                    if (entry?.GetValue("IconGuid") is string s && Guid.TryParse(s, out var id) && pending.Remove(id))
                        entry.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { pending.Clear(); }
            if (pending.Count == 0 || ++attempts >= 20) timer.Stop();
        };
        timer.Start();
    }

    private void ApplyClickMode()
    {
        if (_micTray != null)
            _micTray.DoubleClickCommand = S.DoubleClickOpensSettings ? new RelayCommand(OnMicTrayDoubleClick) : null;
        if (_camTray != null)
        {
            // The camera icon always acts at once. Undoing a camera switch on double-click would turn the
            // device off and on again (dropping any call), so double-click-to-Settings is mic-icon only;
            // the second click of a double-click is ignored while the first switch is still running.
            _camTray.NoLeftClickDelay = true;
            _camTray.DoubleClickCommand = null;
        }
    }

    private long _lastClickTicks;
    private bool _mutedBeforeClick;

    private void OnMicTrayClick()
    {
        _lastClickTicks = Environment.TickCount64;
        _mutedBeforeClick = Mic.DesiredMuted;
        UserToggle();
    }

    /// <summary>
    /// The first click of a double-click already toggled; undo it quietly, then open Settings.
    /// This keeps single clicks instant instead of waiting out the double-click time.
    /// </summary>
    private void OnMicTrayDoubleClick()
    {
        long sinceClick = Environment.TickCount64 - _lastClickTicks;
        if (sinceClick <= NativeMethods.GetDoubleClickTime() + 200 && Mic.DesiredMuted != _mutedBeforeClick)
            Mic.SetMuted(_mutedBeforeClick);
        ShowSettings();
    }

    private string BuildMicTooltip()
    {
        bool ptt = S.HotkeyMode == HotkeyMode.PushToTalk;
        return Mic.State switch
        {
            MicState.NoDevice => PrivacyToggle.Properties.Resources.TrayTip_Mic_NoDevice,
            MicState.Muted => LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Off, MicHintAction(true)),
            _ => ptt && HotkeyError == null
                ? PrivacyToggle.Properties.Resources.TrayTip_Mic_On_Ptt
                : LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_On, MicHintAction(false)),
        };
    }

    private string MicHintAction(bool unmute)
    {
        string verb = unmute ? PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_Unmute : PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_Mute;
        if (HotkeyError != null)
            return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_ClickUnavailable, verb);
        if (S.HotkeyMode == HotkeyMode.PushToTalk)
            return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_HoldTalk, S.Hotkey);
        return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_Hotkey, S.Hotkey, verb);
    }

    private string BuildCameraTooltip()
    {
        if (Camera.Busy) return Camera.DesiredOff ? PrivacyToggle.Properties.Resources.TrayTip_Cam_TurningOff : PrivacyToggle.Properties.Resources.TrayTip_Cam_TurningOn;
        return Camera.State switch
        {
            CameraState.Unavailable => PrivacyToggle.Properties.Resources.TrayTip_Cam_NotInstalled,
            CameraState.NoDevice => PrivacyToggle.Properties.Resources.TrayTip_Cam_NoDevice,
            CameraState.Off => LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Cam_Off, CamHintAction(true)),
            _ => LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Cam_On, CamHintAction(false)),
        };
    }

    private string CamHintAction(bool turnOn)
    {
        string verb = turnOn ? PrivacyToggle.Properties.Resources.TrayTip_Cam_Hint_TurnOn : PrivacyToggle.Properties.Resources.TrayTip_Cam_Hint_TurnOff;
        if (CameraHotkeyError != null)
            return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Cam_Hint_Click, verb);
        return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_Hotkey, S.CameraHotkey, verb);
    }

    private string BuildNetworkTooltip()
    {
        if (Network.Busy) return Network.DesiredOff ? PrivacyToggle.Properties.Resources.TrayTip_Net_Disconnecting : PrivacyToggle.Properties.Resources.TrayTip_Net_Reconnecting;
        return Network.State switch
        {
            NetworkState.Unavailable => Network.HelperOutdated
                ? PrivacyToggle.Properties.Resources.TrayTip_Net_NeedsInstaller
                : PrivacyToggle.Properties.Resources.TrayTip_Net_NotInstalled,
            NetworkState.NoDevice => PrivacyToggle.Properties.Resources.TrayTip_Net_NoDevice,
            NetworkState.Off => LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Net_Off, NetHintAction(true)),
            _ => LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Net_On, NetHintAction(false)),
        };
    }

    private string NetHintAction(bool reconnect)
    {
        string verb = reconnect ? PrivacyToggle.Properties.Resources.TrayTip_Net_Hint_Reconnect : PrivacyToggle.Properties.Resources.TrayTip_Net_Hint_Disconnect;
        if (NetworkHotkeyError != null)
            return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Cam_Hint_Click, verb);
        return LocalizationService.Format(PrivacyToggle.Properties.Resources.TrayTip_Mic_Hint_Hotkey, S.NetworkHotkey, verb);
    }

    /// <summary>
    /// While a switch is running, show where it is heading so the icon reacts instantly like the mic's;
    /// if Windows refuses, the next update shows the real state again.
    /// </summary>
    private CameraState DisplayedCameraState =>
        Camera.Busy && Camera.State is CameraState.On or CameraState.Off
            ? (Camera.DesiredOff ? CameraState.Off : CameraState.On)
            : Camera.State;

    private NetworkState DisplayedNetworkState =>
        Network.Busy && Network.State is NetworkState.On or NetworkState.Off
            ? (Network.DesiredOff ? NetworkState.Off : NetworkState.On)
            : Network.State;

    private void UpdateVisuals()
    {
        if (_micTray != null)
        {
            // TaskbarIcon disposes the icon it replaces, so always hand it a copy.
            _micTray.Icon = (System.Drawing.Icon)_micIcons[Mic.State].Clone();
            _micTray.ToolTipText = BuildMicTooltip();
        }
        if (_camTray != null && Camera != null)
        {
            _camTray.Icon = (System.Drawing.Icon)_camIcons[DisplayedCameraState].Clone();
            _camTray.ToolTipText = BuildCameraTooltip();
        }
        if (_netTray != null && Network != null)
        {
            _netTray.Icon = (System.Drawing.Icon)_netIcons[DisplayedNetworkState].Clone();
            _netTray.ToolTipText = BuildNetworkTooltip();
        }
        _micMenu?.Refresh();
        _camMenu?.Refresh();
        _netMenu?.Refresh();
        UpdateOverlay();
    }

    private void Notify(string title, string message)
    {
        try
        {
            (_micTray ?? _camTray ?? _netTray)?.ShowNotification(title, message, NotificationIcon.None, sound: false);
        }
        catch { }
    }

    /// <summary>
    /// Pops up a short-lived badge for a switch, in the device's chosen corner. If a MIC OFF / CAM OFF / NET OFF
    /// badge already sits in that corner, it appears just beside it (below at the top, above at the bottom).
    /// </summary>
    private void FlashBadge(PrivacyDevice device, (bool? Off, string Text) state)
    {
        var corner = device switch
        {
            PrivacyDevice.Camera => S.CamNotifyCorner,
            PrivacyDevice.Network => S.NetNotifyCorner,
            _ => S.MicNotifyCorner,
        };
        double offset = _overlays.TryGetValue(corner, out var overlay) && overlay.IsVisible ? overlay.ActualHeight : 0;
        if (!_notifyBadges.TryGetValue(corner, out var badge))
            _notifyBadges[corner] = badge = new NotifyBadgeWindow();
        badge.Flash(device, state.Off, state.Text, corner, offset);
    }

    // ---------------- mic actions ----------------

    public void UserToggle()
    {
        bool target = Mic.State switch
        {
            MicState.Muted => false,
            MicState.Live => true,
            _ => !Mic.DesiredMuted,
        };
        SetMutedByUser(target, notify: true);
    }

    private void SetMutedByUser(bool muted, bool notify)
    {
        bool before = Mic.DesiredMuted;
        Mic.SetMuted(muted);
        if (Mic.DesiredMuted == before && S.HotkeyMode == HotkeyMode.PushToTalk) return;

        if (S.PlaySound) Sounds.Play(Mic.DesiredMuted);
        if (notify && S.MicNotify)
        {
            if (S.MicNotifyStyle == NotifyStyle.Badge)
                FlashBadge(PrivacyDevice.Microphone, Mic.State switch
                {
                    MicState.NoDevice => (null, PrivacyToggle.Properties.Resources.NotifyBadge_NoMic),
                    MicState.Muted => (true, PrivacyToggle.Properties.Resources.NotifyBadge_MicOff),
                    _ => (false, PrivacyToggle.Properties.Resources.NotifyBadge_MicOn),
                });
            else
                Notify(PrivacyToggle.Properties.Resources.Notify_Title_PrivacyToggle, Mic.State switch
                {
                    MicState.NoDevice => PrivacyToggle.Properties.Resources.Notify_Mic_NoDevice,
                    MicState.Muted => PrivacyToggle.Properties.Resources.Notify_Mic_Muted,
                    _ => PrivacyToggle.Properties.Resources.Notify_Mic_Live,
                });
        }
    }

    private void OnMicStateChanged()
    {
        if (S.HotkeyMode == HotkeyMode.Toggle && S.LastMuted != Mic.DesiredMuted)
        {
            S.LastMuted = Mic.DesiredMuted;
            Settings.Save();
        }
        UpdateBlocked();
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Tells the usage tracker when a device is fully blocked: every microphone muted / every camera off.
    /// Apps holding a blocked device get silence or nothing, so that time isn't counted as use.
    /// (If only some mics are muted, an app could still hear through another one, so that still counts.)
    /// </summary>
    private void UpdateBlocked()
    {
        if (Usage == null) return;
        if (Mic != null)
        {
            var mics = Mic.Devices;
            Usage.SetBlocked(UsageDevice.Microphone, mics.Count > 0 && mics.All(d => d.Muted));
        }
        if (Camera != null && Camera.State != CameraState.Unavailable && !Camera.Busy)
        {
            var cams = Camera.Devices;
            Usage.SetBlocked(UsageDevice.Camera, cams.Count > 0 && cams.All(d => !d.Enabled));
        }
    }

    // ---------------- camera actions ----------------

    public async void UserToggleCamera()
    {
        if (Camera.State == CameraState.Unavailable)
        {
            Notify(PrivacyToggle.Properties.Resources.Notify_Cam_NotInstalled_Title, PrivacyToggle.Properties.Resources.Notify_Cam_NotInstalled_Body);
            return;
        }
        if (Camera.Busy) return;

        bool off = Camera.State switch
        {
            CameraState.Off => false,
            CameraState.On => true,
            _ => !Camera.DesiredOff,
        };
        // Feedback right away (like the mic); Windows takes a moment to actually switch the device.
        if (S.PlaySound && Camera.State is CameraState.On or CameraState.Off) Sounds.Play(off, camera: true);
        await Camera.SetOffAsync(off);

        // A program holds the camera and refused: ask before closing it, then try again.
        if (off && Camera.LastBlocker is { } blocker)
        {
            if (!BlockerDialog.Ask(blocker)) return;
            await Camera.CloseBlockerAndTurnOffAsync(blocker.ProcessId);
        }

        if (Camera.LastError != null)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_Camera, LocalizationService.Format(PrivacyToggle.Properties.Resources.Notify_Cam_SwitchFailed, Camera.LastError));
        else if (Camera.NeedsRestart)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_Camera, PrivacyToggle.Properties.Resources.Notify_Cam_NeedsRestart);
        else if (S.CamNotify && S.CamNotifyStyle == NotifyStyle.Badge)
            FlashBadge(PrivacyDevice.Camera, Camera.State switch
            {
                CameraState.NoDevice => (null, PrivacyToggle.Properties.Resources.NotifyBadge_NoCamera),
                CameraState.Off => (true, PrivacyToggle.Properties.Resources.NotifyBadge_CamOff),
                _ => (false, PrivacyToggle.Properties.Resources.NotifyBadge_CamOn),
            });
        else if (S.CamNotify)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_PrivacyToggle, Camera.State switch
            {
                CameraState.NoDevice => PrivacyToggle.Properties.Resources.Notify_Cam_NoDevice,
                CameraState.Off => PrivacyToggle.Properties.Resources.Notify_Cam_Off,
                _ => PrivacyToggle.Properties.Resources.Notify_Cam_On,
            });
    }

    private void OnCameraStateChanged()
    {
        if (Camera.State is CameraState.On or CameraState.Off && !Camera.Busy && S.LastCameraOff != Camera.DesiredOff)
        {
            S.LastCameraOff = Camera.DesiredOff;
            Settings.Save();
        }
        UpdateBlocked();
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- network actions ----------------

    public async void UserToggleNetwork()
    {
        if (Network.State == NetworkState.Unavailable)
        {
            Notify(PrivacyToggle.Properties.Resources.Notify_Net_NotAvailable_Title, Network.HelperOutdated
                ? PrivacyToggle.Properties.Resources.Notify_Net_NotAvailable_Update
                : PrivacyToggle.Properties.Resources.Notify_Net_NotAvailable_Install);
            return;
        }
        if (Network.Busy || Network.State == NetworkState.NoDevice) return;

        bool off = Network.State == NetworkState.On;
        // Over Remote Desktop, disconnecting also cuts this session off, with no way to reconnect from here.
        if (off && SystemParameters.IsRemoteSession && MessageBox.Show(
                PrivacyToggle.Properties.Resources.MessageBox_RemoteDisconnect_Body,
                PrivacyToggle.Properties.Resources.MessageBox_Title_Network, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (S.PlaySound) Sounds.Play(off, camera: true);
        await Network.SetOffAsync(off);

        if (Network.LastError != null)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_Network, LocalizationService.Format(PrivacyToggle.Properties.Resources.Notify_Net_SwitchFailed, Network.LastError));
        else if (Network.NeedsRestart)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_Network, PrivacyToggle.Properties.Resources.Notify_Net_NeedsRestart);
        else if (S.NetNotify && S.NetNotifyStyle == NotifyStyle.Badge)
            FlashBadge(PrivacyDevice.Network, Network.State switch
            {
                NetworkState.NoDevice => (null, PrivacyToggle.Properties.Resources.NotifyBadge_NoNetwork),
                NetworkState.Off => (true, PrivacyToggle.Properties.Resources.NotifyBadge_NetOff),
                _ => (false, PrivacyToggle.Properties.Resources.NotifyBadge_NetOn),
            });
        else if (S.NetNotify)
            Notify(PrivacyToggle.Properties.Resources.Notify_Title_PrivacyToggle, Network.State switch
            {
                NetworkState.NoDevice => PrivacyToggle.Properties.Resources.Notify_Net_NoDevice,
                NetworkState.Off => PrivacyToggle.Properties.Resources.Notify_Net_Off,
                _ => PrivacyToggle.Properties.Resources.Notify_Net_On,
            });
    }

    private void OnNetworkStateChanged()
    {
        if (Network.State is NetworkState.On or NetworkState.Off && !Network.Busy && S.LastNetworkOff != Network.DesiredOff)
        {
            S.LastNetworkOff = Network.DesiredOff;
            Settings.Save();
        }
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- hotkeys ----------------

    private void ApplyHotkeys()
    {
        Hotkeys.UnregisterAll();
        PushToTalk.Stop();
        HotkeyError = null;
        CameraHotkeyError = null;
        NetworkHotkeyError = null;

        if (_hotkeySuspended) return;

        if (S.HotkeyMode == HotkeyMode.Toggle)
        {
            if (!Hotkeys.Register(HotkeyService.MicId, S.Hotkey))
                HotkeyError = LocalizationService.Format(PrivacyToggle.Properties.Resources.Hotkey_InUse, S.Hotkey);
        }
        else if (!PushToTalk.Start(S.Hotkey))
        {
            HotkeyError = PrivacyToggle.Properties.Resources.Hotkey_PttFailed;
        }

        if (!Hotkeys.Register(HotkeyService.CameraId, S.CameraHotkey))
            CameraHotkeyError = LocalizationService.Format(PrivacyToggle.Properties.Resources.Hotkey_InUse, S.CameraHotkey);

        if (!Hotkeys.Register(HotkeyService.NetworkId, S.NetworkHotkey))
            NetworkHotkeyError = LocalizationService.Format(PrivacyToggle.Properties.Resources.Hotkey_InUse, S.NetworkHotkey);
    }

    /// <summary>Called while a Settings recorder box captures keys, so the current hotkeys don't fire.</summary>
    public void SuspendHotkey(bool suspend)
    {
        _hotkeySuspended = suspend;
        var (micError, camError, netError) = (HotkeyError, CameraHotkeyError, NetworkHotkeyError);
        ApplyHotkeys();
        if (suspend) (HotkeyError, CameraHotkeyError, NetworkHotkeyError) = (micError, camError, netError);
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    public HotkeySetting HotkeyFor(PrivacyDevice target) => target switch
    {
        PrivacyDevice.Camera => S.CameraHotkey,
        PrivacyDevice.Network => S.NetworkHotkey,
        _ => S.Hotkey,
    };

    /// <summary>Tries a new hotkey. Returns null on success, or why it was rejected (the old one stays).</summary>
    public string? TryChangeHotkey(HotkeySetting hotkey, PrivacyDevice target)
    {
        foreach (var other in Enum.GetValues<PrivacyDevice>().Where(t => t != target))
            if (hotkey.SameAs(HotkeyFor(other)))
                return LocalizationService.Format(PrivacyToggle.Properties.Resources.Hotkey_DuplicateDevice, hotkey, HotkeyDeviceLabel(other));

        if (target != PrivacyDevice.Microphone || S.HotkeyMode == HotkeyMode.Toggle)
        {
            // Probe the combination before committing to it.
            Hotkeys.UnregisterAll();
            PushToTalk.Stop();
            bool ok = Hotkeys.Register(HotkeyService.MicId, hotkey);
            Hotkeys.UnregisterAll();
            if (!ok)
            {
                ApplyHotkeys();
                return LocalizationService.Format(PrivacyToggle.Properties.Resources.Hotkey_AlreadyInUse, hotkey);
            }
        }

        switch (target)
        {
            case PrivacyDevice.Camera: S.CameraHotkey = hotkey; break;
            case PrivacyDevice.Network: S.NetworkHotkey = hotkey; break;
            default: S.Hotkey = hotkey; break;
        }
        Settings.Save();
        ApplyHotkeys();
        UpdateVisuals();
        StateChanged?.Invoke();
        return null;
    }

    public void SetHotkeyMode(HotkeyMode mode)
    {
        if (S.HotkeyMode == mode) return;
        S.HotkeyMode = mode;
        Settings.Save();
        ApplyHotkeys();
        if (mode == HotkeyMode.PushToTalk) Mic.SetMuted(true); // push-to-talk rests muted
        UpdateVisuals();
        StateChanged?.Invoke();
    }

    // ---------------- settings actions ----------------

    public void SetStartWithWindows(bool on)
    {
        S.StartWithWindows = on;
        StartupService.SetEnabled(on);
        Settings.Save();
        _micMenu?.Refresh();
        _camMenu?.Refresh();
        _netMenu?.Refresh();
        StateChanged?.Invoke();
    }

    public void SetDoubleClickOpensSettings(bool on)
    {
        S.DoubleClickOpensSettings = on;
        Settings.Save();
        ApplyClickMode();
    }

    public void SetTheme(ThemeMode mode)
    {
        S.Theme = mode;
        Settings.Save();
        ThemeService.SetMode(mode);
    }

    public void SetLanguage(UiLanguage language)
    {
        S.Language = language;
        Settings.Save();
        LocalizationService.Apply(language);
    }

    private static string HotkeyDeviceLabel(PrivacyDevice device) => device switch
    {
        PrivacyDevice.Camera => PrivacyToggle.Properties.Resources.Hotkey_Device_camera,
        PrivacyDevice.Network => PrivacyToggle.Properties.Resources.Hotkey_Device_network,
        _ => PrivacyToggle.Properties.Resources.Hotkey_Device_microphone,
    };

    public void SetShowMicTrayIcon(bool on)
    {
        S.ShowMicTrayIcon = on;
        Settings.Save();
        UpdateTrays();
    }

    public void SetShowCameraTrayIcon(bool on)
    {
        S.ShowCameraTrayIcon = on;
        Settings.Save();
        UpdateTrays();
    }

    public void SetShowNetworkTrayIcon(bool on)
    {
        S.ShowNetworkTrayIcon = on;
        Settings.Save();
        UpdateTrays();
    }

    public void SetTaskbarSpeed(bool on, TaskbarSpeedShow show)
    {
        S.TaskbarSpeed = on;
        S.TaskbarSpeedShow = show;
        Settings.Save();
        UpdateTaskbarSpeed();
    }

    /// <summary>Opens, updates or closes the taskbar speed display to match the settings.</summary>
    private void UpdateTaskbarSpeed()
    {
        if (S.TaskbarSpeed && !_disposed)
        {
            if (_taskbarSpeed == null)
            {
                // The same speed the Activity page shows, when the helper can count it; else Windows' own counters.
                _taskbarSpeed = new TaskbarSpeedWindow(S.TaskbarSpeedShow, () => NetworkUsage?.LiveTotal);
                _taskbarSpeed.Clicked += () =>
                {
                    ShowSettings();
                    _settingsWindow?.ShowTaskbarSpeedSettings();
                };
                _taskbarSpeed.Show();
            }
            else
            {
                _taskbarSpeed.SetShow(S.TaskbarSpeedShow);
            }
        }
        else
        {
            _taskbarSpeed?.Close();
            _taskbarSpeed = null;
        }
    }

    public void SetEnforceNetworkOff(bool on)
    {
        S.EnforceNetworkOff = on;
        Network.EnforceOff = on;
        Settings.Save();
    }

    public void SetNetworkTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.NetworkDeviceMode = mode;
        S.SelectedNetworkIds = selectedIds.ToList();
        Settings.Save();
        Network.SetTargets(S.NetworkDeviceMode, S.SelectedNetworkIds);
    }

    public void SetEnforceMute(bool on)
    {
        S.EnforceMute = on;
        Mic.EnforceMute = on;
        Settings.Save();
    }

    public void SetEnforceCameraOff(bool on)
    {
        S.EnforceCameraOff = on;
        Camera.EnforceOff = on;
        Settings.Save();
    }

    public void SetDeviceTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.DeviceMode = mode;
        S.SelectedDeviceIds = selectedIds.ToList();
        Settings.Save();
        Mic.SetTargets(S.DeviceMode, S.SelectedDeviceIds);
    }

    public void SetCameraTargets(DeviceMode mode, IEnumerable<string> selectedIds)
    {
        S.CameraDeviceMode = mode;
        S.SelectedCameraIds = selectedIds.ToList();
        Settings.Save();
        Camera.SetTargets(S.CameraDeviceMode, S.SelectedCameraIds);
    }

    /// <summary>
    /// Saves a device's alert choices: the switch notification (its style and, for the badge style, corner)
    /// and the on-screen OFF badge with its corner.
    /// </summary>
    public void SetAlerts(PrivacyDevice device, bool notify, NotifyStyle notifyStyle, OverlayCorner notifyCorner,
        bool overlay, OverlayCorner corner)
    {
        switch (device)
        {
            case PrivacyDevice.Microphone:
                (S.MicNotify, S.MicNotifyStyle, S.MicNotifyCorner, S.MicOverlay, S.MicOverlayCorner) =
                    (notify, notifyStyle, notifyCorner, overlay, corner);
                break;
            case PrivacyDevice.Camera:
                (S.CamNotify, S.CamNotifyStyle, S.CamNotifyCorner, S.CamOverlay, S.CamOverlayCorner) =
                    (notify, notifyStyle, notifyCorner, overlay, corner);
                break;
            default:
                (S.NetNotify, S.NetNotifyStyle, S.NetNotifyCorner, S.NetOverlay, S.NetOverlayCorner) =
                    (notify, notifyStyle, notifyCorner, overlay, corner);
                break;
        }
        Settings.Save();
        UpdateOverlay();
    }

    /// <summary>Saves simple flags that need no further action.</summary>
    public void Save() => Settings.Save();

    // ---------------- windows ----------------

    /// <summary>Opens (or brings forward) Settings; <paramref name="page"/> picks that device's page.</summary>
    public void ShowSettings(PrivacyDevice? page = null)
    {
        if (_disposed) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (page is PrivacyDevice device) _settingsWindow.ShowPage(device);
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;  // nudge to the front, then release
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    /// <summary>
    /// Shows each device's badge in its own corner. Badges that share a corner share one window
    /// (side by side), so they never overlap.
    /// </summary>
    private void UpdateOverlay()
    {
        bool micBadge = S.MicOverlay && Mic?.State == MicState.Muted;
        bool camBadge = S.CamOverlay && Camera?.State == CameraState.Off;
        bool netBadge = S.NetOverlay && Network?.State == NetworkState.Off;

        foreach (var corner in Enum.GetValues<OverlayCorner>())
        {
            bool mic = micBadge && S.MicOverlayCorner == corner;
            bool cam = camBadge && S.CamOverlayCorner == corner;
            bool net = netBadge && S.NetOverlayCorner == corner;
            _overlays.TryGetValue(corner, out var window);
            if (mic || cam || net)
            {
                if (window == null) _overlays[corner] = window = new OverlayWindow();
                window.SetBadges(mic, cam, net);
                window.Place(corner);
                if (!window.IsVisible) window.Show();
            }
            else
            {
                window?.Hide();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Push-to-talk keeps mics muted only while PrivacyToggle runs; don't leave them stuck muted.
        if (S.HotkeyMode == HotkeyMode.PushToTalk && Mic != null)
            Mic.SetMuted(false);

        _settingsWindow?.Close();
        foreach (var window in _overlays.Values) window.Close();
        foreach (var window in _notifyBadges.Values) window.Close();
        _taskbarSpeed?.Close();
        Hotkeys?.Dispose();
        PushToTalk?.Dispose();
        Mic?.Dispose();
        Camera?.Dispose();
        Network?.Dispose();
        NetworkUsage?.Dispose();
        Usage?.Dispose();
        _micTray?.Dispose(); // removes the icons so no "ghost" stays in the tray
        _camTray?.Dispose();
        _netTray?.Dispose();
        Sounds.Dispose();
        foreach (var icon in _micIcons.Values.Concat(_camIcons.Values).Concat(_netIcons.Values).Distinct()) icon.Dispose();
    }
}
