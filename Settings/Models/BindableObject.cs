using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kobachi.AutoAwake.Models;

/// <summary>Base class that provides property change notifications.</summary>
public abstract partial class BindableObject : INotifyPropertyChanged {
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Updates a field and raises a notification when its value changes.</summary>
	protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
		if (EqualityComparer<T>.Default.Equals(field, value)) {
			return false;
		}

		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	/// <summary>Raises a change notification for the specified property.</summary>
	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
