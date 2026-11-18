using System;

namespace Kobachi.AutoAwake.Models;

/// <summary>Specifies the modifier keys of a hotkey.</summary>
[Flags]
public enum HotkeyModifiers {
	/// <summary>No modifier key.</summary>
	None = 0,

	/// <summary>The Ctrl key.</summary>
	Control = 1,

	/// <summary>The Alt key.</summary>
	Alt = 2,

	/// <summary>The Shift key.</summary>
	Shift = 4,

	/// <summary>The Windows logo key.</summary>
	Windows = 8
}
