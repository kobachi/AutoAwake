namespace Kobachi.AutoAwake.Models;

/// <summary>Specifies the action used to prevent sleep.</summary>
public enum SleepPreventionAction {
	/// <summary>Prevents the system from entering sleep.</summary>
	NoSleep,

	/// <summary>Prevents sleep and keeps the display on.</summary>
	KeepDisplay,

	/// <summary>Prevents the screen saver and idle lock from activating.</summary>
	KeepUnlocked
}
