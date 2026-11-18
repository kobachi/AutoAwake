using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kobachi.AutoAwake;

/// <summary>Displays settings errors in localized dialogs.</summary>
internal static class SettingsError {
	/// <summary>Displays a settings load error.</summary>
	public static void ShowLoadError(string details) => Show("SettingsLoadError", details);

	/// <summary>Displays a settings save error.</summary>
	public static void ShowSaveError(string details) => Show("SettingsSaveError", details);

	/// <summary>Displays an error related to another running application instance.</summary>
	public static void ShowInstanceError(string details) => Show("SettingsInstanceError", details);

	/// <summary>Displays an error dialog using the specified resource key and details.</summary>
	private static void Show(string key, string details) {
		var resources = new ResourceLoader();
		MessageBox(IntPtr.Zero, resources.GetString(key) + "\n\n" + details, "AutoAwake", 0x10);
	}

	/// <summary>Displays a Windows message box through the Win32 API.</summary>
	[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
	private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
