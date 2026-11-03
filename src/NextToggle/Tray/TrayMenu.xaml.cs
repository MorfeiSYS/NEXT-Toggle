using System.Windows;
using System.Windows.Controls;
using NextToggle.Services;

namespace NextToggle.Tray;

public partial class TrayMenu : ContextMenu
{
    private readonly AppController _app;

    public TrayMenu(AppController app)
    {
        _app = app;
        InitializeComponent();
        Opened += (_, _) => Refresh();
    }

    public void Refresh()
    {
        ToggleItem.Header = _app.Mic.State == MicState.Muted ? NextToggle.Properties.Resources.Tray_UnmuteMicrophone : NextToggle.Properties.Resources.Tray_MuteMicrophone;

        var cam = _app.Camera;
        CameraItem.Header = cam.Busy ? NextToggle.Properties.Resources.Tray_CameraSwitching
            : cam.State switch
            {
                CameraState.Off => NextToggle.Properties.Resources.Tray_TurnCameraOn,
                CameraState.On => NextToggle.Properties.Resources.Tray_TurnCameraOff,
                CameraState.NoDevice => NextToggle.Properties.Resources.Tray_NoCameraFound,
                _ => NextToggle.Properties.Resources.Tray_CameraNotInstalled,
            };
        CameraItem.IsEnabled = !cam.Busy && cam.State is CameraState.On or CameraState.Off;

        var net = _app.Network;
        NetworkItem.Header = net.Busy ? NextToggle.Properties.Resources.Tray_NetworkSwitching
            : net.State switch
            {
                NetworkState.Off => NextToggle.Properties.Resources.Tray_ReconnectNetwork,
                NetworkState.On => NextToggle.Properties.Resources.Tray_DisconnectNetwork,
                NetworkState.NoDevice => NextToggle.Properties.Resources.Tray_NoNetworkAdapter,
                _ => NextToggle.Properties.Resources.Tray_NetworkNotAvailable,
            };
        NetworkItem.IsEnabled = !net.Busy && net.State is NetworkState.On or NetworkState.Off;

        StartupItem.IsChecked = _app.Settings.Current.StartWithWindows;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => _app.UserToggle();
    private void Camera_Click(object sender, RoutedEventArgs e) => _app.UserToggleCamera();
    private void Network_Click(object sender, RoutedEventArgs e) => _app.UserToggleNetwork();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
    private void Startup_Click(object sender, RoutedEventArgs e) => _app.SetStartWithWindows(StartupItem.IsChecked);
    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
