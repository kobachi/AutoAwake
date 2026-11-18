using System;

namespace Kobachi.AutoAwake.Models;

/// <summary>Stores sleep-prevention settings for a specific application.</summary>
public sealed partial class ApplicationSettings : BindableObject {
	/// <summary>Gets or sets the INI section name for this application rule.</summary>
	public string SectionName {
		get;
		set => SetProperty(ref field, value);
	} = string.Empty;

	/// <summary>Gets or sets the absolute path to the application executable.</summary>
	public string Path {
		get;
		set => SetProperty(ref field, value);
	} = string.Empty;

	/// <summary>Gets or sets the sleep-prevention action for the application.</summary>
	public SleepPreventionAction Action {
		get;
		set {
			if (SetProperty(ref field, value)) {
				OnPropertyChanged(nameof(ActionIndex));
			}
		}
	} = SleepPreventionAction.NoSleep;

	/// <summary>Gets or sets the combo-box index corresponding to <see cref="Action"/>.</summary>
	public int ActionIndex {
		get => Action switch {
			SleepPreventionAction.NoSleep => 0,
			SleepPreventionAction.KeepDisplay => 1,
			SleepPreventionAction.KeepUnlocked => 2,
			_ => throw new InvalidOperationException("Invalid sleep prevention action.")
		};
		set {
			Action = value switch {
				0 => SleepPreventionAction.NoSleep,
				1 => SleepPreventionAction.KeepDisplay,
				2 => SleepPreventionAction.KeepUnlocked,
				_ => Action
			};
		}
	}

	/// <summary>Gets or sets whether the rule applies only while a window is visible.</summary>
	public bool VisibleWindowOnly {
		get;
		set => SetProperty(ref field, value);
	}
}
