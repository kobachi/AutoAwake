using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Kobachi.AutoAwake.Persistence;

/// <summary>Retrieves and changes the state of the packaged startup task.</summary>
internal static class StartupRegistration {
	/// <summary>Identifies the startup task declared by the package.</summary>
	private const string TaskId = "AutoAwakeCoreStartup";

	/// <summary>Win32 result indicating that the current process has no package identity.</summary>
	private const int AppModelErrorNoPackage = 15700;

	/// <summary>Win32 result indicating that a buffer is required to retrieve the package name.</summary>
	private const int ErrorInsufficientBuffer = 122;

	/// <summary>Returns whether the current process is running as an MSIX package.</summary>
	public static bool IsPackaged() {
		uint length = 0;
		var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
		return result switch {
			AppModelErrorNoPackage => false,
			ErrorInsufficientBuffer or 0 => true,
			_ => throw new Win32Exception(result)
		};
	}

	/// <summary>Asynchronously retrieves the current startup task state.</summary>
	public static async Task<StartupTaskState> GetStateAsync() => (await StartupTask.GetAsync(TaskId)).State;

	/// <summary>Enables or disables the startup task.</summary>
	public static async Task SetEnabledAsync(bool enabled) {
		var task = await StartupTask.GetAsync(TaskId);
		if (enabled) {
			await task.RequestEnableAsync();
		} else {
			task.Disable();
		}
	}

	/// <summary>Retrieves the current package identity through the Win32 API.</summary>
	[DllImport("kernel32.dll", ExactSpelling = true)]
	private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
