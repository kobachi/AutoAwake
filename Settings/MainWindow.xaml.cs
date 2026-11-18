using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Kobachi.AutoAwake.Models;
using Kobachi.AutoAwake.Persistence;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Kobachi.AutoAwake;

/// <summary>Provides the settings window and coordinates its view with persisted application settings.</summary>
public sealed partial class MainWindow {
	/// <summary>Stores and persists the settings displayed in this window.</summary>
	private readonly SettingsStore settingsStore;

	/// <summary>Debounces saves of the window position and size.</summary>
	private readonly DispatcherQueueTimer boundsSaveTimer;

	/// <summary>Provides system theme and high-contrast settings for the window.</summary>
	private readonly ThemeSettings themeSettings;

	/// <summary>Dispatches title bar updates to the UI thread.</summary>
	private readonly DispatcherQueue uiQueue;

	/// <summary>Tracks whether initial control and settings synchronization is complete.</summary>
	private bool initialized;

	/// <summary>Tracks whether the window has closed.</summary>
	private bool closed;

	/// <summary>Prevents overlapping startup-task operations.</summary>
	private bool startupBusy;

	/// <summary>Gets the settings model displayed by this window.</summary>
	public AwakeSettings Settings => settingsStore.Settings;

	/// <summary>Initializes the settings window, loads settings, and wires up window events.</summary>
	public MainWindow() {
		// Load settings and initialize the XAML controls.
		settingsStore = SettingsStore.OpenDefault();
		settingsStore.SaveFailed += SettingsError.ShowSaveError;
		InitializeComponent();

		// Bind the settings and application information to the view.
		AutoAwakeNotRunningNotice.IsOpen = !IsAutoAwakeRunning();
		var about = new AboutInformation(typeof(MainWindow).Assembly);
		AboutTitle.Text = about.TitleAndVersion;
		AboutCopyright.Text = about.Copyright;
		ContentRoot.DataContext = Settings;
		Navigation.SelectedItem = GeneralNavigation;
		GeneralContent.Visibility = Visibility.Visible;
		initialized = true;

		// Configure the custom title bar and initialize theme handling.
		Title = AppTitleBar.Title;
		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		uiQueue = DispatcherQueue.GetForCurrentThread();
		themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
		themeSettings.Changed += ThemeSettings_Changed;
		ContentRoot.ActualThemeChanged += ContentRoot_ActualThemeChanged;
		AppTitleBar.Loaded += AppTitleBar_Loaded;
		UpdateTitleBarTheme();

		// Apply minimum dimensions and restore saved bounds or use a default size.
		if (AppWindow.Presenter is OverlappedPresenter presenter) {
			presenter.PreferredMinimumWidth = SettingsStore.MinimumWindowWidth;
			presenter.PreferredMinimumHeight = SettingsStore.MinimumWindowHeight;
		}
		if (settingsStore.WindowBounds is { } bounds) {
			var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
			var workArea = displayArea.WorkArea;
			var width = Math.Max(SettingsStore.MinimumWindowWidth, Math.Min(bounds.Width, workArea.Width));
			var height = Math.Max(SettingsStore.MinimumWindowHeight, Math.Min(bounds.Height, workArea.Height));
			var x = Math.Clamp(bounds.X, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
			var y = Math.Clamp(bounds.Y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
			AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
		} else {
			var initialSize = AppWindow.Size;
			AppWindow.Resize(new SizeInt32(
				Math.Max(SettingsStore.MinimumWindowWidth, initialSize.Width / 2),
				Math.Max(SettingsStore.MinimumWindowHeight, initialSize.Height / 2)
			));
		}
		AppWindow.SetIcon("AppIcon.ico");

		// Register window lifecycle and bounds-save event handlers.
		boundsSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		boundsSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
		boundsSaveTimer.IsRepeating = false;
		boundsSaveTimer.Tick += BoundsSaveTimer_Tick;
		AppWindow.Changed += AppWindow_Changed;
		Activated += MainWindow_Activated;
		Closed += MainWindow_Closed;
	}

	/// <summary>Saves the window bounds when the debounce timer elapses.</summary>
	private void BoundsSaveTimer_Tick(DispatcherQueueTimer sender, object args) => SaveWindowBounds();

	/// <summary>Refreshes the title bar and startup state when the window is activated.</summary>
	private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
		try {
			QueueTitleBarThemeUpdate();
			SaveWindowBounds();
			if (args.WindowActivationState != WindowActivationState.Deactivated) {
				await UpdateStartupAsync(null);
			}
		}
		catch (Exception e) {
			ShowStartupError(e);
		}
	}

	/// <summary>Saves window bounds and releases event subscriptions when the window closes.</summary>
	private void MainWindow_Closed(object sender, WindowEventArgs args) {
		closed = true;
		Activated -= MainWindow_Activated;
		themeSettings.Changed -= ThemeSettings_Changed;
		SaveWindowBounds();
		boundsSaveTimer.Stop();
		boundsSaveTimer.Tick -= BoundsSaveTimer_Tick;
		AppWindow.Changed -= AppWindow_Changed;
		ContentRoot.ActualThemeChanged -= ContentRoot_ActualThemeChanged;
		AppTitleBar.Loaded -= AppTitleBar_Loaded;
		settingsStore.Dispose();
	}

	/// <summary>Queues a title bar theme update after the content theme changes.</summary>
	private void ContentRoot_ActualThemeChanged(FrameworkElement sender, object args) => QueueTitleBarThemeUpdate();

	/// <summary>Queues a title bar theme update after the title bar loads.</summary>
	private void AppTitleBar_Loaded(object sender, RoutedEventArgs args) => QueueTitleBarThemeUpdate();

	/// <summary>Queues a title bar theme update after system theme settings change.</summary>
	private void ThemeSettings_Changed(ThemeSettings sender, object args) => QueueTitleBarThemeUpdate();

	/// <summary>Schedules a title bar theme update on the UI thread.</summary>
	private void QueueTitleBarThemeUpdate() {
		if (!uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
				if (!closed) {
					UpdateTitleBarTheme();
				}
			})) {
			Debug.WriteLine("Could not schedule the title bar theme update.");
		}
	}

	/// <summary>Applies the current app theme or high-contrast settings to the title bar.</summary>
	private void UpdateTitleBarTheme() {
		// WinUI can overwrite caption colors during title bar initialization and theme changes.
		var titleBar = AppWindow.TitleBar;
		if (themeSettings.HighContrast) {
			titleBar.PreferredTheme = TitleBarTheme.Legacy;
			titleBar.ButtonForegroundColor = null;
			titleBar.ButtonHoverForegroundColor = null;
		} else {
			var dark = ContentRoot.ActualTheme == ElementTheme.Dark;
			titleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
			var foreground = dark ? Colors.White : Colors.Black;
			titleBar.ButtonForegroundColor = foreground;
			titleBar.ButtonHoverForegroundColor = foreground;
		}
	}

	/// <summary>Starts a delayed save when the window position or size changes.</summary>
	private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) {
		if (args.DidPositionChange || args.DidSizeChange) {
			boundsSaveTimer.Start();
		}
	}

	/// <summary>Saves the current window position and size when they meet the minimum dimensions.</summary>
	private void SaveWindowBounds() {
		var position = AppWindow.Position;
		var size = AppWindow.Size;
		if (size.Width < SettingsStore.MinimumWindowWidth || size.Height < SettingsStore.MinimumWindowHeight) {
			return;
		}
		settingsStore.SaveWindowBounds(position.X, position.Y, size.Width, size.Height);
	}

	/// <summary>Toggles the navigation pane when requested by the title bar.</summary>
	private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

	/// <summary>Shows the content associated with the selected navigation item.</summary>
	private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
		if (!initialized) {
			return;
		}
		GeneralContent.Visibility = (NavigationViewItem)sender.SelectedItem == GeneralNavigation ? Visibility.Visible : Visibility.Collapsed;
		CpuContent.Visibility = (NavigationViewItem)sender.SelectedItem == CpuNavigation ? Visibility.Visible : Visibility.Collapsed;
		ApplicationsContent.Visibility = (NavigationViewItem)sender.SelectedItem == ApplicationsNavigation ? Visibility.Visible : Visibility.Collapsed;
		AboutContent.Visibility = ReferenceEquals(sender.SelectedItem, AboutNavigation) ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>Returns whether the AutoAwake core process is currently running.</summary>
	private static bool IsAutoAwakeRunning() {
		var processes = Process.GetProcessesByName("AutoAwakeCore");
		try {
			return processes.Length > 0;
		} finally {
			foreach (var process in processes) process.Dispose();
		}
	}

	/// <summary>Starts the AutoAwake core process and displays any launch error.</summary>
	private async void StartAutoAwake_Click(object sender, RoutedEventArgs args) {
		try {
			Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AutoAwakeCore.exe")) { UseShellExecute = true });
			AutoAwakeNotRunningNotice.IsOpen = false;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or COMException) {
			var resources = new ResourceLoader();
			var dialog = new ContentDialog {
				XamlRoot = ContentRoot.XamlRoot,
				Title = resources.GetString("AutoAwakeStartErrorTitle"),
				Content = resources.GetString("AutoAwakeStartErrorMessage") + "\n\n" + e.Message,
				CloseButtonText = resources.GetString("DialogClose")
			};
			await dialog.ShowAsync();
		}
	}

	/// <summary>Enables the packaged startup task when the user requests it.</summary>
	private async void RegisterStartup_Click(object sender, RoutedEventArgs args) {
		try {
			await UpdateStartupAsync(true);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or Win32Exception or COMException) {
			ShowStartupError(e);
		}
	}

	/// <summary>Disables the packaged startup task when the user requests it.</summary>
	private async void RemoveStartup_Click(object sender, RoutedEventArgs args) {
		try {
			await UpdateStartupAsync(false);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or Win32Exception or COMException) {
			ShowStartupError(e);
		}
	}

	/// <summary>Displays a localized startup-task error while the window is open.</summary>
	private void ShowStartupError(Exception error) {
		if (closed) {
			return;
		}
		var resources = new ResourceLoader();
		StartupNotice.IsOpen = true;
		StartupNotice.Message = resources.GetString("StartupErrorTitle") + "\n" + error.Message;
		StartupStateMessage.Text = StartupNotice.Message;
	}

	/// <summary>Refreshes startup-task state and optionally applies a requested state change.</summary>
	private async Task UpdateStartupAsync(bool? enable) {
		if (startupBusy || closed) {
			return;
		}
		startupBusy = true;
		RegisterStartup.IsEnabled = false;
		RemoveStartup.IsEnabled = false;
		var resources = new ResourceLoader();
		try {
			if (!StartupRegistration.IsPackaged()) {
				StartupStateMessage.Text = resources.GetString("StartupUnavailable");
				StartupNotice.Message = StartupStateMessage.Text;
				StartupNotice.IsOpen = true;
				return;
			}
			if (enable.HasValue) {
				await StartupRegistration.SetEnabledAsync(enable.Value);
			}
			var state = await StartupRegistration.GetStateAsync();
			if (closed) {
				return;
			}
			var key = state switch {
				StartupTaskState.Disabled => "StartupStateDisabled",
				StartupTaskState.DisabledByUser => "StartupStateDisabledByUser",
				StartupTaskState.DisabledByPolicy => "StartupStateDisabledByPolicy",
				StartupTaskState.Enabled => "StartupStateEnabled",
				StartupTaskState.EnabledByPolicy => "StartupStateEnabledByPolicy",
				_ => throw new InvalidOperationException("Unknown startup task state: " + state)
			};
			StartupStateMessage.Text = resources.GetString(key);
			var enabled = state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy;
			StartupNotice.IsOpen = !enabled;
			StartupNotice.Message = resources.GetString(key);
			RegisterStartup.IsEnabled = state == StartupTaskState.Disabled;
			RemoveStartup.IsEnabled = state == StartupTaskState.Enabled;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or Win32Exception or COMException) {
			ShowStartupError(e);
		} finally {
			startupBusy = false;
		}
	}

	/// <summary>Opens the executable picker and adds the selected application rule.</summary>
	private async void AddApplication_Click(object sender, RoutedEventArgs args) {
		try {
			AddApplication.IsEnabled = false;
			try {
				var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (string.IsNullOrEmpty(programFiles)) {
					throw new IOException("The Program Files directory is unavailable.");
				}
				var picker = new FileOpenPicker(AppWindow.Id) {
					SuggestedFolder = programFiles
				};
				picker.FileTypeFilter.Add(".exe");
				var file = await picker.PickSingleFileAsync();
				if (file != null) {
					await AddApplicationFile(file.Path);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException) {
				await ShowApplicationError("ApplicationPickerError", e.Message);
			} finally {
				AddApplication.IsEnabled = true;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception or COMException) {
			await ShowApplicationError("ApplicationPickerError", e.Message);
		}
	}

	/// <summary>Accepts file drops only when application rules can be added.</summary>
	private void ApplicationList_DragOver(object sender, DragEventArgs args) {
		args.AcceptedOperation = AddApplication.IsEnabled && args.DataView.Contains(StandardDataFormats.StorageItems)
			? DataPackageOperation.Copy : DataPackageOperation.None;
		args.Handled = true;
	}

	/// <summary>Validates dropped files and adds executable application rules.</summary>
	private async void ApplicationList_Drop(object sender, DragEventArgs args) {
		try {
			args.Handled = true;
			if (!AddApplication.IsEnabled || !args.DataView.Contains(StandardDataFormats.StorageItems)) {
				return;
			}
			AddApplication.IsEnabled = false;
			var deferral = args.GetDeferral();
			try {
				var items = await args.DataView.GetStorageItemsAsync();
				foreach (var item in items) {
					if (item is StorageFile file && string.Equals(file.FileType, ".exe", StringComparison.OrdinalIgnoreCase)) {
						try {
							await AddApplicationFile(file.Path);
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException) {
							await ShowApplicationError("ApplicationPickerError", e.Message);
						}
					} else {
						await ShowApplicationError("ApplicationDropInvalid", item.Name);
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException) {
				await ShowApplicationError("ApplicationPickerError", e.Message);
			} finally {
				deferral.Complete();
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception or COMException) {
			await ShowApplicationError("ApplicationPickerError", e.Message);
		} finally {
			AddApplication.IsEnabled = true;
		}
	}

	/// <summary>Adds an executable path and reports when it is already configured.</summary>
	private async Task AddApplicationFile(string path) {
		if (!Settings.AddApplication(path)) {
			await ShowApplicationError("DuplicateApplication");
		}
	}

	/// <summary>Displays a localized application-related error dialog.</summary>
	private async Task ShowApplicationError(string key, string? details = null) {
		var resources = new ResourceLoader();
		var dialog = new ContentDialog {
			XamlRoot = ContentRoot.XamlRoot,
			Title = resources.GetString("ApplicationErrorTitle"),
			Content = resources.GetString(key) + (details == null ? "" : "\n\n" + details),
			CloseButtonText = resources.GetString("DialogClose")
		};
		await dialog.ShowAsync();
	}

	/// <summary>Removes the application rule associated with the clicked button.</summary>
	private void DeleteApplication_Click(object sender, RoutedEventArgs args) {
		if (sender is Button { DataContext: ApplicationSettings application }) {
			Settings.Applications.Remove(application);
		}
	}

	/// <summary>Updates the CPU threshold from the slider when initialization is complete.</summary>
	private void CpuThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args) {
		if (initialized) {
			Settings.CpuUsageThreshold = (int)args.NewValue;
		}
	}

	/// <summary>Validates and applies an integer CPU threshold entered in the number box.</summary>
	private void CpuThresholdInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) {
		if (!initialized) {
			return;
		}
		var value = args.NewValue;
		if (!double.IsFinite(value) || value < 0 || value > 100 || value != Math.Truncate(value)) {
			sender.Value = Settings.CpuUsageThreshold;
			return;
		}
		Settings.CpuUsageThreshold = (int)value;
	}

	/// <summary>Maps a sleep-prevention action to its combo-box index.</summary>
	private int ActionIndex(SleepPreventionAction action) => action switch {
		SleepPreventionAction.NoSleep => 0,
		SleepPreventionAction.KeepDisplay => 1,
		SleepPreventionAction.KeepUnlocked => 2,
		_ => throw new InvalidOperationException("Invalid sleep prevention action.")
	};

	/// <summary>Updates the CPU-based action from the selected combo-box item.</summary>
	private void CpuAction_SelectionChanged(object sender, SelectionChangedEventArgs args) {
		if (!initialized) {
			return;
		}
		Settings.CpuUsageAction = CpuAction.SelectedIndex switch {
			0 => SleepPreventionAction.NoSleep,
			1 => SleepPreventionAction.KeepDisplay,
			2 => SleepPreventionAction.KeepUnlocked,
			_ => Settings.CpuUsageAction
		};
	}

	/// <summary>Validates and applies the sleep-prevention interval entered in the number box.</summary>
	private void IntervalInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) {
		if (!initialized) {
			return;
		}
		var value = args.NewValue;
		if (!double.IsFinite(value) || value < 1 || value > 86400 || value != Math.Truncate(value)) {
			sender.Value = Settings.Interval;
			return;
		}
		Settings.Interval = (int)value;
	}
}
