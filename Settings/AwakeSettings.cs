using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Kobachi.AutoAwake {
	public enum SleepPreventionAction {
		NoSleep,
		KeepDisplay,
		KeepUnlocked
	}

	public abstract partial class BindableObject : INotifyPropertyChanged {
		public event PropertyChangedEventHandler? PropertyChanged;

		protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
			if (EqualityComparer<T>.Default.Equals(field, value)) {
				return false;
			}

			field = value;
			OnPropertyChanged(propertyName);
			return true;
		}

		protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}

	public sealed partial class AwakeSettings : BindableObject {
		private int interval = 30;
		private bool suppressWindowsUpdate = true;
		private bool cpuUsageEnabled = true;
		private int cpuUsageThreshold = 80;
		private SleepPreventionAction cpuUsageAction = SleepPreventionAction.NoSleep;

		public bool SuppressWindowsUpdate {
			get => suppressWindowsUpdate;
			set => SetProperty(ref suppressWindowsUpdate, value);
		}

		public int Interval {
			get => interval;
			set => SetProperty(ref interval, value);
		}

		public bool CpuUsageEnabled {
			get => cpuUsageEnabled;
			set => SetProperty(ref cpuUsageEnabled, value);
		}

		public int CpuUsageThreshold {
			get => cpuUsageThreshold;
			set => SetProperty(ref cpuUsageThreshold, value);
		}

		public SleepPreventionAction CpuUsageAction {
			get => cpuUsageAction;
			set => SetProperty(ref cpuUsageAction, value);
		}

		public ObservableCollection<ApplicationSettings> Applications { get; } = new();

		public bool AddApplication(string filePath) {
			string path = Path.GetFullPath(filePath);
			if (Applications.Any(application => string.Equals(Path.GetFullPath(application.Path), path, StringComparison.OrdinalIgnoreCase))) return false;
			string name = Path.GetFileNameWithoutExtension(path).Replace('[', '(').Replace(']', ')').Trim();
			if (string.IsNullOrEmpty(name)) throw new ArgumentException("The executable file name cannot be used as an app name.", nameof(filePath));
			string section = name;
			for (int suffix = 2; string.Equals(section, "General", StringComparison.OrdinalIgnoreCase) ||
				Applications.Any(application => string.Equals(application.SectionName, section, StringComparison.OrdinalIgnoreCase)); suffix++) {
				section = $"{name} ({suffix})";
			}
			Applications.Add(new ApplicationSettings { SectionName = section, Path = path, Action = SleepPreventionAction.NoSleep });
			return true;
		}
	}

	public sealed partial class ApplicationSettings : BindableObject {
		private string sectionName = string.Empty;
		private string path = string.Empty;
		private SleepPreventionAction action = SleepPreventionAction.NoSleep;
		private bool visibleWindowOnly;

		public string SectionName {
			get => sectionName;
			set => SetProperty(ref sectionName, value);
		}

		public string Path {
			get => path;
			set => SetProperty(ref path, value);
		}

		public SleepPreventionAction Action {
			get => action;
			set {
				if (SetProperty(ref action, value)) OnPropertyChanged(nameof(ActionIndex));
			}
		}

		public int ActionIndex {
			get => Action switch {
				SleepPreventionAction.NoSleep => 0,
				SleepPreventionAction.KeepDisplay => 1,
				SleepPreventionAction.KeepUnlocked => 2,
				_ => throw new InvalidOperationException("Invalid sleep prevention action.")
			};
			set {
				if (value == 0) Action = SleepPreventionAction.NoSleep;
				else if (value == 1) Action = SleepPreventionAction.KeepDisplay;
				else if (value == 2) Action = SleepPreventionAction.KeepUnlocked;
			}
		}

		public bool VisibleWindowOnly {
			get => visibleWindowOnly;
			set => SetProperty(ref visibleWindowOnly, value);
		}
	}
}
