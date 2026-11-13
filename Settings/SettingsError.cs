using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kobachi.AutoAwake {
	internal static class SettingsError {
		public static void ShowLoadError(string details) => Show("SettingsLoadError", details);
		public static void ShowSaveError(string details) => Show("SettingsSaveError", details);
		public static void ShowInstanceError(string details) => Show("SettingsInstanceError", details);

		private static void Show(string key, string details) {
			var resources = new ResourceLoader();
			MessageBox(IntPtr.Zero, resources.GetString(key) + "\n\n" + details, "AutoAwake", 0x10);
		}

		[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
		private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
	}
}
