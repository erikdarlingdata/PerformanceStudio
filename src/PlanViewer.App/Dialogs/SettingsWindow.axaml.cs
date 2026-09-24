using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Dialogs;

internal partial class SettingsWindow : Window
{
	private AppSettings _settings;
	private bool _isDirty;

	/// <summary>
	/// Set while a section is being built and initialized. Giving a control its starting value
	/// raises ValueChanged/SelectionChanged/TextChanged/PropertyChanged - some synchronously,
	/// some later when the control is templated and its bindings run on the layout pass - so the
	/// dirty handlers no-op while this is set. Without it, merely opening Settings looks like an
	/// edit and Cancel always asks to discard changes.
	/// </summary>
	private bool _building;

	/// <summary>Identifies the most recent build so an older one cannot release the guard early.</summary>
	private int _buildToken;

	/// <summary>Set while the discard prompt is up, so a second close cannot stack another.</summary>
	private bool _closeWalkInProgress;

	/* Only the controls something still consults are kept. Every section used to cache each of its
	   controls in a field so Save could read the values back out at the end; now that edits are
	   written as they happen, those fields held nothing anyone asked for. What remains is the
	   colour list, which is rebuilt in place and validated on save, and the row count that decides
	   how long it is. These two hold the section that is on screen, or the last one that was —
	   nothing depends on which, but do not read them as a picture of the current section. */

	// Multi QS Overview controls
	private NumericUpDown? _topDbCountBox;
	private readonly List<TextBox> _colorTextBoxes = new();
	private readonly List<Rectangle> _colorPreviews = new();
	private StackPanel? _colorListPanel;

	// Script Options (Format) rows. The grid itself is a local in its builder — nothing outside
	// needs it, and only the rows carry state worth keeping.
	private readonly ObservableCollection<FormatOptionRow> _formatRows = new();

	/* Integrations (MCP server + proxy). These do not live in AppSettings: MCP is two raw keys in
	   settings.json and the proxy password is in the OS credential store, so this section keeps its
	   own pending state, which Save_Click writes out separately. Edits land in these fields as they
	   happen, the same way every other section now writes straight into _settings. */
	private TextBlock? _mcpCopyStatus;
	private Grid? _proxyManualPanel;

	private bool _integrationsLoaded;
	private bool _mcpEnabled;
	private int _mcpPort = 5152;
	private ProxyMode _proxyMode = ProxyMode.System;
	private string _proxyAddress = "";
	private string _proxyUsername = "";
	private string _proxyTypedPassword = "";
	private bool _hasStoredProxyPassword;

	/// <summary>Set by Reset, the one gesture that means "remove the saved proxy password".</summary>
	private bool _clearStoredProxyPassword;

	/// <summary>The loaded values, so Save can skip writing a section the user never touched.</summary>
	private (bool Enabled, int Port, ProxyMode Mode, string Address, string Username) _integrationsOriginal;

	internal event Action<AppSettings>? SettingsSaved;

	/* Both constructors take a copy, and the copy is what makes Cancel mean anything.

	   Every section writes its edits straight into _settings as they happen, so _settings is the
	   draft, not the live configuration — discarding it is how Cancel discards. AppSettingsService
	   .Load() hands back a cached instance shared with the rest of the app, so using it directly
	   would mean editing a text box had already changed the app's settings in memory and Cancel
	   had nothing left to undo. Clone() round-trips through JSON, so the collections come apart
	   too, which matters because the colour list is edited in place. */
	public SettingsWindow()
	{
		_settings = AppSettingsService.Load().Clone();
		InitializeComponent();
		ShowSection(0);
	}

	internal SettingsWindow(AppSettings settings)
	{
		_settings = settings.Clone();
		InitializeComponent();
		ShowSection(0);
	}

