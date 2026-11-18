using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Kobachi.AutoAwake.Models;
using Windows.System;

namespace Kobachi.AutoAwake.Persistence;

/// <summary>Checks duplicate assignments and tests whether Windows can register a shortcut.</summary>
internal static class ShortcutKeyAvailability {
	private const int ProbeId = 0x7f01;
	private const uint NoRepeat = 0x4000;

	/// <summary>Returns whether the shortcut is unique and can be registered at this moment.</summary>
	public static bool IsAvailable(HotkeyModifiers modifiers, VirtualKey key, params string[] otherAssignments) {
		if (!HotkeyFormatter.TryGetKeyName(key, out var name)) {
			return false;
		}
		var candidate = HotkeyFormatter.Format(modifiers, name);
		if (otherAssignments.Any(other => other.Length != 0 && string.Equals(HotkeySettings.Normalize(other), candidate, StringComparison.Ordinal))) {
			return false;
		}
		var flags = NoRepeat;
		if ((modifiers & HotkeyModifiers.Alt) != 0) flags |= 0x0001;
		if ((modifiers & HotkeyModifiers.Control) != 0) flags |= 0x0002;
		if ((modifiers & HotkeyModifiers.Shift) != 0) flags |= 0x0004;
		if ((modifiers & HotkeyModifiers.Windows) != 0) flags |= 0x0008;
		if (!RegisterHotKey(IntPtr.Zero, ProbeId, flags, (uint)key)) {
			return false;
		}
		return !UnregisterHotKey(IntPtr.Zero, ProbeId) ? throw new Win32Exception(Marshal.GetLastWin32Error()) : true;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(IntPtr window, int id);
}
