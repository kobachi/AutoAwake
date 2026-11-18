using System.Collections.Generic;
using Windows.System;

namespace Kobachi.AutoAwake.Models;

/// <summary>Converts keys to the hotkey text format understood by AutoAwake Core, such as "Ctrl+Alt+F9".</summary>
public static class HotkeyFormatter {
	private static readonly Dictionary<VirtualKey, string> NamedKeys = new() {
		[VirtualKey.Space] = "Space",
		[VirtualKey.Enter] = "Enter",
		[VirtualKey.Tab] = "Tab",
		[VirtualKey.Escape] = "Esc",
		[VirtualKey.Up] = "Up",
		[VirtualKey.Down] = "Down",
		[VirtualKey.Left] = "Left",
		[VirtualKey.Right] = "Right",
		[VirtualKey.Home] = "Home",
		[VirtualKey.End] = "End",
		[VirtualKey.PageUp] = "PageUp",
		[VirtualKey.PageDown] = "PageDown",
		[VirtualKey.Insert] = "Insert",
		[VirtualKey.Delete] = "Delete"
	};

	/// <summary>Gets the name of a key that AutoAwake Core accepts in a hotkey.</summary>
	/// <param name="key">The key to convert.</param>
	/// <param name="name">Receives the key name on success.</param>
	/// <returns>true if the key can be used in a hotkey; otherwise, false.</returns>
	public static bool TryGetKeyName(VirtualKey key, out string name) {
		var code = (int)key;
		switch (code) {
			case >= (int)VirtualKey.A and <= (int)VirtualKey.Z:
			case >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9:
				name = ((char)code).ToString();
				return true;
			case >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24:
				name = "F" + (code - (int)VirtualKey.F1 + 1);
				return true;
			case >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9:
				name = "Num" + (code - (int)VirtualKey.NumberPad0);
				return true;
			default:
				return NamedKeys.TryGetValue(key, out name!);
		}
	}

	/// <summary>Formats modifiers and a key name as hotkey text.</summary>
	/// <param name="modifiers">The modifier keys.</param>
	/// <param name="keyName">The key name returned by <see cref="TryGetKeyName"/>.</param>
	public static string Format(HotkeyModifiers modifiers, string keyName) {
		var text = FormatModifiers(modifiers);
		return text.Length == 0 ? keyName : text + "+" + keyName;
	}

	/// <summary>Formats modifier keys as hotkey text without a trailing separator.</summary>
	/// <param name="modifiers">The modifier keys.</param>
	public static string FormatModifiers(HotkeyModifiers modifiers) {
		var parts = new List<string>(4);
		if (modifiers.HasFlag(HotkeyModifiers.Control)) {
			parts.Add("Ctrl");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Alt)) {
			parts.Add("Alt");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Shift)) {
			parts.Add("Shift");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Windows)) {
			parts.Add("Win");
		}
		return string.Join("+", parts);
	}
}