	private void SectionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (SectionList.SelectedIndex >= 0)
			ShowSection(SectionList.SelectedIndex);
	}

	private void ShowSection(int index)
	{
		var token = ++_buildToken;
		_building = true;

		DetailPanel.Content = index switch
		{
			0 => BuildQueryStoreSection(),
			1 => BuildQueryHistorySection(),
			2 => BuildScriptOptionsSection(),
			3 => BuildIntegrationsSection(),
			_ => null
		};

		// Controls keep initializing after this returns: templates are applied and cell bindings
		// (the format DataGrid writes back through them) run on the layout pass. Release the guard
		// once the UI has settled, so only real user edits mark the dialog dirty.
		Dispatcher.UIThread.Post(() =>
		{
			if (_buildToken == token)
				_building = false;
		}, DispatcherPriority.Background);
	}

	/// <summary>
	/// Applies an edit from a control and marks the dialog dirty. Every section's change handlers
	/// go through here.
	///
	/// <para>Two events are dropped rather than applied. One is an edit raised while the section
	/// is still being built, which came from a control taking its initial value rather than from
	/// the user — that is what <c>_building</c> has always been for. The other is an edit from a
	/// control whose section has since been rebuilt: <paramref name="buildToken"/> is the token
	/// its build was given, so a handler that outlived its controls recognises itself and stays
	/// out of the way. Nothing reads the controls back any more, so a stale handler writing into
	/// the settings is the only way an orphan could still do damage.</para>
	/// </summary>
	private void Commit(int buildToken, Action edit)
	{
		if (_building || buildToken != _buildToken) return;
		edit();
		_isDirty = true;
	}

	// ── Query Store Section ──────────────────────────────────────────

	private static readonly (string Content, string Tag)[] MetricOptions =
	{
		("Total CPU", "cpu"), ("Avg CPU", "avg-cpu"),
		("Total Duration", "duration"), ("Avg Duration", "avg-duration"),
		("Total Reads", "reads"), ("Avg Reads", "avg-reads"),
		("Total Writes", "writes"), ("Avg Writes", "avg-writes"),
		("Total Physical Reads", "physical-reads"), ("Avg Physical Reads", "avg-physical-reads"),
		("Total Memory", "memory"), ("Avg Memory", "avg-memory"),
		("Executions", "executions"),
	};

	private static readonly (string Content, string Tag)[] TimeRangeOptions =
	{
		("3 hours", "3"), ("24 hours", "24"), ("48 hours", "48"),
		("7 days", "168"), ("30 days", "720"),
	};

	private static readonly (string Content, string Tag)[] TimeDisplayOptions =
	{
		("Local", "Local"), ("UTC", "Utc"), ("Server", "Server"),
	};

	private static readonly (string Content, string Tag)[] GroupByOptions =
	{
		("None", "None"), ("Query Hash", "QueryHash"), ("Module", "Module"),
	};

	private Control BuildQueryStoreSection()
	{
		var token = _buildToken;
		var panel = new StackPanel { Spacing = 16 };

		// Chapter 1: Query Store
		panel.Children.Add(CreateChapterHeader("Query Store"));

		var slicerDays = CreateNumericUpDown(_settings.QueryStoreSlicerDays, 1, 365);
		slicerDays.ValueChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreSlicerDays = (int)(slicerDays.Value ?? 30));
		panel.Children.Add(CreateRow("Default history length (days)", slicerDays));

		var defaultMetric = CreateTagComboBox(MetricOptions, _settings.QueryStoreDefaultMetric);
		defaultMetric.SelectionChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreDefaultMetric = GetComboTag(defaultMetric));
		panel.Children.Add(CreateRow("Default metric for top", defaultMetric));

		var topLimit = CreateNumericUpDown(_settings.QueryStoreTopLimit, 1, 200);
		topLimit.ValueChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreTopLimit = (int)(topLimit.Value ?? 25));
		panel.Children.Add(CreateRow("Top elements limit", topLimit));

		var timeRange = CreateTagComboBox(TimeRangeOptions, _settings.QueryStoreDefaultTimeRange);
		timeRange.SelectionChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreDefaultTimeRange = GetComboTag(timeRange));
		panel.Children.Add(CreateRow("Default time range", timeRange));

		var timeDisplay = CreateTagComboBox(TimeDisplayOptions, _settings.QueryStoreDefaultTimeDisplay);
		timeDisplay.SelectionChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreDefaultTimeDisplay = GetComboTag(timeDisplay));
		panel.Children.Add(CreateRow("Default time display", timeDisplay));

		var groupBy = CreateTagComboBox(GroupByOptions, _settings.QueryStoreDefaultGroupBy);
		groupBy.SelectionChanged += (_, _) =>
			Commit(token, () => _settings.QueryStoreDefaultGroupBy = GetComboTag(groupBy));
		panel.Children.Add(CreateRow("Default group by", groupBy));

		// Chapter 2: Multi QS Overview
		panel.Children.Add(CreateChapterHeader("Multi QS Overview"));

		var topDbCount = CreateNumericUpDown(_settings.MultiQsTopDbCount, 2, 20);
		_topDbCountBox = topDbCount;
		topDbCount.ValueChanged += (_, _) => Commit(token, () =>
		{
			_settings.MultiQsTopDbCount = (int)(topDbCount.Value ?? 5);
			RebuildColorList(token);
		});
		panel.Children.Add(CreateRow("Number of top databases", topDbCount));

		_colorListPanel = new StackPanel { Spacing = 4 };
		RebuildColorList(token);
		panel.Children.Add(CreateRow("Top database colors", _colorListPanel));

		return panel;
	}

	/// <summary>
	/// Rebuilds the colour rows for the current database count, padding the stored list if the
	/// count has outgrown it.
	///
	/// <para><b>It only ever grows.</b> Trimming the tail to match the count looks like tidying up
	/// and is data loss: the list is the sole record of the palette now, so dropping entry 8
	/// because the count is 5 throws away a colour the user chose, and raising the count back
	/// hands them a stock one instead. Entries past the count cost nothing — the row loop below is
	/// bounded by the count, and so is everything that reads the palette — and keeping them is what
	/// lets the count go down and up again without the trip costing anything.</para>
	/// </summary>
	private void RebuildColorList(int buildToken)
	{
		if (_colorListPanel == null) return;
		_colorListPanel.Children.Clear();
		_colorTextBoxes.Clear();
		_colorPreviews.Clear();

		var count = (int)(_topDbCountBox?.Value ?? _settings.MultiQsTopDbCount);
		var colors = _settings.MultiQsTopDbColors;

		while (colors.Count < count)
			colors.Add(AppSettingsService.DefaultTopDbColors[colors.Count % AppSettingsService.DefaultTopDbColors.Count]);

		for (int i = 0; i < count; i++)
		{
			var hex = colors[i];
			var preview = new Rectangle
			{
				Width = 24, Height = 24,
				Fill = TryParseBrush(hex),
				RadiusX = 3, RadiusY = 3,
				Margin = new Thickness(0, 0, 6, 0)
			};
			var textBox = new TextBox
			{
				Text = hex, Width = 100, Height = 28, FontSize = 12,
				Foreground = (IBrush?)this.FindResource("ForegroundBrush") ?? Brushes.White
			};
			var index = i;
			textBox.TextChanged += (_, _) => Commit(buildToken, () =>
			{
				var text = textBox.Text ?? "";
				// Stored as typed, valid or not — Save is where a bad colour is refused, and
				// keeping the raw text is what lets the user see it again to correct it.
				if (index < _settings.MultiQsTopDbColors.Count)
					_settings.MultiQsTopDbColors[index] = text;
				if (index < _colorPreviews.Count)
					_colorPreviews[index].Fill = TryParseBrush(text);
			});

			_colorTextBoxes.Add(textBox);
			_colorPreviews.Add(preview);

			var row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 4,
				Children =
				{
					new TextBlock
					{
						Text = $"#{i + 1}",
						Width = 28,
						VerticalAlignment = VerticalAlignment.Center,
						Foreground = (IBrush?)this.FindResource("ForegroundBrush") ?? Brushes.White,
						FontSize = 12
					},
					preview,
					textBox
				}
			};
			_colorListPanel.Children.Add(row);
		}
	}

	private static SolidColorBrush TryParseBrush(string hex)
	{
		try
		{
			return new SolidColorBrush(Color.Parse(hex));
		}
		catch
		{
			return new SolidColorBrush(Colors.Gray);
		}
	}

	// ── Query History Section ────────────────────────────────────────

	private static readonly (string Content, string Tag)[] HistoryMetricOptions =
	{
		("Avg CPU (ms)", "AvgCpuMs"), ("Avg Duration (ms)", "AvgDurationMs"),
		("Avg Logical Reads", "AvgLogicalReads"), ("Avg Logical Writes", "AvgLogicalWrites"),
		("Avg Physical Reads", "AvgPhysicalReads"), ("Avg Memory (MB)", "AvgMemoryMb"),
		("Avg Rows", "AvgRowcount"),
		("Total CPU (ms)", "TotalCpuMs"), ("Total Duration (ms)", "TotalDurationMs"),
		("Total Reads", "TotalLogicalReads"), ("Total Writes", "TotalLogicalWrites"),
		("Total Physical Reads", "TotalPhysicalReads"), ("Total Memory (MB)", "TotalMemoryMb"),
		("Executions", "CountExecutions"),
	};

	private Control BuildQueryHistorySection()
	{
		var token = _buildToken;
		var panel = new StackPanel { Spacing = 16 };
		panel.Children.Add(CreateChapterHeader("Query History"));

		var metric = CreateTagComboBox(HistoryMetricOptions, _settings.QueryHistoryDefaultMetric);
		metric.SelectionChanged += (_, _) =>
			Commit(token, () => _settings.QueryHistoryDefaultMetric = GetComboTag(metric));
		panel.Children.Add(CreateRow("Default chart metric", metric));

		var maxPlans = CreateNumericUpDown(_settings.QueryHistoryMaxPlans, 1, 100);
		maxPlans.ValueChanged += (_, _) =>
			Commit(token, () => _settings.QueryHistoryMaxPlans = (int)(maxPlans.Value ?? 10));
		panel.Children.Add(CreateRow("Max plans fetched per query", maxPlans));

		return panel;
	}

	// ── Integrations Section ─────────────────────────────────────────

	/// <summary>
	/// Reads the MCP and proxy settings once per dialog. Deferred until the section is first
	/// shown so merely opening Settings does not hit the credential store.
	/// </summary>
	private void EnsureIntegrationsLoaded()
	{
		if (_integrationsLoaded) return;

		var mcp = Mcp.McpSettings.Load();
		_mcpEnabled = mcp.Enabled;
		_mcpPort = mcp.Port;

		/* The stored proxy password is deliberately not put into the TextBox. PasswordChar only
		   masks the glyph — the cleartext still sits in the visual and accessibility trees. The
		   placeholder says it is saved, and an empty box at save time means "keep what is there". */
		var proxy = ProxySettings.Load();
		_hasStoredProxyPassword = !string.IsNullOrEmpty(proxy.Password);
		_proxyMode = proxy.Mode;
		_proxyAddress = proxy.Address;
		_proxyUsername = proxy.Username;
		_proxyTypedPassword = "";

		_integrationsOriginal = (_mcpEnabled, _mcpPort, _proxyMode, _proxyAddress, _proxyUsername);
		_integrationsLoaded = true;
	}

	private Control BuildIntegrationsSection()
	{
		EnsureIntegrationsLoaded();

		var token = _buildToken;
		var panel = new StackPanel { Spacing = 16 };

		panel.Children.Add(CreateChapterHeader("MCP Server"));
		panel.Children.Add(new TextBlock
		{
			Text = "Lets an AI assistant read plans you have open in Performance Studio. "
				 + "The server listens on this machine only.",
			FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 520
		});

		var enabledBox = new CheckBox
		{
			Content = "Enable MCP server",
			IsChecked = _mcpEnabled,
			FontSize = 13
		};
		enabledBox.IsCheckedChanged += (_, _) => Commit(token, () => _mcpEnabled = enabledBox.IsChecked == true);
		panel.Children.Add(enabledBox);

		var portBox = CreateNumericUpDown(_mcpPort, 1024, 65535);
		portBox.ValueChanged += (_, _) => Commit(token, () => _mcpPort = (int)(portBox.Value ?? 5152));
		panel.Children.Add(CreateRow("Port", portBox));

		panel.Children.Add(new TextBlock
		{
			Text = "Restart Performance Studio for MCP changes to take effect.",
			FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap
		});

		var copyButton = new Button
		{
			Content = "Copy MCP command",
			Height = 32,
			Padding = new Thickness(16, 0),
			FontSize = 12,
			Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
		};
		copyButton.Click += CopyMcpCommand_Click;
		_mcpCopyStatus = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
		panel.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Children = { copyButton, _mcpCopyStatus }
		});

		panel.Children.Add(CreateChapterHeader("Proxy"));

		var systemRadio = new RadioButton
		{
			GroupName = "ProxyMode",
			Content = "Use system proxy (Windows credentials)",
			FontSize = 13,
			IsChecked = _proxyMode == ProxyMode.System
		};
		var manualRadio = new RadioButton
		{
			GroupName = "ProxyMode",
			Content = "Manual",
			FontSize = 13,
			IsChecked = _proxyMode == ProxyMode.Manual
		};

		/* Both radios raise IsCheckedChanged on every selection — one going false, one going
		   true. Only the now-checked one drives the update, or the two events fight. */
		void OnProxyModeChanged(object? sender, RoutedEventArgs _)
		{
			if (sender is not RadioButton { IsChecked: true }) return;
			Commit(token, () =>
			{
				_proxyMode = manualRadio.IsChecked == true ? ProxyMode.Manual : ProxyMode.System;
				if (_proxyManualPanel != null)
					_proxyManualPanel.IsVisible = _proxyMode == ProxyMode.Manual;
			});
		}
		systemRadio.IsCheckedChanged += OnProxyModeChanged;
		manualRadio.IsCheckedChanged += OnProxyModeChanged;

		panel.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16,
			Children = { systemRadio, manualRadio }
		});

		var addressBox = CreateProxyInput(_proxyAddress, "http://proxy.example.com:8080");
		addressBox.TextChanged += (_, _) => Commit(token, () => _proxyAddress = addressBox.Text ?? "");

		var usernameBox = CreateProxyInput(_proxyUsername, "DOMAIN\\user");
		usernameBox.TextChanged += (_, _) => Commit(token, () => _proxyUsername = usernameBox.Text ?? "");

		var passwordBox = CreateProxyInput(_proxyTypedPassword,
			_hasStoredProxyPassword ? "(saved — leave blank to keep)" : "");
		passwordBox.PasswordChar = '•';
		passwordBox.TextChanged += (_, _) => Commit(token, () => _proxyTypedPassword = passwordBox.Text ?? "");

		_proxyManualPanel = new Grid
		{
			IsVisible = _proxyMode == ProxyMode.Manual,
			ColumnDefinitions = new ColumnDefinitions("90,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
			RowSpacing = 6,
			ColumnSpacing = 8,
			MaxWidth = 460,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		AddProxyRow(_proxyManualPanel, 0, "Address", addressBox);
		AddProxyRow(_proxyManualPanel, 1, "Username", usernameBox);
		AddProxyRow(_proxyManualPanel, 2, "Password", passwordBox);
		panel.Children.Add(_proxyManualPanel);

		return panel;
	}

	private static TextBox CreateProxyInput(string text, string placeholder) => new()
	{
		Text = text,
		PlaceholderText = placeholder,
		FontSize = 13,
		Height = 32,
		Padding = new Thickness(6, 2)
	};

	private static void AddProxyRow(Grid grid, int row, string label, Control input)
	{
		var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
		Grid.SetRow(text, row);
		Grid.SetColumn(text, 0);
		grid.Children.Add(text);

		Grid.SetRow(input, row);
		Grid.SetColumn(input, 1);
		grid.Children.Add(input);
	}

	private async void CopyMcpCommand_Click(object? sender, RoutedEventArgs e)
	{
		var command = $"claude mcp add --transport streamable-http --scope user performance-studio http://localhost:{_mcpPort}/";
		var copied = await ClipboardHelper.TrySetTextAsync(this, command);
		if (_mcpCopyStatus == null)
			return;

		if (!copied)
		{
			_mcpCopyStatus.Text = "Clipboard busy — try again.";
			return;
		}

		/* The command carries the port shown above, which is the one the user means. It is not
		   yet the one the app answers on: in About this button sat beside a value that committed
		   on every keystroke, but here nothing is written until Save. Copying silently would hand
		   someone a command for a port this app will never listen on. */
		var portIsUnsaved = _mcpPort != _integrationsOriginal.Port;
		_mcpCopyStatus.Text = portIsUnsaved
			? $"Copied — save and restart for port {_mcpPort} to answer."
			: "Copied to clipboard.";
	}

	/// <summary>
	/// Returns the section to a stock MCP server and a system proxy. Only pending state changes
	/// here — nothing is written until Save, so Cancel still walks it all back.
	/// </summary>
	/// <param name="clearStoredPassword">
	/// Whether to also stage removal of the saved proxy password from the OS credential store.
	///
	/// <para>True only for Reset Section, pressed while Integrations is on screen. Reset All
	/// passes false, because deleting a credential is the one thing in this dialog that Cancel
	/// cannot really undo — the password is gone and the user has to find it again — and Reset
	/// All is a single unconfirmed button reachable from any section. Someone restoring the
	/// Query Store defaults has not asked to lose their proxy password, and nothing on screen
	/// would warn them. Resetting the proxy <i>configuration</i> from anywhere is fine; only the
	/// credential needs the deliberate, targeted gesture.</para>
	/// </param>
	private void ResetIntegrations(bool clearStoredPassword)
	{
		EnsureIntegrationsLoaded();

		var fresh = new Mcp.McpSettings();
		_mcpEnabled = fresh.Enabled;
		_mcpPort = fresh.Port;
		_proxyMode = ProxyMode.System;
		_proxyAddress = "";
		_proxyUsername = "";
		_proxyTypedPassword = "";

		if (!clearStoredPassword)
			return;

		/* This is the only way to delete a stored proxy password, because an empty password box
		   always means "keep what is there" — otherwise anyone who edited the proxy address would
		   have to retype the password. Clearing the flag too stops the placeholder claiming a
		   password is saved after the reset has taken it away. */
		_clearStoredProxyPassword = _hasStoredProxyPassword;
		_hasStoredProxyPassword = false;
	}

	/// <summary>Writes MCP and proxy settings, and only the ones that actually changed.</summary>
	private void SaveIntegrations()
	{
		if (!_integrationsLoaded) return;

		var mcpChanged = _mcpEnabled != _integrationsOriginal.Enabled
					  || _mcpPort != _integrationsOriginal.Port;
		var proxyChanged = _proxyMode != _integrationsOriginal.Mode
						|| _proxyAddress != _integrationsOriginal.Address
						|| _proxyUsername != _integrationsOriginal.Username;
		var passwordChanged = _proxyTypedPassword.Length > 0 || _clearStoredProxyPassword;

		if (mcpChanged)
		{
			SettingsFile.Update(o =>
			{
				o["mcp_enabled"] = _mcpEnabled;
				o["mcp_port"] = _mcpPort;
			});
		}

		/* Split from the MCP write on purpose. Saving the proxy reaches the OS credential store,
		   and this used to run whenever anything in the section changed — so ticking the MCP
		   checkbox was enough to touch a credential the user never opened. */
		if (proxyChanged || passwordChanged)
		{
			var proxy = new ProxySettings
			{
				Mode = _proxyMode,
				Address = _proxyAddress,
				Username = _proxyUsername,
				Password = _proxyTypedPassword,

				/* Touch the credential store only on a positive instruction: a password was
				   typed, or Reset asked for the stored one to go. Never on an empty box.

				   The old rule ("touch unless the box is empty AND we believe one is stored")
				   deleted the credential whenever we believed wrongly — and ProxySettings.Load
				   swallows a credential-store failure and reports no password, so an unreadable
				   store looked exactly like an empty one. An unreadable store is unknown, not
				   empty, and the difference was a password nobody asked to delete. */
				TouchCredential = passwordChanged
			};
			proxy.Save();

			if (_proxyTypedPassword.Length > 0)
				_hasStoredProxyPassword = true;
		}

		_integrationsOriginal = (_mcpEnabled, _mcpPort, _proxyMode, _proxyAddress, _proxyUsername);
		_proxyTypedPassword = "";
		_clearStoredProxyPassword = false;
	}

	// ── Script Options Section ───────────────────────────────────────

	private static readonly string[] FormatPropertyOrder =
	[
		"KeywordCasing", "SqlVersion", "IndentationSize",
		"AlignClauseBodies", "AlignColumnDefinitionFields", "AlignSetClauseItem",
		"AsKeywordOnOwnLine", "IncludeSemicolons",
		"IndentSetClause", "IndentViewBody",
		"MultilineInsertSourcesList", "MultilineInsertTargetsList",
		"MultilineSelectElementsList", "MultilineSetClauseItems",
		"MultilineViewColumnsList", "MultilineWherePredicatesList",
		"NewLineBeforeCloseParenthesisInMultilineList",
		"NewLineBeforeFromClause", "NewLineBeforeGroupByClause",
		"NewLineBeforeHavingClause", "NewLineBeforeJoinClause",
		"NewLineBeforeOffsetClause", "NewLineBeforeOpenParenthesisInMultilineList",
		"NewLineBeforeOrderByClause", "NewLineBeforeOutputClause",
		"NewLineBeforeWhereClause", "NewLineBeforeWindowClause",
	];

	private static readonly Dictionary<string, string[]> FormatChoiceOptionsMap = new()
	{
		["KeywordCasing"] = ["Uppercase", "Lowercase", "PascalCase"],
		["SqlVersion"] = ["80", "90", "100", "110", "120", "130", "140", "150", "160", "170"],
	};

	private Control BuildScriptOptionsSection()
	{
		var token = _buildToken;
		var panel = new StackPanel { Spacing = 16 };
		panel.Children.Add(CreateChapterHeader("Format Options"));

		var current = _settings.FormatOptions ?? new SqlFormatSettings();
		var defaults = new SqlFormatSettings();
		_formatRows.Clear();

		var props = typeof(SqlFormatSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.ToDictionary(p => p.Name);

		foreach (var name in FormatPropertyOrder)
		{
			if (!props.TryGetValue(name, out var prop)) continue;

			var currentVal = prop.GetValue(current);
			var defaultVal = prop.GetValue(defaults);
			var isBool = prop.PropertyType == typeof(bool);
			FormatChoiceOptionsMap.TryGetValue(prop.Name, out var choiceOptions);

			var row = new FormatOptionRow
			{
				Name = SplitPascalCase(prop.Name),
				CurrentValue = currentVal?.ToString() ?? "",
				DefaultValue = defaultVal?.ToString() ?? "",
				IsBool = isBool,
				BoolValue = isBool && currentVal is true,
				DefaultBoolValue = isBool && defaultVal is true,
				ChoiceOptions = choiceOptions,
				PropertyInfo = prop
			};
			row.PropertyChanged += (_, _) => Commit(token, CommitFormatOptions);
			_formatRows.Add(row);
		}

		var formatGrid = new DataGrid
		{
			ItemsSource = _formatRows,
			AutoGenerateColumns = false,
			CanUserReorderColumns = false,
			CanUserResizeColumns = true,
			CanUserSortColumns = false,
			HeadersVisibility = DataGridHeadersVisibility.Column,
			GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
			IsReadOnly = false,
			MinHeight = 400,
			FontSize = 13,
		};

		Helpers.DataGridBehaviors.AttachCopyGuard(formatGrid,
			item => item is FormatOptionRow row ? $"{row.Name}\t{row.CurrentValue}" : null);

		formatGrid.Columns.Add(new DataGridTextColumn
		{
			Header = "Setting",
			Binding = new Binding("Name"),
			Width = new DataGridLength(2, DataGridLengthUnitType.Star),
			IsReadOnly = true
		});

		// Value column: ToggleSwitch for bools, ComboBox for choices, TextBox for text
		var valueColumn = new DataGridTemplateColumn
		{
			Header = "Value",
			Width = new DataGridLength(1, DataGridLengthUnitType.Star),
		};
		valueColumn.CellTemplate = new FuncDataTemplate<FormatOptionRow>((row, _) =>
		{
			if (row == null) return new Panel();
			var container = new Panel();

			if (row.IsBool)
			{
				var toggle = new ToggleSwitch
				{
					[!ToggleSwitch.IsCheckedProperty] = new Binding("BoolValue"),
					Margin = new Thickness(4, 0),
					VerticalAlignment = VerticalAlignment.Center,
				};
				container.Children.Add(toggle);
			}
			else if (row.IsChoice)
			{
				var combo = new ComboBox
				{
					ItemsSource = row.ChoiceOptions,
					[!ComboBox.SelectedItemProperty] = new Binding("CurrentValue"),
					VerticalAlignment = VerticalAlignment.Center,
					MinHeight = 0, Height = 26, FontSize = 12,
					Margin = new Thickness(4, 0),
				};
				container.Children.Add(combo);
			}
			else
			{
				var tb = new TextBox
				{
					[!TextBox.TextProperty] = new Binding("CurrentValue"),
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(4, 0),
				};
				container.Children.Add(tb);
			}

			return container;
		}, supportsRecycling: false);
		formatGrid.Columns.Add(valueColumn);

		// Default column: disabled ToggleSwitch for bools, TextBlock for others
		var defaultColumn = new DataGridTemplateColumn
		{
			Header = "Default",
			Width = new DataGridLength(1, DataGridLengthUnitType.Star),
			IsReadOnly = true,
		};
		defaultColumn.CellTemplate = new FuncDataTemplate<FormatOptionRow>((row, _) =>
		{
			if (row == null) return new Panel();
			var container = new Panel();

			if (row.IsBool)
			{
				var toggle = new ToggleSwitch
				{
					IsChecked = row.DefaultBoolValue,
					IsEnabled = false,
					Margin = new Thickness(4, 0),
					VerticalAlignment = VerticalAlignment.Center,
				};
				container.Children.Add(toggle);
			}
			else
			{
				container.Children.Add(new TextBlock
				{
					Text = row.DefaultValue,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(8, 0),
				});
			}

			return container;
		}, supportsRecycling: false);
		formatGrid.Columns.Add(defaultColumn);

		panel.Children.Add(formatGrid);

		var revertBtn = new Button
		{
			Content = "Revert Format to Defaults",
			Height = 28, Padding = new Thickness(12, 0),
			FontSize = 12,
			Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("AppButton")
		};
		revertBtn.Click += (_, _) =>
		{
			foreach (var row in _formatRows)
			{
				row.CurrentValue = row.DefaultValue;
				if (row.IsBool)
					row.BoolValue = row.DefaultBoolValue;
			}
		};
		panel.Children.Add(revertBtn);

		return panel;
	}

	private static string SplitPascalCase(string name)
	{
		var sb = new StringBuilder(name.Length + 8);
		for (int i = 0; i < name.Length; i++)
		{
			var c = name[i];
			if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
				sb.Append(' ');
			sb.Append(c);
		}
		return sb.ToString();
	}

	// ── Helpers ──────────────────────────────────────────────────────

	private static readonly SolidColorBrush ChapterHeaderBg = new(Color.Parse("#2A2D35"));
	private static readonly SolidColorBrush ChapterHeaderFg = new(Color.Parse("#4FC3F7"));

	private static Border CreateChapterHeader(string text) => new()
	{
		Background = ChapterHeaderBg,
		CornerRadius = new CornerRadius(4),
		Padding = new Thickness(12, 6),
		Margin = new Thickness(0, 8, 0, 2),
		Child = new TextBlock
		{
			Text = text,
			FontSize = 15,
			FontWeight = FontWeight.SemiBold,
			Foreground = ChapterHeaderFg,
		}
	};

	private static StackPanel CreateRow(string label, Control control)
	{
		return new StackPanel
		{
			Spacing = 4,
			Children =
			{
				new TextBlock { Text = label, FontSize = 13 },
				control
			}
		};
	}

	private static ComboBox CreateTagComboBox((string Content, string Tag)[] options, string selectedTag)
	{
		var box = new ComboBox { Width = 200, Height = 32, FontSize = 13 };
		int selectedIndex = 0;
		for (int i = 0; i < options.Length; i++)
		{
			box.Items.Add(new ComboBoxItem { Content = options[i].Content, Tag = options[i].Tag });
			if (options[i].Tag == selectedTag)
				selectedIndex = i;
		}
		box.SelectedIndex = selectedIndex;
		return box;
	}

	private static NumericUpDown CreateNumericUpDown(int value, int min, int max) => new()
	{
		Value = value,
		Minimum = min, Maximum = max, FormatString = "0",
		Width = 120, Height = 32, FontSize = 13,
		HorizontalAlignment = HorizontalAlignment.Left,
		HorizontalContentAlignment = HorizontalAlignment.Left,
		TextAlignment = TextAlignment.Left,
	};

	private static string GetComboTag(ComboBox box) =>
		(box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

	/// <summary>
	/// Rebuilds <c>FormatOptions</c> from the grid rows, after any one of them changes.
	///
	/// <para>Starts from what is already stored rather than from a fresh
	/// <see cref="SqlFormatSettings"/>, because a row that cannot be read right now has to keep
	/// its current value. An int field is unreadable for as long as it is empty, which it is the
	/// moment you select its contents to retype them — and this runs on every keystroke now, not
	/// once when Save is pressed. Seeded from a fresh instance, clearing a field would drop the
	/// default into the draft immediately, and navigating away would make that stick: the same
	/// silent revert this whole change set out to stop.</para>
	/// </summary>
	private void CommitFormatOptions()
	{
		var fmt = new SqlFormatSettings();
		if (_settings.FormatOptions is { } stored)
		{
			foreach (var prop in typeof(SqlFormatSettings)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (!prop.CanRead || !prop.CanWrite) continue;
				try { prop.SetValue(fmt, prop.GetValue(stored)); }
				catch { /* leave this one at its default */ }
			}
		}

		foreach (var row in _formatRows)
		{
			try
			{
				var prop = row.PropertyInfo;
				object? value;
				if (prop.PropertyType == typeof(bool))
					value = row.BoolValue;
				else if (prop.PropertyType == typeof(int))
				{
					// A half-typed number is not a value yet; the seeding above keeps the last good one.
					if (!int.TryParse(row.CurrentValue, out var intVal)) continue;
					value = intVal;
				}
				else
					value = row.CurrentValue;
				prop.SetValue(fmt, value);
			}
			catch { /* skip bad values */ }
		}
		_settings.FormatOptions = fmt;
	}

	/// <summary>
	/// Checks the stored colours parse, and shows the user the ones that do not.
	///
	/// <para>Validated against the settings rather than the text boxes, because the boxes only
	/// exist while their section is on screen. Saving from another section used to redden a set of
	/// detached controls nobody could see and then refuse to save, with no visible reason; now an
	/// invalid colour brings its section forward and marks itself.</para>
	/// </summary>
	private bool ColorsAreValid()
	{
		/* Only the rows in play. The stored list is allowed to run past the current count so a
		   colour survives the count going down and back up, but those extra entries have no row
		   and no box — refusing a save over one would be a dead end the user cannot get out of. */
		var bad = new List<int>();
		var colors = _settings.MultiQsTopDbColors;
		var visible = Math.Min(colors.Count, _settings.MultiQsTopDbCount);
		for (int i = 0; i < visible; i++)
		{
			try { Color.Parse(colors[i]); }
			catch { bad.Add(i); }
		}

		if (bad.Count == 0)
		{
			foreach (var tb in _colorTextBoxes)
				tb.BorderBrush = null;
			return true;
		}

		if (SectionList.SelectedIndex != 0)
			SectionList.SelectedIndex = 0;  // raises SelectionChanged, which rebuilds the section

		foreach (var i in bad)
			if (i < _colorTextBoxes.Count)
				_colorTextBoxes[i].BorderBrush = Brushes.Red;

		return false;
	}

	// ── Button handlers ──────────────────────────────────────────────

	private void Save_Click(object? sender, RoutedEventArgs e)
	{
		/* Nothing is read back off the controls here. Every section writes its edits into
		   _settings as they happen, so this method's job is to validate, apply and persist what
		   is already there. Reading at save time was the shape that produced three separate bugs:
		   values from sections the user never opened, values from sections rebuilt since, and
		   edits lost entirely by leaving a section and coming back. */
		if (!ColorsAreValid())
			return;

		// Apply live settings
		if (Enum.TryParse<TimeDisplayMode>(_settings.QueryStoreDefaultTimeDisplay, true, out var tdm))
			TimeDisplayHelper.Current = tdm;

		AppSettingsService.Save(_settings);
		SaveIntegrations();
		_isDirty = false;
		SettingsSaved?.Invoke(_settings);
		Close();
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e)
	{
		TryClose();
	}

	private void Reset_Click(object? sender, RoutedEventArgs e)
	{
		var fresh = new AppSettings();
		var section = SectionList.SelectedIndex;

		switch (section)
		{
			case 0: // Query Store
				_settings.QueryStoreSlicerDays = fresh.QueryStoreSlicerDays;
				_settings.QueryStoreDefaultMetric = fresh.QueryStoreDefaultMetric;
				_settings.QueryStoreTopLimit = fresh.QueryStoreTopLimit;
				_settings.QueryStoreDefaultTimeRange = fresh.QueryStoreDefaultTimeRange;
				_settings.QueryStoreDefaultTimeDisplay = fresh.QueryStoreDefaultTimeDisplay;
				_settings.QueryStoreDefaultGroupBy = fresh.QueryStoreDefaultGroupBy;
				_settings.MultiQsTopDbCount = fresh.MultiQsTopDbCount;
				_settings.MultiQsTopDbColors = [.. AppSettingsService.DefaultTopDbColors];
				break;
			case 1: // Query History
				_settings.QueryHistoryDefaultMetric = fresh.QueryHistoryDefaultMetric;
				_settings.QueryHistoryMaxPlans = fresh.QueryHistoryMaxPlans;
				break;
			case 2: // Script Options
				_settings.FormatOptions = new SqlFormatSettings();
				break;
			case 3: // Integrations
				// Pressed while looking at this section, so the saved proxy password goes too.
				ResetIntegrations(clearStoredPassword: true);
				break;
		}

		_isDirty = true;
		ShowSection(section);
	}

	private void ResetAll_Click(object? sender, RoutedEventArgs e)
	{
		var fresh = new AppSettings
		{
			RecentPlans = _settings.RecentPlans,
			OpenTabs = _settings.OpenTabs,
			AccuracyRatioDivergenceLimit = _settings.AccuracyRatioDivergenceLimit
		};
		_settings = fresh;

		/* Integrations live outside AppSettings, so replacing it above misses them, and a
		   "Reset All" that quietly skipped a visible section would be a lie. The saved proxy
		   password is the exception and stays: this button is unconfirmed and reachable from
		   every section, so someone restoring the Query Store defaults would lose a credential
		   they never came here for, with nothing on screen to warn them and no real way back.
		   Reset Section, pressed on Integrations, is the gesture that means that. */
		ResetIntegrations(clearStoredPassword: false);

		_isDirty = true;
		ShowSection(SectionList.SelectedIndex);
	}

	protected override void OnClosing(WindowClosingEventArgs e)
	{
		if (_isDirty)
		{
			e.Cancel = true;
			TryClose();
			return;
		}
		base.OnClosing(e);
	}

	private async void TryClose()
	{
		/* Only one close walk at a time. OnClosing cancels every close unconditionally - one
		   arriving mid-prompt must not fall through to base while the prompt is still asking -
		   but a second one must not stack a second discard dialog. Mirrors MainWindow's
		   _closeWalkInProgress latch; the walk's own Close() below clears _isDirty first, so it
		   sails through OnClosing rather than being swallowed here. */
		if (_closeWalkInProgress)
			return;

		if (!_isDirty)
		{
			Close();
			return;
		}

		_closeWalkInProgress = true;
		try
		{
			if (await ShowDiscardDialog())
			{
				_isDirty = false;
				Close();
			}
		}
		finally
		{
			// Cleared however the walk ends, so Cancel or the X can start a fresh one.
			_closeWalkInProgress = false;
		}
	}

	private async Task<bool> ShowDiscardDialog()
	{
		var tcs = new TaskCompletionSource<bool>();

		var dialog = new Window
		{
			Title = "Unsaved Changes",
			Width = 360, Height = 160,
			MinWidth = 360, MinHeight = 160,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = (IBrush)this.FindResource("BackgroundBrush")!,
			Foreground = (IBrush)this.FindResource("ForegroundBrush")!,
			Content = new StackPanel
			{
				Margin = new Thickness(20),
				Children =
				{
					new TextBlock
					{
						Text = "You have unsaved changes. Discard them?",
						FontSize = 13, TextWrapping = TextWrapping.Wrap,
						Margin = new Thickness(0, 0, 0, 16)
					}
				}
			}
		};

		var discardBtn = new Button
		{
			Content = "Discard", Height = 32, Width = 90,
			Padding = new Thickness(16, 0), FontSize = 12,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center,
			Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("AppButton")
		};
		var cancelBtn = new Button
		{
			Content = "Cancel", Height = 32, Width = 90,
			Padding = new Thickness(16, 0), FontSize = 12,
			Margin = new Thickness(8, 0, 0, 0),
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center,
			Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("AppButton")
		};

		var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttonPanel.Children.Add(discardBtn);
		buttonPanel.Children.Add(cancelBtn);
		((StackPanel)dialog.Content!).Children.Add(buttonPanel);

		discardBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
		cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
		// Closed, not Closing: an owner-driven teardown closes the window without ever raising
		// Closing, which would leave the await below waiting forever.
		dialog.Closed += (_, _) => tcs.TrySetResult(false);

		await dialog.ShowDialog(this);
		return await tcs.Task;
	}
}
