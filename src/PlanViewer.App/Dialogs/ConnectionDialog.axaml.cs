using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Dialogs;

public partial class ConnectionDialog : Window
{
    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;
    private List<ServerConnection> _savedConnections = new();

    public ServerConnection? ResultConnection { get; private set; }
    public string? ResultDatabase { get; private set; }

    public ConnectionDialog(ICredentialService credentialService, ConnectionStore connectionStore)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        InitializeComponent();

        AuthTypeBox.SelectedIndex = 0;
        EncryptBox.SelectedIndex = 0;
        PopulateSavedServers();
    }

    private void PopulateSavedServers()
    {
        _savedConnections = _connectionStore.Load();
        var serverNames = _savedConnections
            .OrderByDescending(s => s.LastConnected)
            .Select(s => s.ServerName)
            .Distinct()
            .ToList();
        ServerList.ItemsSource = serverNames;

        // Pre-fill the most recently used connection
        var mostRecent = _savedConnections
            .OrderByDescending(s => s.LastConnected)
            .FirstOrDefault();

        if (mostRecent != null)
        {
            ServerNameBox.Text = mostRecent.ServerName;
            ApplySavedConnection(mostRecent);
        }
    }

    private void ApplySavedConnection(ServerConnection saved)
    {
        // Auth type
        for (int i = 0; i < AuthTypeBox.Items.Count; i++)
        {
            if (AuthTypeBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == saved.AuthenticationType)
            {
                AuthTypeBox.SelectedIndex = i;
                break;
            }
        }

        // Encrypt mode
        for (int i = 0; i < EncryptBox.Items.Count; i++)
        {
            if (EncryptBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == saved.EncryptMode)
            {
                EncryptBox.SelectedIndex = i;
                break;
            }
        }

        TrustCertBox.IsChecked = saved.TrustServerCertificate;
        ReadOnlyIntentCheckBox.IsChecked = saved.ApplicationIntentReadOnly;
        DatabaseInputBox.Text = saved.DatabaseName ?? "";

        // Load stored credentials
        var cred = _credentialService.GetCredential(saved.Id);
        if (cred != null)
        {
            LoginBox.Text = cred.Value.Username;
            PasswordBox.Text = cred.Value.Password;
        }
    }

    private void ServerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var serverName = ServerList.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(serverName)) return;

        ServerNameBox.Text = serverName;
        ServerDropdown.IsOpen = false;

        var saved = _savedConnections.FirstOrDefault(s =>
            s.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase));

        if (saved != null)
            ApplySavedConnection(saved);
    }

    private void AuthType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AuthTypeBox.SelectedItem is not ComboBoxItem item) return;
        var authType = item.Tag?.ToString();

        var showLogin = authType is "SqlServer" or "EntraMFA";
        var showPassword = authType == "SqlServer";

        LoginPanel.IsVisible = showLogin;
        PasswordPanel.IsVisible = showPassword;
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        var serverName = ServerNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(serverName))
        {
            StatusText.Text = "Enter a server name";
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            return;
        }

        // For Azure SQL DB / JIT access the login often can't open master, so connect
        // through the database the user named (if any) instead of the hardcoded master.
        var typedDatabase = DatabaseInputBox.Text?.Trim();
        var connectDatabase = string.IsNullOrEmpty(typedDatabase) ? "master" : typedDatabase;

        StatusText.Text = "Connecting...";
        StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE4, 0xE6, 0xEB));
        TestButton.IsEnabled = false;

        try
        {
            var connection = BuildServerConnection();
            var connectionString = connection.GetConnectionString(
                LoginBox.Text?.Trim(),
                PasswordBox.Text,
                connectDatabase);

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Fetch databases the login can see. On Azure SQL DB connected to a single user
            // database this returns master + that database, which is expected.
            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            // The named database is reachable (OpenAsync succeeded), so make sure it's
            // selectable even if enumeration didn't surface it (restricted JIT permissions).
            if (!string.IsNullOrEmpty(typedDatabase) &&
                !databases.Contains(typedDatabase, StringComparer.OrdinalIgnoreCase))
                databases.Insert(0, typedDatabase);

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
            ConnectButton.IsEnabled = true;

            // Pre-select the named database when given, otherwise default to master.
            var preferred = string.IsNullOrEmpty(typedDatabase) ? "master" : typedDatabase;
            var preferredIdx = databases.FindIndex(d => d.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (preferredIdx >= 0) DatabaseBox.SelectedIndex = preferredIdx;
            else if (databases.Count > 0) DatabaseBox.SelectedIndex = 0;

            StatusText.Text = $"Connected ({databases.Count} databases)";
            StatusText.Foreground = Avalonia.Media.Brushes.LimeGreen;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            DatabaseBox.IsEnabled = false;
            ConnectButton.IsEnabled = false;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var connection = BuildServerConnection();

        // Save credentials
        var authType = GetSelectedAuthType();
        if (authType == AuthenticationTypes.SqlServer)
        {
            var login = LoginBox.Text?.Trim() ?? "";
            var password = PasswordBox.Text ?? "";
            _credentialService.SaveCredential(connection.Id, login, password);
        }
        else if (authType == AuthenticationTypes.EntraMFA)
        {
            var login = LoginBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(login))
                _credentialService.SaveCredential(connection.Id, login, "");
        }

        // Save connection to store
        _connectionStore.AddOrUpdate(connection);

        ResultConnection = connection;
        ResultDatabase = DatabaseBox.SelectedItem?.ToString();
        Close(true);
    }

    private void DropdownButton_Click(object? sender, RoutedEventArgs e)
    {
        ServerDropdown.MinWidth = ServerNameGrid.Bounds.Width;
        ServerDropdown.IsOpen = !ServerDropdown.IsOpen;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private ServerConnection BuildServerConnection()
    {
        var serverName = ServerNameBox.Text?.Trim() ?? "";
        var databaseName = DatabaseInputBox.Text?.Trim();
        return new ServerConnection
        {
            Id = serverName,
            ServerName = serverName,
            DisplayName = serverName,
            DatabaseName = string.IsNullOrEmpty(databaseName) ? null : databaseName,
            AuthenticationType = GetSelectedAuthType(),
            TrustServerCertificate = TrustCertBox.IsChecked == true,
            EncryptMode = GetSelectedEncryptMode(),
            ApplicationIntentReadOnly = ReadOnlyIntentCheckBox.IsChecked == true
        };
    }

    private string GetSelectedAuthType()
    {
        if (AuthTypeBox.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? AuthenticationTypes.Windows;
        return AuthenticationTypes.Windows;
    }

    private string GetSelectedEncryptMode()
    {
        if (EncryptBox.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "Mandatory";
        return "Mandatory";
    }

}
