using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Kobachi.AutoAwake.Models;

/// <summary>Stores application-wide behavior settings.</summary>
public sealed partial class AwakeSettings : BindableObject {
	/// <summary>Gets or sets whether Windows Update activity is suppressed.</summary>
	public bool SuppressWindowsUpdate {
		get;
		set => SetProperty(ref field, value);
	} = true;

	/// <summary>Gets or sets the interval, in seconds, between sleep-prevention requests.</summary>
	public int Interval {
		get;
		set => SetProperty(ref field, value);
	} = 30;

	/// <summary>Gets or sets whether CPU usage-based sleep prevention is enabled.</summary>
	public bool CpuUsageEnabled {
		get;
		set => SetProperty(ref field, value);
	} = true;

	/// <summary>Gets or sets the CPU usage threshold percentage.</summary>
	public int CpuUsageThreshold {
		get;
		set => SetProperty(ref field, value);
	} = 80;

	/// <summary>Gets or sets the sleep-prevention action used when the CPU threshold is reached.</summary>
	public SleepPreventionAction CpuUsageAction {
		get;
		set => SetProperty(ref field, value);
	} = SleepPreventionAction.NoSleep;

	/// <summary>Gets the global baseline hotkey settings.</summary>
	public HotkeySettings Hotkeys { get; } = new();

	/// <summary>Gets the per-application sleep-prevention settings.</summary>
	public ObservableCollection<ApplicationSettings> Applications { get; } = new();

	/// <summary>Adds an executable to the per-application settings.</summary>
	public bool AddApplication(string filePath) {
		var path = Path.GetFullPath(filePath);
		if (Applications.Any(application => string.Equals(Path.GetFullPath(application.Path), path, StringComparison.OrdinalIgnoreCase))) {
			return false;
		}
		var name = Path.GetFileNameWithoutExtension(path).Replace('[', '(').Replace(']', ')').Trim();
		if (string.IsNullOrEmpty(name)) {
			throw new ArgumentException(@"The executable file name cannot be used as an app name.", nameof(filePath));
		}
		var section = name;
		for (var suffix = 2; IsSectionInUse(section); suffix++) {
			section = $"{name} ({suffix})";
		}
		Applications.Add(new ApplicationSettings { SectionName = section, Path = path, Action = SleepPreventionAction.NoSleep });
		return true;
	}

	/// <summary>Returns whether a section name is reserved or already used by an application.</summary>
	private bool IsSectionInUse(string section) => IsReservedSection(section) ||
		Applications.Any(application => string.Equals(application.SectionName, section, StringComparison.OrdinalIgnoreCase));

	/// <summary>Returns whether a section name is reserved for general or window settings.</summary>
	private static bool IsReservedSection(string section) =>
		string.Equals(section, "General", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase);
}
