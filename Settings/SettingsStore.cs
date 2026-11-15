using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Kobachi.AutoAwake {
	public sealed class SettingsStore : IDisposable {
		public const int MinimumWindowWidth = 640;
		public const int MinimumWindowHeight = 480;
		private static readonly Encoding FileEncoding = new UnicodeEncoding(false, true, true);
		private readonly string path;
		private readonly HashSet<ApplicationSettings> subscribed = new();
		private Dictionary<ApplicationSettings, string> savedSections = new();
		private string? lastError;

		public AwakeSettings Settings { get; }
		public (int X, int Y, int Width, int Height)? WindowBounds { get; private set; }
		public event Action<string>? SaveFailed;

		private SettingsStore(string path, AwakeSettings settings, Dictionary<ApplicationSettings, string> sections,
			(int X, int Y, int Width, int Height)? windowBounds) {
			this.path = path;
			Settings = settings;
			WindowBounds = windowBounds;
			savedSections = sections;
			Settings.PropertyChanged += OnPropertyChanged;
			Settings.Applications.CollectionChanged += OnCollectionChanged;
			UpdateSubscriptions();
		}

		public static SettingsStore OpenDefault() {
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrEmpty(appData)) throw new IOException("The roaming application data directory is unavailable.");
			return Open(Path.Combine(appData, "AutoAwake", "settings.ini"));
		}

		public static SettingsStore Open(string path) {
			path = Path.GetFullPath(path);
			string directory = Path.GetDirectoryName(path) ?? throw new IOException("The settings directory is unavailable.");
			Directory.CreateDirectory(directory);
			if (!File.Exists(path)) {
				string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
				try {
					using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					using (var writer = new StreamWriter(stream, FileEncoding)) {
						writer.Write("[General]\r\nInterval=30\r\nSuppressWindowsUpdate=1\r\nCpuUsageEnabled=1\r\nCpuUsageThreshold=80\r\nCpuUsageAction=NoSleep\r\n");
					}
					File.Move(temp, path);
				} finally {
					if (File.Exists(temp)) File.Delete(temp);
				}
			}
			byte[] bytes = File.ReadAllBytes(path);
			if (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[0] != 0xff || bytes[1] != 0xfe)
				throw new FormatException("The settings file must use UTF-16 LE with a BOM.");
			try { FileEncoding.GetString(bytes, 2, bytes.Length - 2); }
			catch (DecoderFallbackException e) { throw new FormatException("The settings file contains invalid UTF-16.", e); }

			var settings = new AwakeSettings();
			settings.SuppressWindowsUpdate = ParseBoolean(ReadValue(path, "General", "SuppressWindowsUpdate", "1"), "SuppressWindowsUpdate");
			settings.CpuUsageEnabled = ParseBoolean(ReadValue(path, "General", "CpuUsageEnabled", "1"), "CpuUsageEnabled");
			settings.Interval = ParseNumber(ReadValue(path, "General", "Interval", "30"), 1, 86400, "Interval");
			settings.CpuUsageThreshold = ParseNumber(ReadValue(path, "General", "CpuUsageThreshold", "80"), 0, 100, "CpuUsageThreshold");
			settings.CpuUsageAction = ParseAction(ReadValue(path, "General", "CpuUsageAction", "NoSleep"), "CpuUsageAction");
			var sections = new Dictionary<ApplicationSettings, string>();
			var allSections = ReadSections(path, checked(bytes.Length / 2 + 2));
			(int X, int Y, int Width, int Height)? windowBounds = null;
			foreach (string section in allSections) {
				if (string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase)) {
					windowBounds = ReadWindowBounds(path);
					continue;
				}
				if (string.Equals(section, "General", StringComparison.OrdinalIgnoreCase)) continue;
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

		private static (int X, int Y, int Width, int Height) ReadWindowBounds(string file) {
			int x = ParseSignedNumber(ReadValue(file, "Settings", "WindowX", "#missing#"), "Settings/WindowX");
			int y = ParseSignedNumber(ReadValue(file, "Settings", "WindowY", "#missing#"), "Settings/WindowY");
			int width = ParseNumber(ReadValue(file, "Settings", "WindowWidth", "#missing#"), 1, 32768, "Settings/WindowWidth");
			int height = ParseNumber(ReadValue(file, "Settings", "WindowHeight", "#missing#"), 1, 32768, "Settings/WindowHeight");
			return (x, y, Math.Max(width, MinimumWindowWidth), Math.Max(height, MinimumWindowHeight));
		}

		private static int ParseSignedNumber(string value, string name) {
			if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
				throw new FormatException(name + " must be a signed 32-bit integer.");
			return number;
		}

		private static bool ParseBoolean(string value, string name) => value switch {
			"0" => false, "1" => true, _ => throw new FormatException(name + " must be 0 or 1.")
		};

		private static int ParseNumber(string value, int min, int max, string name) {
			if (value.Length == 0 || value.Length >= 15) throw new FormatException(name + " is invalid.");
			foreach (char c in value) if (c < '0' || c > '9') throw new FormatException(name + " is invalid.");
			if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < min || number > max)
				throw new FormatException(name + " is out of range.");
			return number;
		}

		private static SleepPreventionAction ParseAction(string value, string name) {
			if (string.Equals(value, "NoSleep", StringComparison.OrdinalIgnoreCase)) return SleepPreventionAction.NoSleep;
			if (string.Equals(value, "KeepDisplay", StringComparison.OrdinalIgnoreCase)) return SleepPreventionAction.KeepDisplay;
			if (string.Equals(value, "KeepUnlocked", StringComparison.OrdinalIgnoreCase)) return SleepPreventionAction.KeepUnlocked;
			throw new FormatException(name + " must be NoSleep, KeepDisplay or KeepUnlocked.");
		}

		private static void Validate(AwakeSettings settings) {
			if (settings.Interval < 1 || settings.Interval > 86400) throw new FormatException("Interval must be between 1 and 86400.");
			if (settings.CpuUsageThreshold < 0 || settings.CpuUsageThreshold > 100) throw new FormatException("CpuUsageThreshold must be between 0 and 100.");
			ValidateAction(settings.CpuUsageAction);
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "General" };
			foreach (var rule in settings.Applications) {
				if (string.IsNullOrWhiteSpace(rule.SectionName) || rule.SectionName != rule.SectionName.Trim() || rule.SectionName.IndexOfAny(new[] { '[', ']', '\r', '\n', '\0' }) >= 0 || !names.Add(rule.SectionName))
					throw new FormatException("Application section names must be unique and cannot be General or contain brackets or line breaks.");
				string p = rule.Path;
				if (string.IsNullOrEmpty(p) || p.Length >= 32767 || p != p.Trim() || p.IndexOfAny(new[] { '\r', '\n', '\0', '"' }) >= 0 || p.EndsWith('\\') ||
					!((p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\') || (p.Length >= 3 && p[0] == '\\' && p[1] == '\\')))
					throw new FormatException("Each application Path must be an absolute executable path.");
				try {
					if (Path.GetFullPath(p).Length >= 32768) throw new FormatException("Application Path is too long.");
				} catch (ArgumentException e) {
					throw new FormatException("Each application Path must be an absolute executable path.", e);
				}
				ValidateAction(rule.Action);
			}
		}

		private static void ValidateAction(SleepPreventionAction action) {
			if (action != SleepPreventionAction.NoSleep && action != SleepPreventionAction.KeepDisplay && action != SleepPreventionAction.KeepUnlocked)
				throw new FormatException("Invalid sleep prevention action.");
		}

		private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (sender is ApplicationSettings && e.PropertyName == nameof(ApplicationSettings.ActionIndex)) return;
			TrySave();
		}
		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
			UpdateSubscriptions();
			TrySave();
		}

		private void UpdateSubscriptions() {
			var current = new HashSet<ApplicationSettings>(Settings.Applications);
			foreach (var rule in subscribed) if (!current.Contains(rule)) rule.PropertyChanged -= OnPropertyChanged;
			foreach (var rule in current) if (!subscribed.Contains(rule)) rule.PropertyChanged += OnPropertyChanged;
			subscribed.Clear();
			subscribed.UnionWith(current);
		}

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

		public void SaveWindowBounds(int x, int y, int width, int height) {
			if (width < MinimumWindowWidth || width > 32768)
				throw new ArgumentOutOfRangeException(nameof(width), "Window width must be between 640 and 32768 pixels.");
			if (height < MinimumWindowHeight || height > 32768)
				throw new ArgumentOutOfRangeException(nameof(height), "Window height must be between 480 and 32768 pixels.");
			if (WindowBounds == (x, y, width, height)) return;
			WindowBounds = (x, y, width, height);
			TrySave();
		}

		public void Save() {
			Validate(Settings);
			if (WindowBounds is { } bounds && (bounds.Width < MinimumWindowWidth || bounds.Width > 32768 || bounds.Height < MinimumWindowHeight || bounds.Height > 32768))
				throw new FormatException("Settings window dimensions must be at least 640 by 480 and at most 32768 pixels.");
			string directory = Path.GetDirectoryName(path)!;
			string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
			try {
				File.Copy(path, temp, false);
				var existing = new Dictionary<ApplicationSettings, string>();
				foreach (var pair in savedSections) {
					if (!Settings.Applications.Contains(pair.Key) || !string.Equals(pair.Value, pair.Key.SectionName, StringComparison.Ordinal)) {
						existing.Add(pair.Key, ReadSection(temp, pair.Value));
						WriteSection(temp, pair.Value, null);
					}
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
					if (existing.TryGetValue(rule, out string? contents)) WriteSection(temp, rule.SectionName, contents);
					WriteValue(temp, rule.SectionName, "Path", rule.Path);
					WriteValue(temp, rule.SectionName, "Action", rule.Action.ToString());
					WriteValue(temp, rule.SectionName, "VisibleWindowOnly", rule.VisibleWindowOnly ? "1" : "0");
					updated.Add(rule, rule.SectionName);
				}
				File.Move(temp, path, true);
				savedSections = updated;
			} finally {
				if (File.Exists(temp)) File.Delete(temp);
			}
		}

		private static string ReadValue(string file, string section, string key, string fallback, int capacity = 16) {
			var value = new StringBuilder(capacity);
			uint length = GetPrivateProfileString(section, key, fallback, value, (uint)capacity, file);
			if (length >= capacity - 1) throw new FormatException(section + "/" + key + " is too long.");
			return value.ToString();
		}

		private static IEnumerable<string> ReadSections(string file, int capacity) {
			char[] buffer = new char[capacity];
			uint length = GetPrivateProfileSectionNames(buffer, (uint)buffer.Length, file);
			if (length >= buffer.Length - 2) throw new IOException("Could not read settings sections.");
			return new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
		}

		private static string ReadSection(string file, string section) {
			int capacity = checked((int)(new FileInfo(file).Length / 2 + 2));
			char[] buffer = new char[capacity];
			uint length = GetPrivateProfileSection(section, buffer, (uint)buffer.Length, file);
			if (length >= buffer.Length - 2) throw new IOException("Could not read application settings.");
			return new string(buffer, 0, (int)length) + "\0\0";
		}

		private static void WriteValue(string file, string section, string key, string value) {
			if (!WritePrivateProfileString(section, key, value, file)) throw new IOException("Could not write " + section + "/" + key + ".");
		}
		private static void WriteSection(string file, string section, string? value) {
			if (!WritePrivateProfileSection(section, value, file)) throw new IOException("Could not write application section " + section + ".");
		}

		public void Dispose() {
			Settings.PropertyChanged -= OnPropertyChanged;
			Settings.Applications.CollectionChanged -= OnCollectionChanged;
			foreach (var rule in subscribed) rule.PropertyChanged -= OnPropertyChanged;
			subscribed.Clear();
		}

		[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", CharSet = CharSet.Unicode)]
		private static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, uint size, string file);
		[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileSectionNamesW", CharSet = CharSet.Unicode)]
		private static extern uint GetPrivateProfileSectionNames([Out] char[] buffer, uint size, string file);
		[DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileSectionW", CharSet = CharSet.Unicode)]
		private static extern uint GetPrivateProfileSection(string section, [Out] char[] buffer, uint size, string file);
		[DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool WritePrivateProfileString(string? section, string? key, string? value, string file);
		[DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileSectionW", CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool WritePrivateProfileSection(string section, string? value, string file);
	}
}
