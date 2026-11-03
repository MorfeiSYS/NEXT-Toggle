using System.Windows;
using System.Windows.Media.Imaging;
using PrivacyToggle.Helper;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>Asks whether to close the program that keeps the camera from turning off.</summary>
public partial class BlockerDialog : Window
{
    public BlockerDialog(BlockerInfo blocker)
    {
        InitializeComponent();
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/cam-on.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Glyph.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        Message.Text = LocalizationService.Format(PrivacyToggle.Properties.Resources.Blocker_Message, blocker.Name);
        Note.Text = blocker.IsService ? PrivacyToggle.Properties.Resources.Blocker_Note_Service : PrivacyToggle.Properties.Resources.Blocker_Note_App;

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
    }

    /// <summary>Shows the dialog in front of everything; true if the user chose to close the program.</summary>
    public static bool Ask(BlockerInfo blocker)
    {
        var dialog = new BlockerDialog(blocker);
        dialog.Loaded += (_, _) => dialog.Activate();
        return dialog.ShowDialog() == true;
    }

    private void DragStrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
