using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Kobachi.AutoAwake.Models;

namespace Kobachi.AutoAwake.Persistence;

/// <summary>Loads, validates, saves, and monitors changes to the settings file.</summary>
public sealed partial class SettingsStore : IDisposable {
	/// <summary>Defines the minimum supported settings window width in pixels.</summary>
	public const int MinimumWindowWidth = 640;

	/// <summary>Defines the minimum supported settings window height in pixels.</summary>
	public const int MinimumWindowHeight = 480;

	/// <summary>Uses strict UTF-16 little-endian encoding with a byte-order mark.</summary>
	private static readonly Encoding FileEncoding = new UnicodeEncoding(false, true, true);

	/// <summary>Win32 error code for a sharing violation.</summary>
	private const int SharingViolation = 32;

	/// <summary>Win32 error code for a lock violation.</summary>
	private const int LockViolation = 33;

	/// <summary>Maximum number of retries when a settings file is temporarily locked.</summary>
	private const int SettingsReadRetryCount = 100;

	/// <summary>Delay between settings file read retries, in milliseconds.</summary>
	private const int SettingsReadRetryDelayMilliseconds = 50;

	/// <summary>Stores the full path of the settings file.</summary>
	private readonly string path;

	/// <summary>Tracks application rules currently subscribed for property changes.</summary>
	private readonly HashSet<ApplicationSettings> subscribed = [];

	/// <summary>Maps current application rules to their original file section names.</summary>
	private Dictionary<ApplicationSettings, string> savedSections;

	/// <summary>Tracks hotkey properties changed since their last successful save.</summary>
	private readonly HashSet<string> changedHotkeys = new(StringComparer.Ordinal);

	/// <summary>Tracks the most recently reported save error to avoid duplicate notifications.</summary>
	private string? lastError;

	/// <summary>Gets the settings model managed by this store.</summary>
	public AwakeSettings Settings { get; }

	/// <summary>Gets the saved window position and size, if available.</summary>
	public (int X, int Y, int Width, int Height)? WindowBounds { get; private set; }

	/// <summary>Raised when an automatic settings save fails.</summary>
	public event Action<string>? SaveFailed;

	/// <summary>Associates the loaded settings and section names with their backing file.</summary>
	private SettingsStore(string path, AwakeSettings settings, Dictionary<ApplicationSettings, string> sections,
		(int X, int Y, int Width, int Height)? windowBounds) {
		this.path = path;
		Settings = settings;
		WindowBounds = windowBounds;
		savedSections = sections;
		Settings.PropertyChanged += OnPropertyChanged;
		Settings.Hotkeys.PropertyChanged += OnHotkeyChanged;
		Settings.Applications.CollectionChanged += OnCollectionChanged;
		UpdateSubscriptions();
	}

	/// <summary>Opens the default settings file in the user's application data directory.</summary>
	public static SettingsStore OpenDefault() {
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return string.IsNullOrEmpty(appData) ? throw new IOException("The roaming application data directory is unavailable.") : Open(Path.Combine(appData, "AutoAwake", "settings.ini"));
	}

