using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kobachi.AutoAwake.Controls;

/// <summary>Shows an input prompt without a dismiss button or a light-dismiss overlay.</summary>
public sealed partial class CaptureTeachingTip : TeachingTip {
	/// <summary>Hides the standard header close button when the template is applied.</summary>
	protected override void OnApplyTemplate() {
		base.OnApplyTemplate();
		if (GetTemplateChild("AlternateCloseButton") is not Button button) {
			throw new InvalidOperationException("The teaching tip template is missing its close button.");
		}
		button.Width = 0;
		button.Height = 0;
		button.MinWidth = 0;
		button.MinHeight = 0;
		button.IsTabStop = false;
		button.IsHitTestVisible = false;
		button.Visibility = Visibility.Collapsed;
	}
}
