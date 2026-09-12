using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using JadeAudioControl.Cloud;

namespace JadeAudioControl;

/// <summary>
/// Collects the user's own FiiO credentials.
///
/// The password is handed to <see cref="CloudClient.LoginAsync"/>, forwarded to
/// FiiO's token endpoint and then dropped; nothing here stores it. The CAPTCHA
/// is shown as an image for the person to read.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly CloudClient _cloud;
    private string _captchaToken = "";

    public LoginWindow(CloudClient cloud)
    {
        InitializeComponent();
        _cloud = cloud;
        Loaded += async (_, _) =>
        {
            UsernameBox.Focus();
            await RefreshCaptchaAsync();
        };
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        if (RevealBox.IsChecked == true)
        {
            PasswordPlain.Text = PasswordBox.Password;
            PasswordPlain.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordBox.Password = PasswordPlain.Text;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordPlain.Visibility = Visibility.Collapsed;
        }
    }

    private string CurrentPassword =>
        RevealBox.IsChecked == true ? PasswordPlain.Text : PasswordBox.Password;

    private async void Captcha_Click(object sender, RoutedEventArgs e) => await RefreshCaptchaAsync();

    private async Task RefreshCaptchaAsync()
    {
        CaptchaStatus.Text = "loading";
        CaptchaImage.Source = null;
        CaptchaBox.Clear();

        try
        {
            var (bytes, token) = await _cloud.CaptchaAsync();
            _captchaToken = token;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            CaptchaImage.Source = image;
            CaptchaStatus.Text = "";
        }
        catch (Exception exc)
        {
            CaptchaStatus.Text = "failed";
            ErrorText.Text = exc.Message;
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (AgreeBox.IsChecked != true)
        {
            ErrorText.Text = "Tick the agreement box first.";
            return;
        }

        string username = UsernameBox.Text.Trim();
        string password = CurrentPassword;
        string captcha = CaptchaBox.Text.Trim();
        if (username.Length == 0 || password.Length == 0 || captcha.Length == 0)
        {
            ErrorText.Text = "Username, password and verification code are all needed.";
            return;
        }

        SignInButton.IsEnabled = false;
        SignInButton.Content = "Signing in...";
        try
        {
            _cloud.RememberMe = RememberBox.IsChecked == true;
            await _cloud.LoginAsync(username, password, captcha, _captchaToken);
            DialogResult = true;
            Close();
        }
        catch (Exception exc)
        {
            ErrorText.Text = exc.Message;
            PasswordBox.Clear();
            PasswordPlain.Clear();
            await RefreshCaptchaAsync();
        }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInButton.Content = "Sign in";
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void Register_Click(object sender, RoutedEventArgs e) => OpenUrl(CloudClient.RegisterUrl);

    private void Privacy_Click(object sender, RoutedEventArgs e) => OpenUrl(CloudClient.PrivacyUrl);

    private void Agreement_Click(object sender, RoutedEventArgs e) => OpenUrl(CloudClient.AgreementUrl);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