	/// <summary>Loads settings from the specified file and creates a store for them.</summary>
	public static SettingsStore Open(string path) {
		path = Path.GetFullPath(path);
		var directory = Path.GetDirectoryName(path) ?? throw new IOException("The settings directory is unavailable.");
		Directory.CreateDirectory(directory);
		if (!File.Exists(path)) {
			var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
			try {
				using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					using (var writer = new StreamWriter(stream, FileEncoding)) {
						writer.Write("[General]\r\nInterval=30\r\nSuppressWindowsUpdate=1\r\nCpuUsageEnabled=1\r\nCpuUsageThreshold=80\r\nCpuUsageAction=NoSleep\r\n");
					}
				try {
					File.Move(temp, path);
				} catch (IOException) when (File.Exists(path)) {
					// Another process created the settings file after the existence check.
				}
			} finally {
				if (File.Exists(temp)) {
					File.Delete(temp);
				}
			}
		}
		byte[] bytes = ReadSettingsFile(path);
		if (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[0] != 0xff || bytes[1] != 0xfe) {
			throw new FormatException("The settings file must use UTF-16 LE with a BOM.");
		}
		try { FileEncoding.GetString(bytes, 2, bytes.Length - 2); }
		catch (DecoderFallbackException e) { throw new FormatException("The settings file contains invalid UTF-16.", e); }

		var settings = new AwakeSettings {
			SuppressWindowsUpdate = ParseBoolean(ReadValue(path, "General", "SuppressWindowsUpdate", "1"), "SuppressWindowsUpdate"),
			CpuUsageEnabled = ParseBoolean(ReadValue(path, "General", "CpuUsageEnabled", "1"), "CpuUsageEnabled"),
			Interval = ParseNumber(ReadValue(path, "General", "Interval", "30"), 1, 86400, "Interval"),
			CpuUsageThreshold = ParseNumber(ReadValue(path, "General", "CpuUsageThreshold", "80"), 0, 100, "CpuUsageThreshold"),
			CpuUsageAction = ParseAction(ReadValue(path, "General", "CpuUsageAction", "NoSleep"), "CpuUsageAction")
		};
		ReadHotkeys(path, settings.Hotkeys);
		settings.Hotkeys.Validate();
		var sections = new Dictionary<ApplicationSettings, string>();
		var allSections = ReadSections(path, checked(bytes.Length / 2 + 2));
		(int X, int Y, int Width, int Height)? windowBounds = null;
		foreach (var section in allSections) {
			if (string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase)) {
				windowBounds = ReadWindowBounds(path);
				continue;
			}
			if (string.Equals(section, "General", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			var rule = new ApplicationSettings {
				SectionName = section,
				Path = ReadValue(path, section, "Path", "", 32768),
				Action = ParseAction(ReadValue(path, section, "Action", ""), section + "/Action"),
				VisibleWindowOnly = ParseBoolean(ReadValue(path, section, "VisibleWindowOnly", "0"), section + "/VisibleWindowOnly")
			};
			settings.Applications.Add(rule);
			sections.Add(rule, section);
		}
		Validate(settings);
		return new SettingsStore(path, settings, sections, windowBounds);
	}

	/// <summary>Reads the baseline hotkeys from the General section.</summary>
	private static void ReadHotkeys(string file, HotkeySettings hotkeys) {
		hotkeys.Auto = ReadValue(file, "General", "HotkeyAuto", "", 64);
		hotkeys.NoSleep = ReadValue(file, "General", "HotkeyNoSleep", "", 64);
		hotkeys.KeepDisplay = ReadValue(file, "General", "HotkeyKeepDisplay", "", 64);
		hotkeys.KeepUnlocked = ReadValue(file, "General", "HotkeyKeepUnlocked", "", 64);
	}

	/// <summary>Reads the settings file, retrying transient sharing violations for up to five seconds.</summary>
	private static byte[] ReadSettingsFile(string file) {
		for (var attempt = 0; ; attempt++) {
			try {
				return File.ReadAllBytes(file);
			} catch (IOException e) when (attempt < SettingsReadRetryCount && IsSharingViolation(e)) {
				Thread.Sleep(SettingsReadRetryDelayMilliseconds);
			}
		}
	}

	/// <summary>Returns whether an I/O exception represents a Windows sharing or lock violation.</summary>
	private static bool IsSharingViolation(IOException error) {
		var win32Error = (int)((uint)error.HResult & 0xffff);
		return win32Error is SharingViolation or LockViolation;
	}

	/// <summary>Reads the window position and size from the settings file.</summary>
	private static (int X, int Y, int Width, int Height) ReadWindowBounds(string file) {
		int x = ParseSignedNumber(ReadValue(file, "Settings", "WindowX", "#missing#"), "Settings/WindowX");
		int y = ParseSignedNumber(ReadValue(file, "Settings", "WindowY", "#missing#"), "Settings/WindowY");
		int width = ParseNumber(ReadValue(file, "Settings", "WindowWidth", "#missing#"), 1, 32768, "Settings/WindowWidth");
		int height = ParseNumber(ReadValue(file, "Settings", "WindowHeight", "#missing#"), 1, 32768, "Settings/WindowHeight");
		return (x, y, Math.Max(width, MinimumWindowWidth), Math.Max(height, MinimumWindowHeight));
	}

	/// <summary>Parses a signed integer or throws a format exception for invalid input.</summary>
	private static int ParseSignedNumber(string value, string name) {
		if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)) {
			throw new FormatException(name + " must be a signed 32-bit integer.");
		}
		return number;
	}

	/// <summary>Converts an INI 0/1 value to a Boolean.</summary>
	private static bool ParseBoolean(string value, string name) => value switch {
		"0" => false,
		"1" => true,
		_ => throw new FormatException(name + " must be 0 or 1.")
	};

	/// <summary>Parses a decimal integer within the specified range.</summary>
	private static int ParseNumber(string value, int min, int max, string name) {
		if (value.Length is 0 or >= 15 || value.Any(c => c is < '0' or > '9')) {
			throw new FormatException(name + " is invalid.");
		}
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max) {
			throw new FormatException(name + " is out of range.");
		}
		return number;
	}

	/// <summary>Converts a settings-file value to a sleep-prevention action.</summary>
	private static SleepPreventionAction ParseAction(string value, string name) {
		if (string.Equals(value, "NoSleep", StringComparison.OrdinalIgnoreCase)) {
			return SleepPreventionAction.NoSleep;
		}
		if (string.Equals(value, "KeepDisplay", StringComparison.OrdinalIgnoreCase)) {
			return SleepPreventionAction.KeepDisplay;
		}
		if (string.Equals(value, "KeepUnlocked", StringComparison.OrdinalIgnoreCase)) {
			return SleepPreventionAction.KeepUnlocked;
		}
		throw new FormatException(name + " must be NoSleep, KeepDisplay or KeepUnlocked.");
	}

	/// <summary>Validates the settings model and all application rules.</summary>
	private static void Validate(AwakeSettings settings) {
		if (settings.Interval < 1 || settings.Interval > 86400) {
			throw new FormatException("Interval must be between 1 and 86400.");
		}
		if (settings.CpuUsageThreshold < 0 || settings.CpuUsageThreshold > 100) {
			throw new FormatException("CpuUsageThreshold must be between 0 and 100.");
		}
		ValidateAction(settings.CpuUsageAction);
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "General" };
		foreach (var rule in settings.Applications) {
			if (string.IsNullOrWhiteSpace(rule.SectionName) || rule.SectionName != rule.SectionName.Trim() || rule.SectionName.IndexOfAny(['[', ']', '\r', '\n', '\0']) >= 0 || !names.Add(rule.SectionName)) {
				throw new FormatException("Application section names must be unique and cannot be General or contain brackets or line breaks.");
			}
			var p = rule.Path;
			if (string.IsNullOrEmpty(p) || p.Length >= 32767 || p != p.Trim() || p.IndexOfAny(['\r', '\n', '\0', '"']) >= 0 || p.EndsWith('\\') ||
				!((p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\') || p is ['\\', '\\', _, ..])) {
				throw new FormatException("Each application Path must be an absolute executable path.");
			}
			try {
				if (Path.GetFullPath(p).Length >= 32768) {
					throw new FormatException("Application Path is too long.");
				}
			} catch (ArgumentException e) {
				throw new FormatException("Each application Path must be an absolute executable path.", e);
			}
			ValidateAction(rule.Action);
		}
	}

	/// <summary>Validates that the sleep-prevention action is defined.</summary>
	private static void ValidateAction(SleepPreventionAction action) {
		if (action != SleepPreventionAction.NoSleep && action != SleepPreventionAction.KeepDisplay && action != SleepPreventionAction.KeepUnlocked) {
			throw new FormatException("Invalid sleep prevention action.");
		}
	}

	/// <summary>Saves settings when a monitored property changes.</summary>
	private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
		if (sender is ApplicationSettings && e.PropertyName == nameof(ApplicationSettings.ActionIndex)) {
			return;
		}
		TrySave();
	}

	/// <summary>Tracks and saves an edited hotkey.</summary>
	private void OnHotkeyChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName is null) throw new InvalidOperationException("A hotkey property name is required.");
		changedHotkeys.Add(e.PropertyName);
		TrySave();
	}

	/// <summary>Updates subscriptions and saves when an application rule is added or removed.</summary>
	private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
		UpdateSubscriptions();
		TrySave();
	}

	/// <summary>Synchronizes property-change subscriptions with the current application rules.</summary>
	private void UpdateSubscriptions() {
		var current = new HashSet<ApplicationSettings>(Settings.Applications);
		foreach (var rule in subscribed.Where(rule => !current.Contains(rule))) rule.PropertyChanged -= OnPropertyChanged;
		foreach (var rule in current.Where(rule => !subscribed.Contains(rule))) rule.PropertyChanged += OnPropertyChanged;
		subscribed.Clear();
		subscribed.UnionWith(current);
	}

	/// <summary>Attempts to save settings and notifies listeners if the save fails.</summary>
	private void TrySave() {
		try {
			Save();
			lastError = null;
		} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is ArgumentException) {
			if (lastError != e.Message) {
				lastError = e.Message;
				SaveFailed?.Invoke(e.Message);
			}
		}
	}

	/// <summary>Updates the window position and size, then saves them to the settings file.</summary>
	public void SaveWindowBounds(int x, int y, int width, int height) {
		if (width < MinimumWindowWidth || width > 32768) {
			throw new ArgumentOutOfRangeException(nameof(width), @"Window width must be between 640 and 32768 pixels.");
		}

		if (height < MinimumWindowHeight || height > 32768) {
			throw new ArgumentOutOfRangeException(nameof(height), @"Window height must be between 480 and 32768 pixels.");
		}

		if (WindowBounds == (x, y, width, height)) {
			return;
		}
		WindowBounds = (x, y, width, height);
		TrySave();
	}

	/// <summary>Validates settings and applies them to the settings file through a temporary file.</summary>
	public void Save() {
		Validate(Settings);
		if (WindowBounds is { } bounds && (bounds.Width < MinimumWindowWidth || bounds.Width > 32768 || bounds.Height < MinimumWindowHeight || bounds.Height > 32768)) {
			throw new FormatException("Settings window dimensions must be at least 640 by 480 and at most 32768 pixels.");
		}
		var directory = Path.GetDirectoryName(path)!;
		var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
		try {
			File.Copy(path, temp, false);
			if (changedHotkeys.Count != 0) {
				SaveHotkeys(temp);
			}
			var existing = new Dictionary<ApplicationSettings, string>();
			foreach (var pair in savedSections.Where(pair => !Settings.Applications.Contains(pair.Key) || !string.Equals(pair.Value, pair.Key.SectionName, StringComparison.Ordinal))) {
				existing.Add(pair.Key, ReadSection(temp, pair.Value));
				WriteSection(temp, pair.Value, null);
			}
			WriteValue(temp, "General", "Interval", Settings.Interval.ToString(CultureInfo.InvariantCulture));
			WriteValue(temp, "General", "SuppressWindowsUpdate", Settings.SuppressWindowsUpdate ? "1" : "0");
			WriteValue(temp, "General", "CpuUsageEnabled", Settings.CpuUsageEnabled ? "1" : "0");
			WriteValue(temp, "General", "CpuUsageThreshold", Settings.CpuUsageThreshold.ToString(CultureInfo.InvariantCulture));
			WriteValue(temp, "General", "CpuUsageAction", Settings.CpuUsageAction.ToString());
			if (WindowBounds is { } savedBounds) {
				WriteValue(temp, "Settings", "WindowX", savedBounds.X.ToString(CultureInfo.InvariantCulture));
				WriteValue(temp, "Settings", "WindowY", savedBounds.Y.ToString(CultureInfo.InvariantCulture));
				WriteValue(temp, "Settings", "WindowWidth", savedBounds.Width.ToString(CultureInfo.InvariantCulture));
				WriteValue(temp, "Settings", "WindowHeight", savedBounds.Height.ToString(CultureInfo.InvariantCulture));
			}
			var updated = new Dictionary<ApplicationSettings, string>();
			foreach (var rule in Settings.Applications) {
				if (existing.TryGetValue(rule, out string? contents)) {
					WriteSection(temp, rule.SectionName, contents);
				}
				WriteValue(temp, rule.SectionName, "Path", rule.Path);
				WriteValue(temp, rule.SectionName, "Action", rule.Action.ToString());
				WriteValue(temp, rule.SectionName, "VisibleWindowOnly", rule.VisibleWindowOnly ? "1" : "0");
				updated.Add(rule, rule.SectionName);
			}
			File.Move(temp, path, true);
			savedSections = updated;
			changedHotkeys.Clear();
		} finally {
			if (File.Exists(temp)) {
				File.Delete(temp);
			}
		}
	}

	/// <summary>Validates edited shortcuts against the latest file and writes only changed keys.</summary>
	private void SaveHotkeys(string file) {
		var current = new HotkeySettings();
		ReadHotkeys(file, current);
		if (changedHotkeys.Contains(nameof(HotkeySettings.Auto))) current.Auto = Settings.Hotkeys.Auto;
		if (changedHotkeys.Contains(nameof(HotkeySettings.NoSleep))) current.NoSleep = Settings.Hotkeys.NoSleep;
		if (changedHotkeys.Contains(nameof(HotkeySettings.KeepDisplay))) current.KeepDisplay = Settings.Hotkeys.KeepDisplay;
		if (changedHotkeys.Contains(nameof(HotkeySettings.KeepUnlocked))) current.KeepUnlocked = Settings.Hotkeys.KeepUnlocked;
		current.Validate();
		if (changedHotkeys.Contains(nameof(HotkeySettings.Auto))) WriteValue(file, "General", "HotkeyAuto", current.Auto);
		if (changedHotkeys.Contains(nameof(HotkeySettings.NoSleep))) WriteValue(file, "General", "HotkeyNoSleep", current.NoSleep);
		if (changedHotkeys.Contains(nameof(HotkeySettings.KeepDisplay))) WriteValue(file, "General", "HotkeyKeepDisplay", current.KeepDisplay);
		if (changedHotkeys.Contains(nameof(HotkeySettings.KeepUnlocked))) WriteValue(file, "General", "HotkeyKeepUnlocked", current.KeepUnlocked);
	}

	/// <summary>Reads an INI key value and checks its length limit.</summary>
	private static string ReadValue(string file, string section, string key, string fallback, int capacity = 16) {
		var value = new StringBuilder(capacity);
		var length = GetPrivateProfileString(section, key, fallback, value, (uint)capacity, file);
		return length >= capacity - 1 ? throw new FormatException(section + "/" + key + " is too long.") : value.ToString();
	}

	/// <summary>Enumerates the section names in an INI file.</summary>
	private static IEnumerable<string> ReadSections(string file, int capacity) {
		var buffer = new char[capacity];
		var length = GetPrivateProfileSectionNames(buffer, (uint)buffer.Length, file);
		return length >= buffer.Length - 2 ? throw new IOException("Could not read settings sections.") : new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>Reads a section and appends the terminator required by the INI API.</summary>
	private static string ReadSection(string file, string section) {
		var capacity = checked((int)(new FileInfo(file).Length / 2 + 2));
		var buffer = new char[capacity];
		var length = GetPrivateProfileSection(section, buffer, (uint)buffer.Length, file);
		if (length >= buffer.Length - 2) {
			throw new IOException("Could not read application settings.");
		}
		return new string(buffer, 0, (int)length) + "\0\0";
	}

	/// <summary>Writes a value to the specified INI key.</summary>
	private static void WriteValue(string file, string section, string key, string value) {
		if (!WritePrivateProfileString(section, key, value, file)) {
			throw new IOException("Could not write " + section + "/" + key + ".");
		}
	}

	/// <summary>Creates, updates, or deletes an INI section.</summary>
	private static void WriteSection(string file, string section, string? value) {
		if (!WritePrivateProfileSection(section, value, file)) {
			throw new IOException("Could not write application section " + section + ".");
		}
	}

	/// <summary>Unsubscribes from settings change events.</summary>
	public void Dispose() {
		Settings.PropertyChanged -= OnPropertyChanged;
		Settings.Hotkeys.PropertyChanged -= OnHotkeyChanged;
		Settings.Applications.CollectionChanged -= OnCollectionChanged;
		foreach (var rule in subscribed) rule.PropertyChanged -= OnPropertyChanged;
		subscribed.Clear();
	}

	/// <summary>Reads a single INI value through the Win32 profile API.</summary>
	[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", CharSet = CharSet.Unicode)]
	private static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, uint size, string file);

	/// <summary>Reads INI section names through the Win32 profile API.</summary>
	[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileSectionNamesW", CharSet = CharSet.Unicode)]
	private static extern uint GetPrivateProfileSectionNames([Out] char[] buffer, uint size, string file);

	/// <summary>Reads an INI section through the Win32 profile API.</summary>
	[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileSectionW", CharSet = CharSet.Unicode)]
	private static extern uint GetPrivateProfileSection(string section, [Out] char[] buffer, uint size, string file);

	/// <summary>Writes a single INI value through the Win32 profile API.</summary>
	[DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool WritePrivateProfileString(string? section, string? key, string? value, string file);

	/// <summary>Writes an INI section through the Win32 profile API.</summary>
	[DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileSectionW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool WritePrivateProfileSection(string section, string? value, string file);
}
