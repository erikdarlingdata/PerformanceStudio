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
    private readonly string? _currentDatabase;
    private List<ServerConnection> _savedConnections = new();
    private bool _connecting;
    private bool _closed;

    public ServerConnection? ResultConnection { get; private set; }
    public string? ResultDatabase { get; private set; }

    /// <summary>
    /// The databases the login could see when the winning connection opened, for the caller's
    /// database picker. Handing these over is what lets callers skip re-enumerating through a
    /// second connection to master — a round trip this dialog deliberately does not make, because
    /// some logins (Azure SQL DB, JIT access) can only open the database they named.
    /// </summary>
    public IReadOnlyList<string> ResultDatabases { get; private set; } = Array.Empty<string>();

    /// <param name="currentDatabase">
    /// The database the calling session is already on, when the dialog is opened to reconnect.
    /// It is pre-selected once the database list loads; it never connects on its own.
    /// </param>
    public ConnectionDialog(ICredentialService credentialService, ConnectionStore connectionStore,
        string? currentDatabase = null)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        _currentDatabase = string.IsNullOrWhiteSpace(currentDatabase) ? null : currentDatabase.Trim();
        InitializeComponent();

        AuthTypeBox.SelectedIndex = 0;
        EncryptBox.SelectedIndex = 0;
        PopulateSavedServers();
        UpdateConnectEnabled();
    }

    /// <summary>
    /// Status colours come from the theme so this dialog follows the design tokens; the
    /// fallbacks keep it functional if a resource lookup ever misses.
    /// </summary>
    private Avalonia.Media.IBrush StatusBrush(string key, Avalonia.Media.IBrush fallback) =>
        this.TryFindResource(key, out var value) && value is Avalonia.Media.IBrush brush ? brush : fallback;

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

        UpdateConnectEnabled();
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
        // A XAML-wired selection (EncryptBox already has one) fires while the XAML is still
        // loading, before the named fields exist, so read the sender and check what we touch.
        if (LoginPanel is null || PasswordPanel is null) return;
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item) return;
        var authType = item.Tag?.ToString();

        var showLogin = authType is "SqlServer" or "EntraMFA";
        var showPassword = authType == "SqlServer";

        LoginPanel.IsVisible = showLogin;
        PasswordPanel.IsVisible = showPassword;

        UpdateConnectEnabled();
    }

    private void RequiredField_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateConnectEnabled();
    }

    /// <summary>
    /// Connect is available as soon as the fields a connection actually needs are filled in:
    /// a server name, plus a login and password when SQL Server authentication is selected.
    /// Testing the connection first is optional, so Connect never sits disabled with no reason.
    /// </summary>
    private void UpdateConnectEnabled()
    {
        // Same story: a TextChanged during XAML load can land here before these are assigned.
        if (ConnectButton is null || ServerNameBox is null ||
            LoginBox is null || PasswordBox is null || AuthTypeBox is null)
            return;

        var hasServer = !string.IsNullOrWhiteSpace(ServerNameBox.Text);
        var needsCredentials = GetSelectedAuthType() == AuthenticationTypes.SqlServer;
        var hasCredentials = !needsCredentials ||
            (!string.IsNullOrWhiteSpace(LoginBox.Text) && !string.IsNullOrEmpty(PasswordBox.Text));

        ConnectButton.IsEnabled = !_connecting && hasServer && hasCredentials;
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        // Optional secondary action: validate the settings and fill the Database dropdown so the
        // user can browse databases before connecting.
        await ConnectAndLoadDatabasesAsync();
    }

    /// <summary>
    /// Opens a connection with the current settings and fills the Database dropdown with the
    /// databases the login can see. Shared by Test Connection and Connect so both take the same
    /// path. Reports progress and failures in StatusText; returns the databases when the
    /// connection opened, null when it did not.
    /// </summary>
    private async Task<List<string>?> ConnectAndLoadDatabasesAsync()
    {
        var serverName = ServerNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(serverName))
        {
            StatusText.Text = "Enter a server name";
            StatusText.Foreground = StatusBrush("ErrorBrush", Avalonia.Media.Brushes.OrangeRed);
            return null;
        }

        // For Azure SQL DB / JIT access the login often can't open master, so connect
        // through the database the user named (if any) instead of the hardcoded master.
        var typedDatabase = DatabaseInputBox.Text?.Trim();
        var connectDatabase = string.IsNullOrEmpty(typedDatabase) ? "master" : typedDatabase;

        StatusText.Text = "Connecting...";
        StatusText.Foreground = StatusBrush("ForegroundBrush", Avalonia.Media.Brushes.White);
        _connecting = true;
        TestButton.IsEnabled = false;
        UpdateConnectEnabled();

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
            // database this returns master + that database, which is expected. Read
            // uncommitted so the catalog scan cannot sit blocked behind an in-flight
            // CREATE or RESTORE — carried over from the plan toolbar's enumeration, which
            // this one replaced.
            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            // The named database is reachable (OpenAsync succeeded), so make sure it's
            // selectable even if enumeration didn't surface it (restricted JIT permissions).
            if (!string.IsNullOrEmpty(typedDatabase) &&
                !databases.Contains(typedDatabase, StringComparer.OrdinalIgnoreCase))
                databases.Insert(0, typedDatabase);

            // Re-loading the list drops the selection, so remember what was showing first.
            var previouslySelected = DatabaseBox.SelectedItem?.ToString();

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
            SelectPreferredDatabase(databases, previouslySelected, typedDatabase);

            StatusText.Text = $"Connected ({databases.Count} databases)";
            StatusText.Foreground = StatusBrush("SuccessBrush", Avalonia.Media.Brushes.LimeGreen);
            return databases;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = StatusBrush("ErrorBrush", Avalonia.Media.Brushes.OrangeRed);
            DatabaseBox.IsEnabled = false;
            return null;
        }
        finally
        {
            _connecting = false;
            TestButton.IsEnabled = true;
            UpdateConnectEnabled();
        }
    }

    /// <summary>
    /// Picks the database to show in the dropdown, in order: whatever was already selected (a
    /// pick the user made before re-testing), the database a reconnecting session is already on,
    /// the named initial database, then master. The initial database box is a reachability hint
    /// for logins that can't open master, so it does not outrank the session's own database.
    /// </summary>
    private void SelectPreferredDatabase(List<string> databases, string? previouslySelected, string? typedDatabase)
    {
        foreach (var candidate in new[] { previouslySelected, _currentDatabase, typedDatabase, "master" })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var idx = databases.FindIndex(d => d.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            DatabaseBox.SelectedIndex = idx;
            return;
        }

        DatabaseBox.SelectedIndex = databases.Count > 0 ? 0 : -1;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_connecting) return;

        /* Capture what is about to be validated. The fields stay editable while the connection
           opens, and ConnectAndLoadDatabasesAsync reads them in this same synchronous block, so
           reading them again after the await could save a server that was never tested. */
        var connection = BuildServerConnection();
        var login = LoginBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        var typedDatabase = DatabaseInputBox.Text?.Trim();

        // Single step: connect and enumerate databases here, so Test Connection is never a
        // prerequisite. On failure the message stays in StatusText and the dialog stays open.
        var databases = await ConnectAndLoadDatabasesAsync();
        if (databases == null)
            return;

        // Cancel stays live while the connection opens, so the dialog may already be gone.
        if (_closed)
            return;

        // Save credentials
        if (connection.AuthenticationType == AuthenticationTypes.SqlServer)
        {
            _credentialService.SaveCredential(connection.Id, login, password);
        }
        else if (connection.AuthenticationType == AuthenticationTypes.EntraMFA)
        {
            if (!string.IsNullOrEmpty(login))
                _credentialService.SaveCredential(connection.Id, login, "");
        }

        // Save connection to store
        _connectionStore.AddOrUpdate(connection);

        ResultConnection = connection;
        ResultDatabase = ResolveResultDatabase(typedDatabase);
        ResultDatabases = databases;
        Close(true);
    }

    /// <summary>
    /// The database the session should open: the Database dropdown when it has a selection,
    /// otherwise the named initial database that was validated, otherwise master.
    /// </summary>
    private string ResolveResultDatabase(string? typedDatabase)
    {
        var selected = DatabaseBox.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(selected))
            return selected;

        return string.IsNullOrEmpty(typedDatabase) ? "master" : typedDatabase;
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

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
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
