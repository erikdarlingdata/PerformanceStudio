using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        StatusText.Text = "Connecting...";
        StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE4, 0xE6, 0xEB));
        TestButton.IsEnabled = false;

        try
        {
            var connection = BuildServerConnection();
            var connectionString = BuildConnectionString(connection);

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Fetch databases
            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
            ConnectButton.IsEnabled = true;

            // Default to master if available
            var masterIdx = databases.IndexOf("master");
            if (masterIdx >= 0) DatabaseBox.SelectedIndex = masterIdx;

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
        return new ServerConnection
        {
            Id = serverName,
            ServerName = serverName,
            DisplayName = serverName,
            AuthenticationType = GetSelectedAuthType(),
            TrustServerCertificate = TrustCertBox.IsChecked == true,
            EncryptMode = GetSelectedEncryptMode()
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

    private string BuildConnectionString(ServerConnection connection)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.ServerName,
            InitialCatalog = "master",
            ApplicationName = "PlanViewer",
            ConnectTimeout = 15,
            TrustServerCertificate = connection.TrustServerCertificate,
            Encrypt = connection.EncryptMode switch
            {
                "Optional" => SqlConnectionEncryptOption.Optional,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory
            }
        };

        switch (connection.AuthenticationType)
        {
            case AuthenticationTypes.SqlServer:
                builder.UserID = LoginBox.Text?.Trim() ?? "";
                builder.Password = PasswordBox.Text ?? "";
                break;
            case AuthenticationTypes.EntraMFA:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrEmpty(LoginBox.Text?.Trim()))
                    builder.UserID = LoginBox.Text!.Trim();
                break;
            default:
                builder.IntegratedSecurity = true;
                break;
        }

        return builder.ConnectionString;
    }
}
