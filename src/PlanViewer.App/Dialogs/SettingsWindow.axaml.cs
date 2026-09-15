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

	// QueryStore controls
	private NumericUpDown? _slicerDaysBox;
	private ComboBox? _defaultMetricBox;
	private NumericUpDown? _topLimitBox;
	private ComboBox? _defaultTimeRangeBox;
	private ComboBox? _defaultTimeDisplayBox;
	private ComboBox? _defaultGroupByBox;

	// Multi QS Overview controls
	private NumericUpDown? _topDbCountBox;
	private readonly List<TextBox> _colorTextBoxes = new();
	private readonly List<Rectangle> _colorPreviews = new();
	private StackPanel? _colorListPanel;

	// Query History controls
	private ComboBox? _historyMetricBox;
	private NumericUpDown? _historyMaxPlansBox;

	// Script Options (Format) controls
	private readonly ObservableCollection<FormatOptionRow> _formatRows = new();
	private DataGrid? _formatGrid;

	/* Integrations (MCP server + proxy). These do not live in AppSettings: MCP is two raw keys in
	   settings.json and the proxy password is in the OS credential store, so this section keeps its
	   own pending state and commits it in Save_Click. Unlike the other sections, the controls write
	   back on every change rather than being read at save time — the pending fields survive
	   navigating away and back, which an orphaned control's value does not. */
	private CheckBox? _mcpEnabledBox;
	private NumericUpDown? _mcpPortBox;
	private TextBlock? _mcpCopyStatus;
	private RadioButton? _proxySystemRadio;
	private RadioButton? _proxyManualRadio;
	private Grid? _proxyManualPanel;
	private TextBox? _proxyPasswordBox;

	private bool _integrationsLoaded;
	private bool _mcpEnabled;
	private int _mcpPort = 5152;
	private ProxyMode _proxyMode = ProxyMode.System;
	private string _proxyAddress = "";
	private string _proxyUsername = "";
	private string _proxyTypedPassword = "";
	private bool _hasStoredProxyPassword;

	/// <summary>The loaded values, so Save can skip writing a section the user never touched.</summary>
	private (bool Enabled, int Port, ProxyMode Mode, string Address, string Username) _integrationsOriginal;

	internal event Action<AppSettings>? SettingsSaved;

	public SettingsWindow()
	{
		_settings = AppSettingsService.Load();
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

	/// <summary>Marks the dialog dirty unless a section is still being built and initialized.</summary>
	private void MarkDirty()
	{
		if (!_building)
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
		var panel = new StackPanel { Spacing = 16 };

		// Chapter 1: Query Store
		panel.Children.Add(CreateChapterHeader("Query Store"));

		_slicerDaysBox = CreateNumericUpDown(_settings.QueryStoreSlicerDays, 1, 365);
		_slicerDaysBox.ValueChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default history length (days)", _slicerDaysBox));

		_defaultMetricBox = CreateTagComboBox(MetricOptions, _settings.QueryStoreDefaultMetric);
		_defaultMetricBox.SelectionChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default metric for top", _defaultMetricBox));

		_topLimitBox = CreateNumericUpDown(_settings.QueryStoreTopLimit, 1, 200);
		_topLimitBox.ValueChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Top elements limit", _topLimitBox));

		_defaultTimeRangeBox = CreateTagComboBox(TimeRangeOptions, _settings.QueryStoreDefaultTimeRange);
		_defaultTimeRangeBox.SelectionChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default time range", _defaultTimeRangeBox));

		_defaultTimeDisplayBox = CreateTagComboBox(TimeDisplayOptions, _settings.QueryStoreDefaultTimeDisplay);
		_defaultTimeDisplayBox.SelectionChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default time display", _defaultTimeDisplayBox));

		_defaultGroupByBox = CreateTagComboBox(GroupByOptions, _settings.QueryStoreDefaultGroupBy);
		_defaultGroupByBox.SelectionChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default group by", _defaultGroupByBox));

		// Chapter 2: Multi QS Overview
		panel.Children.Add(CreateChapterHeader("Multi QS Overview"));

		_topDbCountBox = CreateNumericUpDown(_settings.MultiQsTopDbCount, 2, 20);
		_topDbCountBox.ValueChanged += (_, e) =>
		{
			MarkDirty();
			RebuildColorList();
		};
		panel.Children.Add(CreateRow("Number of top databases", _topDbCountBox));

		_colorListPanel = new StackPanel { Spacing = 4 };
		RebuildColorList();
		panel.Children.Add(CreateRow("Top database colors", _colorListPanel));

		return panel;
	}

	private void RebuildColorList()
	{
		if (_colorListPanel == null) return;
		_colorListPanel.Children.Clear();
		_colorTextBoxes.Clear();
		_colorPreviews.Clear();

		var count = (int)(_topDbCountBox?.Value ?? _settings.MultiQsTopDbCount);
		var colors = _settings.MultiQsTopDbColors;

		for (int i = 0; i < count; i++)
		{
			var hex = i < colors.Count ? colors[i] : AppSettingsService.DefaultTopDbColors[i % AppSettingsService.DefaultTopDbColors.Count];
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
			textBox.TextChanged += (_, _) =>
			{
				MarkDirty();
				if (index < _colorPreviews.Count)
					_colorPreviews[index].Fill = TryParseBrush(textBox.Text ?? "");
			};

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
		var panel = new StackPanel { Spacing = 16 };
		panel.Children.Add(CreateChapterHeader("Query History"));

		_historyMetricBox = CreateTagComboBox(HistoryMetricOptions, _settings.QueryHistoryDefaultMetric);
		_historyMetricBox.SelectionChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Default chart metric", _historyMetricBox));

		_historyMaxPlansBox = CreateNumericUpDown(_settings.QueryHistoryMaxPlans, 1, 100);
		_historyMaxPlansBox.ValueChanged += (_, _) => MarkDirty();
		panel.Children.Add(CreateRow("Max plans fetched per query", _historyMaxPlansBox));

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
		   watermark says it is saved, and an empty box at save time means "keep what is there". */
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

		var panel = new StackPanel { Spacing = 16 };

		panel.Children.Add(CreateChapterHeader("MCP Server"));
		panel.Children.Add(new TextBlock
		{
			Text = "Lets an AI assistant read plans you have open in Performance Studio. "
				 + "The server listens on this machine only.",
			FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 520
		});

		_mcpEnabledBox = new CheckBox
		{
			Content = "Enable MCP server",
			IsChecked = _mcpEnabled,
			FontSize = 13
		};
		_mcpEnabledBox.IsCheckedChanged += (_, _) =>
		{
			_mcpEnabled = _mcpEnabledBox?.IsChecked == true;
			MarkDirty();
		};
		panel.Children.Add(_mcpEnabledBox);

		_mcpPortBox = CreateNumericUpDown(_mcpPort, 1024, 65535);
		_mcpPortBox.ValueChanged += (_, _) =>
		{
			_mcpPort = (int)(_mcpPortBox?.Value ?? 5152);
			MarkDirty();
		};
		panel.Children.Add(CreateRow("Port", _mcpPortBox));

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

		_proxySystemRadio = new RadioButton
		{
			GroupName = "ProxyMode",
			Content = "Use system proxy (Windows credentials)",
			FontSize = 13,
			IsChecked = _proxyMode == ProxyMode.System
		};
		_proxyManualRadio = new RadioButton
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
			_proxyMode = _proxyManualRadio?.IsChecked == true ? ProxyMode.Manual : ProxyMode.System;
			if (_proxyManualPanel != null)
				_proxyManualPanel.IsVisible = _proxyMode == ProxyMode.Manual;
			MarkDirty();
		}
		_proxySystemRadio.IsCheckedChanged += OnProxyModeChanged;
		_proxyManualRadio.IsCheckedChanged += OnProxyModeChanged;

		panel.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16,
			Children = { _proxySystemRadio, _proxyManualRadio }
		});

		var addressBox = CreateProxyInput(_proxyAddress, "http://proxy.example.com:8080");
		addressBox.TextChanged += (_, _) => { _proxyAddress = addressBox.Text ?? ""; MarkDirty(); };

		var usernameBox = CreateProxyInput(_proxyUsername, "DOMAIN\\user");
		usernameBox.TextChanged += (_, _) => { _proxyUsername = usernameBox.Text ?? ""; MarkDirty(); };

		_proxyPasswordBox = CreateProxyInput(_proxyTypedPassword,
			_hasStoredProxyPassword ? "(saved — leave blank to keep)" : "");
		_proxyPasswordBox.PasswordChar = '•';
		_proxyPasswordBox.TextChanged += (_, _) =>
		{
			_proxyTypedPassword = _proxyPasswordBox?.Text ?? "";
			MarkDirty();
		};

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
		AddProxyRow(_proxyManualPanel, 2, "Password", _proxyPasswordBox);
		panel.Children.Add(_proxyManualPanel);

		return panel;
	}

	private static TextBox CreateProxyInput(string text, string watermark) => new()
	{
		Text = text,
		Watermark = watermark,
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
		if (_mcpCopyStatus != null)
		{
			_mcpCopyStatus.Text = await ClipboardHelper.TrySetTextAsync(this, command)
				? "Copied to clipboard."
				: "Clipboard busy — try again.";
		}
	}

	/// <summary>
	/// Returns the section to a stock MCP server and a system proxy. Only the pending state is
	/// reset — nothing is written, and nothing touches the stored password, until Save.
	/// </summary>
	private void ResetIntegrations()
	{
		EnsureIntegrationsLoaded();

		var fresh = new Mcp.McpSettings();
		_mcpEnabled = fresh.Enabled;
		_mcpPort = fresh.Port;
		_proxyMode = ProxyMode.System;
		_proxyAddress = "";
		_proxyUsername = "";
		_proxyTypedPassword = "";
	}

	/// <summary>Writes MCP and proxy settings, but only when something in the section changed.</summary>
	private void SaveIntegrations()
	{
		if (!_integrationsLoaded) return;

		var current = (_mcpEnabled, _mcpPort, _proxyMode, _proxyAddress, _proxyUsername);
		var typedNewPassword = _proxyTypedPassword.Length > 0;
		if (current == _integrationsOriginal && !typedNewPassword)
			return;

		SettingsFile.Update(o =>
		{
			o["mcp_enabled"] = _mcpEnabled;
			o["mcp_port"] = _mcpPort;
		});

		var proxy = new ProxySettings
		{
			Mode = _proxyMode,
			Address = _proxyAddress,
			Username = _proxyUsername,
			Password = _proxyTypedPassword,
			// An empty box plus an existing stored password means "keep what is there".
			TouchCredential = typedNewPassword || !_hasStoredProxyPassword
		};
		proxy.Save();
		if (proxy.TouchCredential)
			_hasStoredProxyPassword = typedNewPassword;

		_integrationsOriginal = current;
		_proxyTypedPassword = "";
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
			row.PropertyChanged += (_, _) => MarkDirty();
			_formatRows.Add(row);
		}

		_formatGrid = new DataGrid
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

		Helpers.DataGridBehaviors.AttachCopyGuard(_formatGrid,
			item => item is FormatOptionRow row ? $"{row.Name}\t{row.CurrentValue}" : null);

		_formatGrid.Columns.Add(new DataGridTextColumn
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
		_formatGrid.Columns.Add(valueColumn);

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
		_formatGrid.Columns.Add(defaultColumn);

		panel.Children.Add(_formatGrid);

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

	private static string GetComboTag(ComboBox? box) =>
		(box?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

	// ── Button handlers ──────────────────────────────────────────────

	private void Save_Click(object? sender, RoutedEventArgs e)
	{
		// Read QueryStore settings
		_settings.QueryStoreSlicerDays = (int)(_slicerDaysBox?.Value ?? 30);
		_settings.QueryStoreDefaultMetric = GetComboTag(_defaultMetricBox);
		_settings.QueryStoreTopLimit = (int)(_topLimitBox?.Value ?? 25);
		_settings.QueryStoreDefaultTimeRange = GetComboTag(_defaultTimeRangeBox);
		_settings.QueryStoreDefaultTimeDisplay = GetComboTag(_defaultTimeDisplayBox);
		_settings.QueryStoreDefaultGroupBy = GetComboTag(_defaultGroupByBox);

		// Read Multi QS Overview settings
		_settings.MultiQsTopDbCount = (int)(_topDbCountBox?.Value ?? 5);

		// Validate hex color inputs before saving
		var hasInvalidColor = false;
		foreach (var tb in _colorTextBoxes)
		{
			try
			{
				Color.Parse(tb.Text ?? "");
				tb.BorderBrush = null; // reset to default
			}
			catch
			{
				tb.BorderBrush = Brushes.Red;
				hasInvalidColor = true;
			}
		}
		if (hasInvalidColor)
			return;

		_settings.MultiQsTopDbColors = _colorTextBoxes.Select(tb => tb.Text ?? "#555555").ToList();

		/* Read Query History settings — guarded the way Format Options below already is. These
		   fields hold the controls from the last time the section was built, and the section is
		   only built once the user visits it. Unguarded, saving from any other section read a
		   null combo as "" and a null spinner as 10, silently wiping both stored values: open
		   Settings, change a Query Store option, hit Save, and a configured max-plans of 50 came
		   back as 10 with the default metric blanked. */
		if (_historyMetricBox != null)
			_settings.QueryHistoryDefaultMetric = GetComboTag(_historyMetricBox);
		if (_historyMaxPlansBox != null)
			_settings.QueryHistoryMaxPlans = (int)(_historyMaxPlansBox.Value ?? 10);

		// Read Format Options
		if (_formatRows.Count > 0)
		{
			var fmt = new SqlFormatSettings();
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
				ResetIntegrations();
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
		// Integrations live outside AppSettings, so replacing it above misses them. A "Reset All"
		// that quietly skipped a visible section would be a lie. Still only pending state — the
		// stored proxy password survives until the user actually saves.
		ResetIntegrations();
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
