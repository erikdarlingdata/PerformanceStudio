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
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Dialogs;

internal partial class SettingsWindow : Window
{
	private AppSettings _settings;
	private bool _isDirty;

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
		DetailPanel.Content = index switch
		{
			0 => BuildQueryStoreSection(),
			1 => BuildQueryHistorySection(),
			2 => BuildScriptOptionsSection(),
			_ => null
		};
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
		_slicerDaysBox.ValueChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default history length (days)", _slicerDaysBox));

		_defaultMetricBox = CreateTagComboBox(MetricOptions, _settings.QueryStoreDefaultMetric);
		_defaultMetricBox.SelectionChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default metric for top", _defaultMetricBox));

		_topLimitBox = CreateNumericUpDown(_settings.QueryStoreTopLimit, 1, 200);
		_topLimitBox.ValueChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Top elements limit", _topLimitBox));

		_defaultTimeRangeBox = CreateTagComboBox(TimeRangeOptions, _settings.QueryStoreDefaultTimeRange);
		_defaultTimeRangeBox.SelectionChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default time range", _defaultTimeRangeBox));

		_defaultTimeDisplayBox = CreateTagComboBox(TimeDisplayOptions, _settings.QueryStoreDefaultTimeDisplay);
		_defaultTimeDisplayBox.SelectionChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default time display", _defaultTimeDisplayBox));

		_defaultGroupByBox = CreateTagComboBox(GroupByOptions, _settings.QueryStoreDefaultGroupBy);
		_defaultGroupByBox.SelectionChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default group by", _defaultGroupByBox));

		// Chapter 2: Multi QS Overview
		panel.Children.Add(CreateChapterHeader("Multi QS Overview"));

		_topDbCountBox = CreateNumericUpDown(_settings.MultiQsTopDbCount, 2, 20);
		_topDbCountBox.ValueChanged += (_, e) =>
		{
			_isDirty = true;
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
				_isDirty = true;
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
		_historyMetricBox.SelectionChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Default chart metric", _historyMetricBox));

		_historyMaxPlansBox = CreateNumericUpDown(_settings.QueryHistoryMaxPlans, 1, 100);
		_historyMaxPlansBox.ValueChanged += (_, _) => _isDirty = true;
		panel.Children.Add(CreateRow("Max plans fetched per query", _historyMaxPlansBox));

		return panel;
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
			row.PropertyChanged += (_, _) => _isDirty = true;
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

		// Read Query History settings
		_settings.QueryHistoryDefaultMetric = GetComboTag(_historyMetricBox);
		_settings.QueryHistoryMaxPlans = (int)(_historyMaxPlansBox?.Value ?? 10);

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
		}

		_isDirty = true;
		ShowSection(section);
	}

	private void ResetAll_Click(object? sender, RoutedEventArgs e)
	{
		var fresh = new AppSettings
		{
			RecentPlans = _settings.RecentPlans,
			OpenPlans = _settings.OpenPlans,
			AccuracyRatioDivergenceLimit = _settings.AccuracyRatioDivergenceLimit
		};
		_settings = fresh;
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
		if (!_isDirty)
		{
			_isDirty = false;
			Close();
			return;
		}

		var result = await ShowDiscardDialog();
		if (result)
		{
			_isDirty = false;
			Close();
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
		dialog.Closing += (_, _) => tcs.TrySetResult(false);

		await dialog.ShowDialog(this);
		return await tcs.Task;
	}
}
