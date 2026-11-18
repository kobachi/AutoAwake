using System;
using System.Collections.Generic;
using Windows.System;

namespace Kobachi.AutoAwake.Models;

/// <summary>Stores the optional global shortcuts for selecting baseline modes.</summary>
public sealed partial class HotkeySettings : BindableObject {
	/// <summary>Gets or sets the shortcut for automatic mode, or an empty string.</summary>
	public string Auto { get; set => SetProperty(ref field, value ?? throw new ArgumentNullException(nameof(value))); } = string.Empty;

	/// <summary>Gets or sets the shortcut for preventing sleep, or an empty string.</summary>
	public string NoSleep { get; set => SetProperty(ref field, value ?? throw new ArgumentNullException(nameof(value))); } = string.Empty;

	/// <summary>Gets or sets the shortcut for preventing display off, or an empty string.</summary>
	public string KeepDisplay { get; set => SetProperty(ref field, value ?? throw new ArgumentNullException(nameof(value))); } = string.Empty;

	/// <summary>Gets or sets the shortcut for preventing screen saver and idle lock, or an empty string.</summary>
	public string KeepUnlocked { get; set => SetProperty(ref field, value ?? throw new ArgumentNullException(nameof(value))); } = string.Empty;

	/// <summary>Validates the four shortcuts, including duplicate combinations.</summary>
	public void Validate() {
		var combinations = new HashSet<string>(StringComparer.Ordinal);
		foreach (var value in new[] { Auto, NoSleep, KeepDisplay, KeepUnlocked }) {
			if (value.Length == 0) continue;
			var normalized = Normalize(value);
			if (!combinations.Add(normalized)) throw new FormatException("Shortcut key combinations must be unique.");
		}
	}

	/// <summary>Returns the canonical representation of a valid non-empty shortcut.</summary>
	public static string Normalize(string value) {
		if (value.Length >= 63) throw new FormatException("Shortcut key is too long.");
		var parts = value.Split('+');
		if (parts.Length < 2) throw new FormatException("Shortcut key must contain modifiers and a key.");
		var modifiers = HotkeyModifiers.None;
		foreach (var raw in parts[..^1]) {
			var part = raw.Trim(' ');
			var modifier = part.ToUpperInvariant() switch {
				"CTRL" => HotkeyModifiers.Control,
				"ALT" => HotkeyModifiers.Alt,
				"SHIFT" => HotkeyModifiers.Shift,
				"WIN" => HotkeyModifiers.Windows,
				_ => throw new FormatException("Shortcut key has an invalid modifier.")
			};
			if ((modifiers & modifier) != 0) throw new FormatException("Shortcut key modifiers must not be repeated.");
			modifiers |= modifier;
		}
		var key = parts[^1].Trim(' ');
		if (modifiers == HotkeyModifiers.None || key.Length == 0) throw new FormatException("Shortcut key must contain modifiers and a key.");
		for (var code = 0; code <= 255; code++) {
			if (HotkeyFormatter.TryGetKeyName((VirtualKey)code, out var name) && string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) {
				return HotkeyFormatter.Format(modifiers, name);
			}
		}
		throw new FormatException("Shortcut key has an unsupported key.");
	}
}
