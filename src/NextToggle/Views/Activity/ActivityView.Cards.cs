using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using NextToggle.Services;

namespace NextToggle.Views;

/// <summary>"Right now": the Microphone, Camera and Network cards, the summary line, and the live announcements.</summary>
public partial class ActivityView
{
    /// <summary>An app counts as using the network now above this rate (bytes/s); idle keep-alives stay below it.</summary>
    public const double NetworkActiveRate = 1024;

    // The design asks for 270px; 240 still fits every card's content and keeps three across on the default 1080px window.
    private const double MinCardWidth = 240, CardGap = 12;

    private ResourceCard _micCard = null!, _camCard = null!, _netCard = null!;

    // Rows are rebuilt only when the set of apps changes; timers and rates update in place every second.
    private string _micKeys = "", _camKeys = "", _netKeys = "";
    private readonly List<LiveRow> _micRows = new(), _camRows = new(), _netRows = new();
    private HashSet<string>? _announced; // "Microphone|app" keys already announced; null until the first refresh

    /// <summary>One app line in a card, with the parts that change every second.</summary>
    private sealed class LiveRow
    {
        public required string AppKey { get; init; }
        public required FrameworkElement Element { get; init; }
        public TextBlock? Timer, Rate, UpRate;
        public Border? NewBadge;
        public LevelMeter? Meter;
        public DateTime Start;
    }

    private void BuildCards()
    {
        _micCard = new ResourceCard(NextToggle.Properties.Resources.Activity_Lane_Microphone, Glyphs.Device(UsageDevice.Microphone, 18), "MicUsage", "MicOnText");
        _camCard = new ResourceCard(NextToggle.Properties.Resources.Activity_Lane_Camera, Glyphs.Device(UsageDevice.Camera, 18), "CamUsage", "CamOnText");
        _netCard = new ResourceCard(NextToggle.Properties.Resources.Activity_Lane_Network, Glyphs.Transfer(18), "NetUsage", "NetOnText");
        _micCard.ActionClicked += () => _app?.UserToggle();
        _camCard.ActionClicked += () => _app?.UserToggleCamera();
        _netCard.ActionClicked += () => _app?.UserToggleNetwork();
        AlignCards();
        Cards.Children.Add(_micCard);
        Cards.Children.Add(_camCard);
        Cards.Children.Add(_netCard);
    }

