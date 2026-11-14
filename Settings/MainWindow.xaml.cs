using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Kobachi.AutoAwake {
	public sealed partial class MainWindow : Window {
		private readonly SettingsStore settingsStore;
		private readonly DispatcherQueueTimer boundsSaveTimer;
		private bool initialized;
		public AwakeSettings Settings => settingsStore.Settings;

		public MainWindow() {
			settingsStore = SettingsStore.OpenDefault();
			settingsStore.SaveFailed += SettingsError.ShowSaveError;
			InitializeComponent();
			ContentRoot.DataContext = Settings;
			Navigation.SelectedItem = GeneralNavigation;
			GeneralContent.Visibility = Visibility.Visible;
			initialized = true;

			Title = AppTitleBar.Title;
			ExtendsContentIntoTitleBar = true;
			SetTitleBar(AppTitleBar);

			if (AppWindow.Presenter is OverlappedPresenter presenter) {
				presenter.PreferredMinimumWidth = SettingsStore.MinimumWindowWidth;
				presenter.PreferredMinimumHeight = SettingsStore.MinimumWindowHeight;
			}

			if (settingsStore.WindowBounds is { } bounds) {
				var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
				var workArea = displayArea.WorkArea;
				int width = Math.Max(SettingsStore.MinimumWindowWidth, Math.Min(bounds.Width, workArea.Width));
				int height = Math.Max(SettingsStore.MinimumWindowHeight, Math.Min(bounds.Height, workArea.Height));
				int x = Math.Clamp(bounds.X, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
				int y = Math.Clamp(bounds.Y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
				AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
			} else {
				var initialSize = AppWindow.Size;
				AppWindow.Resize(new Windows.Graphics.SizeInt32(
					Math.Max(SettingsStore.MinimumWindowWidth, initialSize.Width / 2),
					Math.Max(SettingsStore.MinimumWindowHeight, initialSize.Height / 2)));
			}

			boundsSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
			boundsSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
			boundsSaveTimer.IsRepeating = false;
			boundsSaveTimer.Tick += (_, _) => SaveWindowBounds();
			AppWindow.Changed += AppWindow_Changed;
			Activated += (_, _) => SaveWindowBounds();
			Closed += (_, _) => {
				SaveWindowBounds();
				boundsSaveTimer.Stop();
				AppWindow.Changed -= AppWindow_Changed;
				settingsStore.Dispose();
			};
			AppWindow.SetIcon("AppIcon.ico");
		}

		private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) {
			if (args.DidPositionChange || args.DidSizeChange) boundsSaveTimer.Start();
		}

		private void SaveWindowBounds() {
			var position = AppWindow.Position;
			var size = AppWindow.Size;
			if (size.Width < SettingsStore.MinimumWindowWidth || size.Height < SettingsStore.MinimumWindowHeight) return;
			settingsStore.SaveWindowBounds(position.X, position.Y, size.Width, size.Height);
		}

		private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
			if (!initialized) return;
			GeneralContent.Visibility = sender.SelectedItem == GeneralNavigation ? Visibility.Visible : Visibility.Collapsed;
			CpuContent.Visibility = sender.SelectedItem == CpuNavigation ? Visibility.Visible : Visibility.Collapsed;
			ApplicationsContent.Visibility = sender.SelectedItem == ApplicationsNavigation ? Visibility.Visible : Visibility.Collapsed;
		}

		private async void AddApplication_Click(object sender, RoutedEventArgs args) {
			AddApplication.IsEnabled = false;
			try {
				string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (string.IsNullOrEmpty(programFiles)) throw new IOException("The Program Files directory is unavailable.");
				var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id) {
					SuggestedFolder = programFiles
				};
				picker.FileTypeFilter.Add(".exe");
				var file = await picker.PickSingleFileAsync();
				if (file != null) await AddApplicationFile(file.Path);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is System.Runtime.InteropServices.COMException) {
				await ShowApplicationError("ApplicationPickerError", e.Message);
			} finally {
				AddApplication.IsEnabled = true;
			}
		}

		private void ApplicationList_DragOver(object sender, DragEventArgs args) {
			args.AcceptedOperation = AddApplication.IsEnabled && args.DataView.Contains(StandardDataFormats.StorageItems)
				? DataPackageOperation.Copy : DataPackageOperation.None;
			args.Handled = true;
		}

		private async void ApplicationList_Drop(object sender, DragEventArgs args) {
			args.Handled = true;
			if (!AddApplication.IsEnabled || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
			AddApplication.IsEnabled = false;
			var deferral = args.GetDeferral();
			try {
				var items = await args.DataView.GetStorageItemsAsync();
				foreach (var item in items) {
					if (item is StorageFile file && string.Equals(file.FileType, ".exe", StringComparison.OrdinalIgnoreCase)) {
						try {
							await AddApplicationFile(file.Path);
						} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is System.Runtime.InteropServices.COMException) {
							await ShowApplicationError("ApplicationPickerError", e.Message);
						}
					} else {
						await ShowApplicationError("ApplicationDropInvalid", item.Name);
					}
				}
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is System.Runtime.InteropServices.COMException) {
				await ShowApplicationError("ApplicationPickerError", e.Message);
			} finally {
				deferral.Complete();
				AddApplication.IsEnabled = true;
			}
		}

		private async System.Threading.Tasks.Task AddApplicationFile(string path) {
			if (!Settings.AddApplication(path)) await ShowApplicationError("DuplicateApplication");
		}

		private async System.Threading.Tasks.Task ShowApplicationError(string key, string? details = null) {
			var resources = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
			var dialog = new ContentDialog {
				XamlRoot = ContentRoot.XamlRoot,
				Title = resources.GetString("ApplicationErrorTitle"),
				Content = resources.GetString(key) + (details == null ? "" : "\n\n" + details),
				CloseButtonText = resources.GetString("DialogClose")
			};
			await dialog.ShowAsync();
		}

		private void DeleteApplication_Click(object sender, RoutedEventArgs args) {
			if (sender is Button { DataContext: ApplicationSettings application }) Settings.Applications.Remove(application);
		}

		private void CpuThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args) {
			if (initialized) Settings.CpuUsageThreshold = (int)args.NewValue;
		}

		private void CpuThresholdInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) {
			if (!initialized) return;
			double value = args.NewValue;
			if (!double.IsFinite(value) || value < 0 || value > 100 || value != Math.Truncate(value)) {
				sender.Value = Settings.CpuUsageThreshold;
				return;
			}
			Settings.CpuUsageThreshold = (int)value;
		}

		private int ActionIndex(SleepPreventionAction action) => action switch {
			SleepPreventionAction.NoSleep => 0,
			SleepPreventionAction.KeepDisplay => 1,
			SleepPreventionAction.KeepUnlocked => 2,
			_ => throw new InvalidOperationException("Invalid sleep prevention action.")
		};

		private void CpuAction_SelectionChanged(object sender, SelectionChangedEventArgs args) {
			if (!initialized) return;
			if (CpuAction.SelectedIndex == 0) Settings.CpuUsageAction = SleepPreventionAction.NoSleep;
			else if (CpuAction.SelectedIndex == 1) Settings.CpuUsageAction = SleepPreventionAction.KeepDisplay;
			else if (CpuAction.SelectedIndex == 2) Settings.CpuUsageAction = SleepPreventionAction.KeepUnlocked;
		}

		private void IntervalInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) {
			if (!initialized) return;
			double value = args.NewValue;
			if (!double.IsFinite(value) || value < 1 || value > 86400 || value != Math.Truncate(value)) {
				sender.Value = Settings.Interval;
				return;
			}
			Settings.Interval = (int)value;
		}
	}
}
