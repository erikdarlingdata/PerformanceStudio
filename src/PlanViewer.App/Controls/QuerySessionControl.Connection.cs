using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        await ShowConnectionDialogAsync();
    }

    private async Task ShowConnectionDialogAsync()
    {
        var dialog = new ConnectionDialog(_credentialService, _connectionStore);
        var result = await dialog.ShowDialog<bool?>(GetParentWindow());

        if (result == true && dialog.ResultConnection != null)
        {
            _serverConnection = dialog.ResultConnection;
            _selectedDatabase = dialog.ResultDatabase;
            _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

            ServerLabel.Text = _serverConnection.ApplicationIntentReadOnly
                ? $"{_serverConnection.ServerName} (Read-only)"
                : _serverConnection.ServerName;
            ServerLabel.Foreground = Brushes.LimeGreen;
            ConnectButton.Content = "Reconnect";

            await PopulateDatabases();
            await FetchServerMetadataAsync();
            await FetchServerUtcOffset();

            if (_selectedDatabase != null)
            {
                for (int i = 0; i < DatabaseBox.Items.Count; i++)
                {
                    if (DatabaseBox.Items[i]?.ToString() == _selectedDatabase)
                    {
                        DatabaseBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            await FetchDatabaseMetadataAsync();

            ExecuteButton.IsEnabled = true;
            ExecuteEstButton.IsEnabled = true;
        }
    }

    private async Task PopulateDatabases()
    {
        if (_serverConnection == null) return;

        try
        {
            var connStr = _serverConnection.GetConnectionString(_credentialService, "master");
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
        }
        catch
        {
            DatabaseBox.IsEnabled = false;
        }
    }

    private async void Database_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverConnection == null || DatabaseBox.SelectedItem == null) return;

        _selectedDatabase = DatabaseBox.SelectedItem.ToString();
        _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

        // Refresh database metadata for the new context
        await FetchDatabaseMetadataAsync();
    }

    private async Task FetchServerMetadataAsync()
    {
        if (_connectionString == null) return;
        try
        {
            _serverMetadata = await ServerMetadataService.FetchServerMetadataAsync(
                _connectionString, IsAzureConnection);
        }
        catch
        {
            // Non-fatal — advice will just lack server context
            _serverMetadata = null;
        }
    }

    private async Task FetchServerUtcOffset()
    {
        if (_connectionString == null) return;
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT DATEDIFF(MINUTE, GETUTCDATE(), GETDATE())", conn);
            var offset = await cmd.ExecuteScalarAsync();
            if (offset is int mins)
                PlanViewer.Core.Services.TimeDisplayHelper.ServerUtcOffsetMinutes = mins;
        }
        catch { }
    }

    private async Task FetchDatabaseMetadataAsync()
    {
        if (_connectionString == null || _serverMetadata == null) return;
        try
        {
            _serverMetadata.Database = await ServerMetadataService.FetchDatabaseMetadataAsync(
                _connectionString, _serverMetadata.SupportsScopedConfigs);
        }
        catch
        {
            // Non-fatal — advice will just lack database context
        }
    }
}