    /// <summary>
    /// Three columns on a wide window, fewer when narrow (every card at least <see cref="MinCardWidth"/> wide).
    /// Cards stretch to their row's height, so they're the same height side by side.
    /// </summary>
    private void LayoutCards()
    {
        double width = Cards.ActualWidth;
        if (width <= 0) return;
        int columns = Math.Clamp((int)((width + 0.5) / (MinCardWidth + CardGap)), 1, 3);
        if (Cards.ColumnDefinitions.Count == columns) return;
        Cards.ColumnDefinitions.Clear();
        Cards.RowDefinitions.Clear();
        for (int c = 0; c < columns; c++) Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cards = new[] { _micCard, _camCard, _netCard };
        for (int r = 0; r < (cards.Length + columns - 1) / columns; r++) Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i], i % columns);
            Grid.SetRow(cards[i], i / columns);
        }
    }

    private void CollapseCards()
    {
        _micCard.CollapseRows();
        _camCard.CollapseRows();
        _netCard.CollapseRows();
        AlignCards();
    }

    /// <summary>
    /// Cards never stretch to their row: collapsed ones are the same height anyway (each reserves room for two
    /// apps), and an expanded one is exactly as tall as its own apps, whatever the cards beside it show.
    /// </summary>
    private void AlignCards()
    {
        foreach (var card in new[] { _micCard, _camCard, _netCard }) card.VerticalAlignment = VerticalAlignment.Top;
    }

    private void RefreshCards(DateTime now)
    {
        var usage = _app!.Usage;
        var live = ActivityModel.LiveNow(usage.Sessions, usage.Blocked, now);
        var mic = LiveFor(live, UsageDevice.Microphone);
        var cam = LiveFor(live, UsageDevice.Camera);
        var net = _app.NetworkUsage.Live.Where(l => l.DownRate + l.UpRate > NetworkActiveRate)
            .OrderByDescending(l => l.DownRate + l.UpRate).ToList();

        RefreshMicCard(mic, now);
        RefreshCamCard(cam, now);
        RefreshNetCard(net);
        RefreshSummary(mic.Count, cam.Count, net.Count);
        Announce(mic, cam);
    }

    /// <summary>Live spans of one device, newest first: (app, start, uncertain).</summary>
    private static List<(string AppKey, DateTime Start, bool Uncertain)> LiveFor(
        List<(string AppKey, List<UsageSpan> Spans)> live, UsageDevice device) =>
        live.SelectMany(l => l.Spans.Where(s => s.Device == device).Select(s => (l.AppKey, s.Start, s.Uncertain)))
            .GroupBy(x => x.AppKey).Select(g => g.OrderBy(x => x.Start).First())
            .OrderByDescending(x => x.Start).ToList();

    // ---------------- microphone ----------------

    private void RefreshMicCard(List<(string AppKey, DateTime Start, bool Uncertain)> live, DateTime now)
    {
        var usage = _app!.Usage;
        int n = live.Count;
        _micCard.SetLive(n > 0);
        _micCard.SetPill(n > 0 ? LivePill(n) : NextToggle.Properties.Resources.ActivityDyn_Pill_Off, n > 0);

        string keys = string.Join("|", live.Select(l => l.AppKey));
        if (keys != _micKeys)
        {
            _micKeys = keys;
            _micRows.Clear();
            foreach (var (appKey, start, _) in live) _micRows.Add(DeviceRow(appKey, start, null, meter: true));
            _micCard.SetRows(_micRows.Select(r => r.Element).ToList());
        }
        UpdateTimers(_micRows, now);

        _micCard.SetIdle(n > 0 ? null : IdleText(UsageDevice.Microphone, now));
        var (total, apps) = ActivityModel.DeviceToday(usage.Sessions, usage.Blocked, now, UsageDevice.Microphone);
        var state = _app.Mic.State;
        // The microphone is either live or muted.
        string action = state == MicState.NoDevice ? NextToggle.Properties.Resources.ActivityDyn_MicAction_NoMic
            : state == MicState.Muted ? NextToggle.Properties.Resources.ActivityDyn_MicAction_Unmute : NextToggle.Properties.Resources.ActivityDyn_MicAction_Mute;
        _micCard.SetFooter(Duration(total), AppsSuffix(apps), action, state != MicState.NoDevice);
    }

    // ---------------- camera ----------------

    private void RefreshCamCard(List<(string AppKey, DateTime Start, bool Uncertain)> live, DateTime now)
    {
        var usage = _app!.Usage;
        int n = live.Count;
        _camCard.SetLive(n > 0);
        _camCard.SetPill(n > 0 ? LivePill(n) : NextToggle.Properties.Resources.ActivityDyn_Pill_Off, n > 0);

        string keys = string.Join("|", live.Select(l => l.AppKey + (l.Uncertain ? "?" : "")));
        if (keys != _camKeys)
        {
            _camKeys = keys;
            _camRows.Clear();
            // The camera's name when there's only one; with several it can't be told which one an app uses.
            var enabled = _app.Camera.Devices.Where(d => d.Enabled).ToList();
            string? device = enabled.Count == 1 ? enabled[0].Name : null;
            foreach (var (appKey, start, uncertain) in live)
                _camRows.Add(DeviceRow(appKey, start, uncertain ? NextToggle.Properties.Resources.ActivityDyn_CamConnectedUncertain : device, meter: false));
            _camCard.SetRows(_camRows.Select(r => r.Element).ToList());
        }
        UpdateTimers(_camRows, now);

        _camCard.SetIdle(n > 0 ? null : IdleText(UsageDevice.Camera, now));
        var (total, apps) = ActivityModel.DeviceToday(usage.Sessions, usage.Blocked, now, UsageDevice.Camera);
        var state = _app.Camera.State;
        bool usable = state is CameraState.On or CameraState.Off && !_app.Camera.Busy;
        string action = state switch
        {
            CameraState.Unavailable => NextToggle.Properties.Resources.ActivityDyn_CamAction_NotInstalled,
            CameraState.NoDevice => NextToggle.Properties.Resources.ActivityDyn_CamAction_NoCamera,
            CameraState.Off => NextToggle.Properties.Resources.ActivityDyn_CamAction_TurnOn,
            _ => NextToggle.Properties.Resources.ActivityDyn_CamAction_TurnOff,
        };
        _camCard.SetFooter(Duration(total), AppsSuffix(apps), action, usable);
    }

    /// <summary>"Off · last used by Visual Studio Code, 1 min ago".</summary>
    private string IdleText(UsageDevice device, DateTime now)
    {
        var usage = _app!.Usage;
        var last = ActivityModel.LastUse(usage.Sessions, usage.Blocked, now, device);
        return last is { } l
            ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_OffLastUsed, AppIdentity.For(l.AppKey).Name, ActivityModel.Ago(l.End, now))
            : LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_OffNoUse7Days,
                device == UsageDevice.Camera ? NextToggle.Properties.Resources.ActivityDyn_Device_camera : NextToggle.Properties.Resources.ActivityDyn_Device_microphone);
    }

    /// <summary>A microphone or camera row: icon, name (+ NEW), a subtitle or level meter, and a running timer.</summary>
    private LiveRow DeviceRow(string appKey, DateTime start, string? subtitle, bool meter)
    {
        var app = AppIdentity.For(appKey);
        var grid = new Grid { Height = ResourceCard.RowHeight, Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(AppIcon(app, 26));

        var text = new StackPanel { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = app.Detail });
        var badge = SmallBadge(NextToggle.Properties.Resources.Activity_Badge_New, "AccentSoft", "AccentText");
        badge.Margin = new Thickness(6, 0, 0, 0);
        nameRow.Children.Add(badge);
        text.Children.Add(nameRow);

        var row = new LiveRow { AppKey = appKey, Element = grid, Start = start, NewBadge = badge };
        if (meter)
        {
            row.Meter = new LevelMeter { Margin = new Thickness(0, 6, 0, 0) };
            row.Meter.SetResourceReference(LevelMeter.BarBrushProperty, "MicUsage");
            text.Children.Add(row.Meter);
        }
        else if (subtitle != null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            text.Children.Add(sub);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Timer = ResourceCard.Tabular(new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(row.Timer, 2);
        grid.Children.Add(row.Timer);
        return row;
    }

    private static void UpdateTimers(List<LiveRow> rows, DateTime now)
    {
        foreach (var row in rows)
        {
            var d = now - row.Start;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            row.Timer!.Text = d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes:00}:{d.Seconds:00}";
            row.NewBadge!.Visibility = d < TimeSpan.FromSeconds(60) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------- network ----------------

    private void RefreshNetCard(List<LiveTraffic> live)
    {
        var usage = _app!.NetworkUsage;
        int n = live.Count;
        bool off = _app.Network.State == NetworkState.Off;
        _netCard.SetLive(n > 0);
        _netCard.SetPill(off ? NextToggle.Properties.Resources.ActivityDyn_Pill_Off : !usage.Available ? NextToggle.Properties.Resources.ActivityDyn_Pill_Unavailable
            : n > 0 ? NetAppsPill(n) : NextToggle.Properties.Resources.ActivityDyn_Pill_Quiet, n > 0);

        if (n > 0)
        {
            var rates = new StackPanel { Orientation = Orientation.Horizontal };
            rates.Children.Add(RateText("↓ " + Rate(live.Sum(l => l.DownRate)), "DownloadText", 14));
            var up = RateText("↑ " + Rate(live.Sum(l => l.UpRate)), "UploadText", 14);
            up.Margin = new Thickness(14, 0, 0, 0);
            rates.Children.Add(up);
            _netCard.SetSubHeader(rates);
        }
        else
        {
            _netCard.SetSubHeader(null);
        }

        string keys = string.Join("|", live.Select(l => l.AppKey));
        if (keys != _netKeys)
        {
            _netKeys = keys;
            _netRows.Clear();
            foreach (var l in live) _netRows.Add(NetworkRow(l.AppKey));
            _netCard.SetRows(_netRows.Select(r => r.Element).ToList());
        }
        foreach (var row in _netRows)
        {
            var l = live.First(x => x.AppKey == row.AppKey);
            // Each direction only when it's actually moving; an upload-only app shows just ↑.
            bool down = l.DownRate >= NetworkActiveRate / 2, up = l.UpRate >= NetworkActiveRate / 2;
            row.Rate!.Text = down || !up ? "↓ " + Rate(l.DownRate) : "";
            row.UpRate!.Text = up ? "↑ " + Rate(l.UpRate) : "";
            row.Rate.Visibility = row.Rate.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        _netCard.SetIdle(n > 0 ? null
            : off ? NextToggle.Properties.Resources.ActivityDyn_NetIdle_Off
            : !usage.Available ? usage.Problem
            : NetworkIdleText());
        var today = usage.Totals(1);
        long bytes = today.Sum(t => t.Traffic.Total);
        // Turns the whole network (Wi-Fi and wired) off or back on, like the Network page's switch.
        var state = _app.Network.State;
        string action = state switch
        {
            NetworkState.Off => NextToggle.Properties.Resources.ActivityDyn_NetAction_Connect,
            NetworkState.On => NextToggle.Properties.Resources.ActivityDyn_NetAction_Disconnect,
            NetworkState.NoDevice => NextToggle.Properties.Resources.ActivityDyn_NetAction_NoAdapter,
            _ => NextToggle.Properties.Resources.ActivityDyn_NetAction_NotInstalled,
        };
        _netCard.SetFooter(Bytes(bytes), AppsSuffix(today.Count), action,
            state is NetworkState.On or NetworkState.Off && !_app.Network.Busy);
    }

    private (DateTime At, NetworkSession? Session) _lastNet = (DateTime.MinValue, null);

    /// <summary>"No app is sending or receiving data right now. Last: Steam, 4 min ago (↓ 1.1 GB)".</summary>
    private string NetworkIdleText()
    {
        var now = DateTime.Now;
        // Finding the last session scans the week's history; once every 30 seconds is plenty.
        if (now - _lastNet.At > TimeSpan.FromSeconds(30)) _lastNet = (now, _app!.NetworkUsage.LastSession());
        var quiet = NextToggle.Properties.Resources.ActivityDyn_NetIdle_Quiet;
        return _lastNet.Session is { } s
            ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_NetIdle_Last, quiet, AppIdentity.For(s.AppKey).Name,
                ActivityModel.Ago(s.End, now), Bytes(s.Received))
            : quiet;
    }

    private LiveRow NetworkRow(string appKey)
    {
        var app = AppIdentity.For(appKey);
        var grid = new Grid { Height = ResourceCard.RowHeight, Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AppIcon(app, 26));

        var name = new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = app.Detail };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var rates = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var down = RateText("", "DownloadText", 13);
        down.HorizontalAlignment = HorizontalAlignment.Right;
        var up = RateText("", "UploadText", 11);
        up.HorizontalAlignment = HorizontalAlignment.Right;
        rates.Children.Add(down);
        rates.Children.Add(up);
        Grid.SetColumn(rates, 2);
        grid.Children.Add(rates);
        return new LiveRow { AppKey = appKey, Element = grid, Rate = down, UpRate = up };
    }

    private static TextBlock RateText(string text, string brushKey, double size)
    {
        var t = ResourceCard.Tabular(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.Bold });
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    // ---------------- summary + announcements ----------------

    private void RefreshSummary(int mic, int cam, int net)
    {
        var parts = new List<string>();
        if (mic > 0) parts.Add(LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Summary_OnMic, mic));
        if (cam > 0) parts.Add(LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Summary_OnCamera, cam));
        if (mic == 0 && cam == 0)
            NowSummary.Text = net > 1
                ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Summary_MicCamOff_NetApps, net)
                : net == 1
                    ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Summary_MicCamOff_NetApp, net)
                    : NextToggle.Properties.Resources.ActivityDyn_Summary_MicCamOff_Quiet;
        else
        {
            if (net > 0) parts.Add(LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Summary_OnNetwork, net));
            NowSummary.Text = string.Join(" · ", parts);
        }
        NowDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, cam > 0 ? "CamUsage" : mic > 0 ? "MicUsage" : "StatusOn");
        SetPulse(NowDot, mic > 0 || cam > 0 || net > 0);
    }

    /// <summary>Tells screen readers (once) when an app starts using the microphone or camera; never per tick.</summary>
    private void Announce(List<(string AppKey, DateTime Start, bool Uncertain)> mic, List<(string AppKey, DateTime Start, bool Uncertain)> cam)
    {
        var current = mic.Select(m => "microphone|" + m.AppKey).Concat(cam.Select(c => "camera|" + c.AppKey)).ToHashSet();
        if (_announced != null)
        {
            var started = current.Except(_announced).ToList();
            if (started.Count > 0)
            {
                LiveAnnouncer.Text = string.Join(". ", started.Select(k =>
                {
                    var parts = k.Split('|', 2);
                    string device = parts[0] == "camera" ? NextToggle.Properties.Resources.ActivityDyn_Announce_camera : NextToggle.Properties.Resources.ActivityDyn_Announce_microphone;
                    return LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Announce, AppIdentity.For(parts[1]).Name, device);
                }));
                AutomationProperties.SetLiveSetting(LiveAnnouncer, AutomationLiveSetting.Polite);
                (UIElementAutomationPeer.FromElement(LiveAnnouncer) ?? UIElementAutomationPeer.CreatePeerForElement(LiveAnnouncer))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
        _announced = current;
    }

    // ---------------- building blocks ----------------

    private static string LivePill(int n) => n == 1
        ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Pill_LiveApps, n)
        : LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Pill_LiveApps_Plural, n);

    private static string NetAppsPill(int n) => n == 1
        ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Pill_Apps, n)
        : LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Pill_Apps_Plural, n);

    private static string AppsSuffix(int apps) => apps == 1
        ? LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Card_Apps, apps)
        : LocalizationService.Format(NextToggle.Properties.Resources.ActivityDyn_Card_Apps_Plural, apps);

    /// <summary>A small rounded label such as NEW or LIVE.</summary>
    private static Border SmallBadge(string text, string background, string foreground)
    {
        var label = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.Black };
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Child = label, VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, background);
        return badge;
    }

    /// <summary>Real app icon, or a lettered tile when there's none; rounded like the design.</summary>
    private static FrameworkElement AppIcon(AppIdentity app, double size)
    {
        if (app.Icon != null)
        {
            var image = new Image { Source = app.Icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.28, size * 0.28);
            return image;
        }
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontWeight = FontWeights.Black, FontSize = size * 0.45,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ((TextBlock)tile.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        tile.SetResourceReference(Border.BackgroundProperty, "Accent");
        return tile;
    }

    /// <summary>"2h 57m", "24m", "0m".</summary>
    private static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m";

    /// <summary>"640 KB/s" under 1000 KB/s, else "8.4 MB/s".</summary>
    public static string Rate(double bytesPerSecond)
    {
        double kb = bytesPerSecond / 1024;
        return kb < 1000 ? $"{kb:0} KB/s" : $"{kb / 1024:0.0} MB/s";
    }

    /// <summary>"350 KB", "42 MB", "1.9 GB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
