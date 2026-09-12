using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace JadeAudioControl;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The protocol and the preset service both speak invariant numbers, so
        // pin the UI to them too: a comma decimal separator would otherwise
        // make typed values and displayed values disagree.
        var culture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        // .NET Framework negotiates whatever the OS default allows, which on an
        // un-patched Windows 7/8.1 still excludes TLS 1.2 - and FiiO's servers
        // require it. Ask for it explicitly.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Jade Audio Control",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
