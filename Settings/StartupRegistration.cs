using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Kobachi.AutoAwake {
	internal static class StartupRegistration {
		private const string TaskId = "AutoAwakeCoreStartup";
		private const int AppModelErrorNoPackage = 15700;
		private const int ErrorInsufficientBuffer = 122;

		public static bool IsPackaged() {
			uint length = 0;
			int result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
			if (result == AppModelErrorNoPackage) return false;
			if (result == ErrorInsufficientBuffer || result == 0) return true;
			throw new System.ComponentModel.Win32Exception(result);
		}

		public static async Task<StartupTaskState> GetStateAsync() => (await StartupTask.GetAsync(TaskId)).State;

		public static async Task SetEnabledAsync(bool enabled) {
			var task = await StartupTask.GetAsync(TaskId);
			if (enabled) await task.RequestEnableAsync();
			else task.Disable();
		}

		[DllImport("kernel32.dll", ExactSpelling = true)]
		private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
	}
}
