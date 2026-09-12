using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using JadeAudioControl.Cloud;
using JadeAudioControl.Protocol;

namespace JadeAudioControl;

/// <summary>
/// Names the bands currently on the device and writes them to the signed-in
/// account. Sharing publicly is a separate, opt-in tick: saving is private
/// unless the person says otherwise.
/// </summary>
public partial class SavePresetWindow : Window
{
    private readonly CloudClient _cloud;
    private readonly IReadOnlyList<Band> _bands;
    private readonly double _globalGain;
    private readonly int _deviceType;

    public SavePresetWindow(
        CloudClient cloud, string deviceName, int deviceType,
        IReadOnlyList<Band> bands, double globalGain)
    {
        InitializeComponent();
        _cloud = cloud;
        _bands = bands;
        _globalGain = globalGain;
        _deviceType = deviceType;

        Subtitle.Text = $"{bands.Count} bands from {deviceName}, saved as {cloud.UserName}.";
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "Give the preset a name.";
            return;
        }

        bool share = ShareBox.IsChecked == true;
        if (share && MessageBox.Show(
                $"Publish '{name}' to the community list?\n\n" +
                "Anyone browsing Handpick for this device will see it, along with your " +
                "FiiO display name.",
                "Jade Audio Control", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SaveButton.IsEnabled = false;
        SaveButton.Content = "Saving...";
        try
        {
            await _cloud.SavePresetAsync(name, DescriptionBox.Text, _deviceType, _globalGain, _bands, share);
            DialogResult = true;
            Close();
        }
        catch (Exception exc)
        {
            ErrorText.Text = exc.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Save";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
