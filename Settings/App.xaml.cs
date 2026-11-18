using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Kobachi.AutoAwake;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App {
	/// <summary>Holds the main window for the lifetime of the application.</summary>
	private Window? window;

	/// <summary>Prevents multiple settings application instances from running concurrently.</summary>
	private Mutex? instanceMutex;

	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App() {
		InitializeComponent();
	}

	/// <summary>
	/// Invoked when the application is launched.
	/// </summary>
	/// <param name="args">Details about the launch request and process.</param>
	protected override void OnLaunched(LaunchActivatedEventArgs args) {
		bool created;
		try {
			instanceMutex = new Mutex(false, @"Local\Kobachi.AutoAwake.Settings.Singleton", out created);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or ArgumentException) {
			SettingsError.ShowInstanceError(e.Message);
			Exit();
			return;
		}
		if (!created) {
			instanceMutex.Dispose();
			instanceMutex = null;
			Exit();
			return;
		}
		try {
			window = new MainWindow();
			window.Activate();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) {
			instanceMutex?.Dispose();
			instanceMutex = null;
			SettingsError.ShowLoadError(e.Message);
			Exit();
		}
	}
}
