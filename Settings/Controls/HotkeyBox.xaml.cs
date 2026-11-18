using System;
using Kobachi.AutoAwake.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI.Core;

namespace Kobachi.AutoAwake.Controls;

/// <summary>
/// Displays and edits a global hotkey in the text format used by AutoAwake Core, such as "Ctrl+Alt+F9".
/// Clicking the box starts capturing; an empty <see cref="Value"/> means no hotkey.
/// </summary>
public sealed partial class HotkeyBox : UserControl {
	/// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
	public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
		nameof(Value), typeof(string), typeof(HotkeyBox), new PropertyMetadata(string.Empty, OnValueChanged));

	/// <summary>Loads the localized display strings.</summary>
	private readonly ResourceLoader resources = new();

	/// <summary>Whether the box is waiting for a shortcut key.</summary>
	private bool capturing;

	/// <summary>Whether the input prompt is opening and temporarily moving focus.</summary>
	private bool openingCaptureTip;

	/// <summary>The modifier keys currently held while capturing.</summary>
	private HotkeyModifiers pendingModifiers;

	/// <summary>A validation message shown instead of the prompt while capturing, or null.</summary>
	private string? message;

	/// <summary>Checks whether a captured shortcut can be assigned.</summary>
	public Func<HotkeyModifiers, VirtualKey, bool>? CanAssign { get; set; }

	/// <summary>Raised after a shortcut or an empty value is committed by the user.</summary>
	public event Action<string>? ValueCommitted;

	/// <summary>Initializes a new instance of the <see cref="HotkeyBox"/> class.</summary>
	public HotkeyBox() {
		InitializeComponent();
		Loaded += HotkeyBox_Loaded;
		UpdateDisplay();
	}

	/// <summary>Applies the control's accessible name to its focusable button.</summary>
	private void HotkeyBox_Loaded(object sender, RoutedEventArgs e) {
		AutomationProperties.SetName(CaptureButton, AutomationProperties.GetName(this));
	}

	/// <summary>Gets or sets the hotkey text, or an empty string when no hotkey is assigned.</summary>
	public string Value {
		get => (string)GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	/// <summary>Refreshes the display when <see cref="Value"/> changes.</summary>
	private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) {
		((HotkeyBox)sender).UpdateDisplay();
	}

	/// <summary>Starts waiting for a shortcut key.</summary>
	private void BeginCapture() {
		capturing = true;
		pendingModifiers = HotkeyModifiers.None;
		message = null;
		UnavailableTip.IsOpen = false;
		CaptureButton.Focus(FocusState.Programmatic);
		UpdateDisplay();
		openingCaptureTip = true;
		CaptureTip.IsOpen = true;
		if (!DispatcherQueue.TryEnqueue(RestoreCaptureFocus)) {
			EndCapture();
		}
	}

	/// <summary>Stops waiting for a shortcut key and keeps the current value.</summary>
	private void EndCapture() {
		capturing = false;
		openingCaptureTip = false;
		pendingModifiers = HotkeyModifiers.None;
		message = null;
		CaptureTip.IsOpen = false;
		UpdateDisplay();
	}

	/// <summary>Restores input focus after the prompt's popup has opened.</summary>
	private void RestoreCaptureFocus() {
		if (capturing && !CaptureButton.Focus(FocusState.Programmatic)) {
			EndCapture();
		}
		openingCaptureTip = false;
	}

	/// <summary>Ends input if the prompt cannot remain open.</summary>
	private void CaptureTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) {
		if (capturing) {
			EndCapture();
		}
	}

	/// <summary>Stops capturing and sets the value.</summary>
	/// <param name="value">The new hotkey text, or an empty string for none.</param>
	private void Commit(string value) {
		EndCapture();
		UnavailableTip.IsOpen = false;
		Value = value;
		ValueCommitted?.Invoke(value);
	}

	/// <summary>Clears the current shortcut and reports that it cannot be used.</summary>
	private void RejectShortcut() {
		Commit(string.Empty);
		UnavailableTip.IsOpen = true;
	}

	/// <summary>Updates the displayed shortcut or input status.</summary>
	private void UpdateDisplay() {
		if (capturing) {
			if (message is not null) {
				DisplayText.Text = message;
			} else if (pendingModifiers != HotkeyModifiers.None) {
				DisplayText.Text = HotkeyFormatter.FormatModifiers(pendingModifiers) + "+";
			} else {
				DisplayText.Text = Value.Length == 0 ? resources.GetString("HotkeyNone") : Value;
			}
		} else {
			DisplayText.Text = Value.Length == 0 ? resources.GetString("HotkeyNone") : Value;
		}
	}

	/// <summary>Determines whether a key is currently held down.</summary>
	/// <param name="key">The key to check.</param>
	private static bool IsDown(VirtualKey key) {
		return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
	}

	/// <summary>Reads the modifier keys that are currently held down.</summary>
	private static HotkeyModifiers ReadModifiers() {
		var modifiers = HotkeyModifiers.None;
		if (IsDown(VirtualKey.Control)) {
			modifiers |= HotkeyModifiers.Control;
		}
		if (IsDown(VirtualKey.Menu)) {
			modifiers |= HotkeyModifiers.Alt;
		}
		if (IsDown(VirtualKey.Shift)) {
			modifiers |= HotkeyModifiers.Shift;
		}
		if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) {
			modifiers |= HotkeyModifiers.Windows;
		}
		return modifiers;
	}

	/// <summary>Determines whether a key is a modifier key.</summary>
	/// <param name="key">The key to check.</param>
	private static bool IsModifierKey(VirtualKey key) {
		return key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
			or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
			or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
			or VirtualKey.LeftWindows or VirtualKey.RightWindows;
	}

	/// <summary>Toggles capturing when the box is clicked.</summary>
	private void CaptureButton_Click(object sender, RoutedEventArgs e) {
		if (capturing) {
			EndCapture();
		} else {
			BeginCapture();
		}
	}

	/// <summary>Ends capturing when focus is lost, clearing the value if a modifier was held.</summary>
	private void CaptureButton_LostFocus(object sender, RoutedEventArgs e) {
		if (!capturing || openingCaptureTip) {
			return;
		}
		// Modifiers were held when focus was lost, so another feature (such as Win+L or Alt+Tab) took the shortcut.
		if (pendingModifiers != HotkeyModifiers.None) {
			RejectShortcut();
		} else {
			EndCapture();
		}
	}

	/// <summary>Interprets key presses while capturing and commits a valid shortcut.</summary>
	private void CaptureButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e) {
		if (!capturing) {
			return;
		}
		var key = e.Key;
		var modifiers = ReadModifiers();
		if (IsModifierKey(key)) {
			pendingModifiers = modifiers;
			message = null;
			UpdateDisplay();
			e.Handled = true;
			return;
		}
		if (key == VirtualKey.Tab && (modifiers == HotkeyModifiers.None || modifiers == HotkeyModifiers.Shift)) {
			// Let keyboard focus navigation leave the box, which cancels capturing.
			return;
		}
		e.Handled = true;
		if (modifiers == HotkeyModifiers.None) {
			if (key == VirtualKey.Escape) {
				EndCapture();
			} else if (key is VirtualKey.Back or VirtualKey.Delete) {
				Commit(string.Empty);
			} else {
				message = resources.GetString("HotkeyModifierRequired");
				UpdateDisplay();
			}
			return;
		}
		pendingModifiers = modifiers;
		if (!HotkeyFormatter.TryGetKeyName(key, out var name)) {
			message = resources.GetString("HotkeyKeyUnsupported");
			UpdateDisplay();
			return;
		}
		if (CanAssign is not null && !CanAssign(modifiers, key)) {
			RejectShortcut();
			return;
		}
		Commit(HotkeyFormatter.Format(modifiers, name));
	}

	/// <summary>Tracks released modifier keys while capturing.</summary>
	private void CaptureButton_PreviewKeyUp(object sender, KeyRoutedEventArgs e) {
		if (!capturing) {
			return;
		}
		if (IsModifierKey(e.Key)) {
			pendingModifiers = ReadModifiers();
			message = null;
			UpdateDisplay();
		} else if (pendingModifiers != HotkeyModifiers.None) {
			// A key-up without its key-down can occur when another application owns the shortcut.
			RejectShortcut();
		}
		// Prevent the button from treating Space or Enter as a click while capturing.
		e.Handled = true;
	}
}
