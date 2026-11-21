using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace VPNSL.Windows;

public partial class MainWindow
{
    private readonly KvnAwgTunnel _awgTunnel = new();
    private bool _awgBusy;
    private bool _loadingKvnMode;

    private bool IsAwgMode =>
        string.Equals((TransportSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "AWG", StringComparison.OrdinalIgnoreCase);

    private void InitializeKvnMode()
    {
        _loadingKvnMode = true;
        try
        {
            SetComboByTag(TransportSelector, _settings.KvnTransport);
            AwgProfileBox.Text = _settings.KvnAwgProfilePath;
        }
        finally { _loadingKvnMode = false; }
        RenderKvnMode();
    }

    private void TransportSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingKvnMode || !IsLoaded) return;
        if (_engine.IsActive || _awgTunnel.IsActive || _awgBusy)
        {
            _loadingKvnMode = true;
            try { SetComboByTag(TransportSelector, _settings.KvnTransport); }
            finally { _loadingKvnMode = false; }
            return;
        }
        _settings.KvnTransport = IsAwgMode ? "AWG" : "CSQTT";
        SettingsStore.Save(_settings);
        RenderKvnMode();
    }

    private void RenderKvnMode()
    {
        _loadingKvnMode = true;
        try
        {
            AwgModeRadio.IsChecked = IsAwgMode;
            CsqttModeRadio.IsChecked = !IsAwgMode;
        }
        finally { _loadingKvnMode = false; }
        AwgProfilePanel.Visibility = IsAwgMode ? Visibility.Visible : Visibility.Collapsed;
        CsqttTransportHint.Visibility = IsAwgMode ? Visibility.Collapsed : Visibility.Visible;
        RefreshSimpleProfiles();
        if (!IsAwgMode) RefreshConnectionSummary();
    }

    private void BrowseAwgProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsActive || _awgTunnel.IsActive || _awgBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Выберите конфигурацию AmneziaWG 3.1",
            Filter = "AmneziaWG (*.conf)|*.conf",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AwgProfileBox.Text = dialog.FileName;
            _settings.KvnAwgProfilePath = dialog.FileName;
            SettingsStore.Save(_settings);
        }
    }

    private async Task ToggleAwgAsync()
    {
        if (_awgBusy || _shutdownStarted) return;
        _awgBusy = true;
        ConnectButton.IsEnabled = false;
        TransportSelector.IsEnabled = false;
        try
        {
            if (_awgTunnel.IsActive)
            {
                SetStatus("Отключение…");
                await _awgTunnel.DisconnectAsync();
                AppendLog("[КВН/AWG] Собственная служба туннеля удалена");
                SetStatus("Отключено");
            }
            else
            {
                if (_engine.IsActive)
                    throw new InvalidOperationException("Сначала отключите CSQTT.");
                EnsureSelectedProfileReady();
                SaveSettingsFromUi();
                SetStatus("Подключение");
                await _awgTunnel.ConnectAsync(_settings.KvnAwgProfilePath);
                AppendLog("[КВН/AWG] Служба AWG запущена. Интернет и IPv6 проверяются отдельно.");
                SetStatus("Туннель запущен");
            }
        }
        catch (Exception ex)
        {
            AppendLog("[КВН/AWG] Ошибка: " + ex.Message);
            SetStatus(_awgTunnel.IsActive ? "Туннель запущен" : "Отключено");
            if (!_shutdownStarted)
                MessageBox.Show(this, ex.Message, "КВН — AmneziaWG",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _awgBusy = false;
            ConnectButton.IsEnabled = !_shutdownStarted;
            var canChange = !_shutdownStarted && !_engine.IsActive && !_awgTunnel.IsActive;
            TransportSelector.IsEnabled = canChange;
            AwgModeRadio.IsEnabled = canChange;
            CsqttModeRadio.IsEnabled = canChange;
            ActiveProfileSelector.IsEnabled = canChange;
        }
    }
}
