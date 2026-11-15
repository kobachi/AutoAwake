using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kobachi.AutoAwake {
	/// <summary>
	/// Manages the fixed-name AutoAwake.lnk in the current user's Startup folder.
	/// Registration is determined by file existence only; the shortcut target is not inspected.
	/// </summary>
	internal static class StartupRegistration {
		/// <summary>Returns the current user's Startup folder, or throws if it is unavailable.</summary>
		private static string StartupDirectory() {
			string directory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
			if (string.IsNullOrEmpty(directory)) throw new IOException("The user startup directory is unavailable.");
			return directory;
		}

		/// <summary>Checks whether the named shortcut file exists in the user's Startup folder.</summary>
		public static bool IsRegistered() => HasShortcut(StartupDirectory());

		/// <summary>Registers the AutoAwakeCore.exe located alongside the settings application.</summary>
		/// <remarks>An existing file is not overwritten. File and COM errors are propagated to the caller.</remarks>
		public static void Register() => Register(StartupDirectory(), Path.Combine(AppContext.BaseDirectory, "AutoAwakeCore.exe"));

		/// <summary>Creates a shortcut to an existing executable in the specified directory.</summary>
		/// <param name="directory">Directory in which to create AutoAwake.lnk.</param>
		/// <param name="target">Path to the executable; its directory becomes the shortcut's working directory.</param>
		/// <remarks>
		/// The link is first saved under a unique temporary name and then moved without overwriting
		/// an existing destination. Any remaining temporary file is deleted when possible.
		/// </remarks>
		internal static void Register(string directory, string target) {
			target = Path.GetFullPath(target);
			if ((File.GetAttributes(target) & FileAttributes.Directory) != 0) throw new IOException("The AutoAwake executable is not a file.");
			Directory.CreateDirectory(directory);
			string destination = Path.Combine(directory, "AutoAwake.lnk");
			string temp = Path.Combine(directory, ".AutoAwake-" + Guid.NewGuid().ToString("N") + ".lnk");
			try {
				CreateShortcut(temp, target);
				File.Move(temp, destination, false);
			} finally {
				if (File.Exists(temp)) File.Delete(temp);
			}
		}

		/// <summary>Deletes AutoAwake.lnk from the user's Startup folder, if present.</summary>
		public static void Remove() => Remove(StartupDirectory());

		/// <summary>Deletes AutoAwake.lnk from the specified directory, if present.</summary>
		/// <param name="directory">Directory containing the shortcut.</param>
		internal static void Remove(string directory) => File.Delete(Path.Combine(directory, "AutoAwake.lnk"));

		/// <summary>Checks for a non-directory entry named AutoAwake.lnk without inspecting its target.</summary>
		/// <param name="directory">Directory to check; a missing directory is treated as unregistered.</param>
		/// <returns>True if the named file exists; otherwise, false.</returns>
		internal static bool HasShortcut(string directory) {
			try {
				return (File.GetAttributes(Path.Combine(directory, "AutoAwake.lnk")) & FileAttributes.Directory) == 0;
			} catch (FileNotFoundException) {
				return false;
			} catch (DirectoryNotFoundException) {
				return false;
			}
		}

		/// <summary>Creates and saves a Windows Shell link using native COM interfaces.</summary>
		/// <remarks>
		/// The calling thread must have initialized COM. Native vtable calls avoid reliance on
		/// built-in COM interop, which is disabled by default in trimmed release builds.
		/// Each acquired interface pointer is released even when an operation fails.
		/// </remarks>
		private static void CreateShortcut(string path, string target) {
			Guid clsid = new("00021401-0000-0000-C000-000000000046");
			Guid shellLinkId = new("000214F9-0000-0000-C000-000000000046");
			Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref shellLinkId, out IntPtr link));
			try {
				// IShellLinkW slots 20 and 9 are SetPath and SetWorkingDirectory.
				Marshal.ThrowExceptionForHR(GetMethod<SetString>(link, 20)(link, target));
				Marshal.ThrowExceptionForHR(GetMethod<SetString>(link, 9)(link, Path.GetDirectoryName(target)!));
				Guid persistFileId = new("0000010B-0000-0000-C000-000000000046");
				Marshal.ThrowExceptionForHR(Marshal.QueryInterface(link, in persistFileId, out IntPtr persistFile));
				try {
					// IPersistFile slot 6 is Save.
					Marshal.ThrowExceptionForHR(GetMethod<SaveFile>(persistFile, 6)(persistFile, path, true));
				} finally {
					Marshal.Release(persistFile);
				}
			} finally {
				Marshal.Release(link);
			}
		}

		private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate {
			IntPtr vtable = Marshal.ReadIntPtr(instance);
			return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
		}

		[DllImport("ole32.dll", ExactSpelling = true)]
		private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

		[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
		private delegate int SetString(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string value);

		[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
		private delegate int SaveFile(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool remember);
	}
}
