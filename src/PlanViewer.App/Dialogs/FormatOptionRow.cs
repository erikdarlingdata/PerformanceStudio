using System.ComponentModel;
using System.Reflection;

namespace PlanViewer.App.Dialogs;

internal class FormatOptionRow : INotifyPropertyChanged
{
	private string _currentValue = "";
	private bool _boolValue;

	public string Name { get; set; } = "";

	public bool IsBool { get; set; }

	public bool BoolValue
	{
		get => _boolValue;
		set
		{
			if (_boolValue == value) return;
			_boolValue = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
			// Keep CurrentValue in sync for serialization
			if (IsBool)
			{
				_currentValue = value.ToString();
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
			}
		}
	}

	public bool DefaultBoolValue { get; set; }

	public string[]? ChoiceOptions { get; set; }

	public bool IsChoice => ChoiceOptions != null;

	public bool IsText => !IsBool && !IsChoice;

	public string CurrentValue
	{
		get => _currentValue;
		set
		{
			if (_currentValue == value) return;
			_currentValue = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
		}
	}

	public string DefaultValue { get; set; } = "";

	internal PropertyInfo PropertyInfo { get; set; } = null!;

	public event PropertyChangedEventHandler? PropertyChanged;
}
