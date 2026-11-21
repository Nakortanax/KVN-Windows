using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace VPNSL.Windows;

/// <summary>
/// Simple profile-first UI. Technical CSQTT parameters remain on an optional
/// advanced page and are never required for normal connecting.
/// </summary>
public partial class MainWindow
{
    private bool _loadingProfiles;

    private bool CanEditProfiles()
    {
        if (_engine.IsActive || _awgTunnel.IsActive || _awgBusy || _shutdownStarted)
        {
            MessageBox.Show(this, "Сначала отключите VPN.", "КВН",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private void RefreshSimpleProfiles()
    {
        _loadingProfiles = true;
        try
        {
            _settings.KvnProfiles ??= [];
            ProfilesList.ItemsSource = null;
            ProfilesList.ItemsSource = _settings.KvnProfiles.ToList();

            var kind = IsAwgMode ? "AWG" : "CSQTT";
            var profiles = _settings.KvnProfiles.Where(p => p.Transport == kind).ToList();
            ActiveProfileSelector.ItemsSource = null;
            ActiveProfileSelector.ItemsSource = profiles;
            var selected = profiles.FirstOrDefault(p => p.Id == _settings.KvnSelectedProfileId)
                           ?? profiles.FirstOrDefault();
            ActiveProfileSelector.SelectedItem = selected;
            EmptyProfileHint.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
            if (selected is not null)
            {
                // Switching protocol chooses a valid profile for the new mode.
                // The actual secrets are decrypted only when selecting/connecting.
                _settings.KvnSelectedProfileId = selected.Id;
                _settings.KvnAwgProfilePath = selected.Transport == "AWG"
                    ? selected.SourcePath : _settings.KvnAwgProfilePath;
                if (selected.Transport == "AWG") AwgProfileBox.Text = selected.SourcePath;
            }
        }
        finally { _loadingProfiles = false; }
    }

    private void KvnRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingKvnMode || !IsLoaded || sender is not RadioButton button) return;
        var mode = button.Tag?.ToString();
        if (mode is not ("AWG" or "CSQTT")) return;
        if (_engine.IsActive || _awgTunnel.IsActive || _awgBusy)
        {
            _loadingKvnMode = true;
            try
            {
                AwgModeRadio.IsChecked = IsAwgMode;
                CsqttModeRadio.IsChecked = !IsAwgMode;
            }
            finally { _loadingKvnMode = false; }
            return;
        }
        SetComboByTag(TransportSelector, mode);
    }

    private void ActiveProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || !IsLoaded) return;
        if (_engine.IsActive || _awgTunnel.IsActive || _awgBusy)
        {
            RefreshSimpleProfiles();
            return;
        }
        if (ActiveProfileSelector.SelectedItem is not KvnProfileEntry selected) return;
        try
        {
            ApplySelectedProfile(selected);
            _settings.KvnSelectedProfileId = selected.Id;
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть профиль: " + ex.Message, "КВН",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySelectedProfile(KvnProfileEntry selected)
    {
        if (selected.Transport == "AWG")
        {
            _settings.KvnAwgProfilePath = selected.SourcePath;
            AwgProfileBox.Text = selected.SourcePath;
            return;
        }
        var secret = KvnProfileStore.LoadCsqtt(selected);
        // Populate the internal CSQTT backend fields, never show them on home.
        _settings.Peer = secret.Endpoint;
        _settings.Password = secret.Password;
        _settings.VkHashes = secret.VkHashes;
        PeerBox.Text = secret.Endpoint;
        PasswordBox.Password = secret.Password;
        VkHashesBox.Text = secret.VkHashes;
    }

    private void EnsureSelectedProfileReady()
    {
        if (ActiveProfileSelector.SelectedItem is not KvnProfileEntry selected)
            throw new InvalidOperationException("Сначала импортируйте и выберите конфигурацию.");
        if (selected.Transport != (IsAwgMode ? "AWG" : "CSQTT"))
            throw new InvalidOperationException("Выбран профиль другого протокола.");
        ApplySelectedProfile(selected);
        if (selected.Transport == "AWG" && !File.Exists(selected.SourcePath))
            throw new FileNotFoundException("Файл конфигурации AWG больше не найден. Импортируйте его снова.");
        _settings.KvnSelectedProfileId = selected.Id;
    }

    private void ImportAwgProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditProfiles()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить AmneziaWG 3.1",
            Filter = "AmneziaWG (*.conf)|*.conf",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = Path.GetFullPath(dialog.FileName);
            var file = new FileInfo(path);
            if (file.Length is < 100 or > 65536)
                throw new InvalidOperationException("Недопустимый размер конфигурации AWG.");
            // Full-tunnel validation happens in KvnAwgTunnel before connecting.
            var name = Path.GetFileNameWithoutExtension(path);
            if (_settings.KvnProfiles.Any(p => p.Transport == "AWG" &&
                string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Эта конфигурация уже добавлена.");
            var entry = new KvnProfileEntry
            {
                Name = name.Length > 48 ? name[..48] : name,
                Transport = "AWG",
                SourcePath = path,
            };
            _settings.KvnProfiles.Add(entry);
            _settings.KvnSelectedProfileId = entry.Id;
            SettingsStore.Save(_settings);
            SetComboByTag(TransportSelector, "AWG");
            RenderKvnMode();
            ProfilesList.SelectedItem = entry;
            ProfileInfoText.Text = "Конфигурация AWG добавлена. Ключи не копировались в репозиторий или ZIP.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Импорт AWG", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportCsqttProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditProfiles()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить конфигурацию CSQTT из Android-КВН",
            Filter = "КВН CSQTT (*.kvn)|*.kvn",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        KvnProfileEntry? imported = null;
        try
        {
            imported = KvnProfileStore.ImportCsqtt(dialog.FileName);
            _settings.KvnProfiles.Add(imported);
            _settings.KvnSelectedProfileId = imported.Id;
            SettingsStore.Save(_settings);
            SetComboByTag(TransportSelector, "CSQTT");
            RefreshSimpleProfiles();
            ProfilesList.SelectedItem = imported;
            ProfileInfoText.Text = "Профиль CSQTT импортирован. Пароль и VK-хеши защищены Windows DPAPI.";
        }
        catch (Exception ex)
        {
            if (imported is not null)
            {
                _settings.KvnProfiles.Remove(imported);
                KvnProfileStore.RemoveCsqtt(imported);
            }
            MessageBox.Show(this, ex.Message, "Импорт CSQTT",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not KvnProfileEntry entry) return;
        ProfileInfoText.Text = entry.Transport == "AWG"
            ? "AmneziaWG · файл .conf будет прочитан при подключении."
            : "CSQTT · данные для подключения сохранены с шифрованием DPAPI.";
    }

    private void ChooseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditProfiles() || ProfilesList.SelectedItem is not KvnProfileEntry chosen) return;
        _settings.KvnSelectedProfileId = chosen.Id;
        try
        {
            SetComboByTag(TransportSelector, chosen.Transport);
            RenderKvnMode();
            if (ActiveProfileSelector.SelectedItem is KvnProfileEntry selected && selected.Id == chosen.Id)
            {
                ApplySelectedProfile(selected);
                SettingsStore.Save(_settings);
            }
            ShowPage("connection");
            HighlightNavigation("connection");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось выбрать профиль: " + ex.Message, "КВН",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditProfiles() || ProfilesList.SelectedItem is not KvnProfileEntry entry) return;
        if (MessageBox.Show(this,
                "Удалить «" + entry.Name + "» из КВН? Исходный файл AWG останется на месте.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            KvnProfileStore.RemoveCsqtt(entry);
            _settings.KvnProfiles.Remove(entry);
            if (_settings.KvnSelectedProfileId == entry.Id) _settings.KvnSelectedProfileId = "";
            if (entry.Transport == "CSQTT" && _settings.KvnSelectedProfileId == "")
            {
                _settings.Password = "";
                _settings.VkHashes = "";
                _settings.Peer = "";
                PeerBox.Clear();
                PasswordBox.Clear();
                VkHashesBox.Clear();
            }
            SettingsStore.Save(_settings);
            RefreshSimpleProfiles();
            ProfileInfoText.Text = "Профиль удалён из списка КВН.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Удаление профиля",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenAdvancedCsqtt_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditProfiles()) return;
        ShowPage("settings");
        HighlightNavigation("profiles");
    }
}
