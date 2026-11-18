using System;
using System.Linq;
using System.Reflection;

namespace Kobachi.AutoAwake;

/// <summary>Creates the About page information from assembly metadata.</summary>
internal sealed class AboutInformation {
	/// <summary>Gets the application title and version.</summary>
	public string TitleAndVersion { get; }

	/// <summary>Gets the copyright notice for the application author.</summary>
	public string Copyright { get; }

	/// <summary>Reads the title, version, and author from the specified assembly.</summary>
	public AboutInformation(Assembly assembly) {
		var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? throw new InvalidOperationException("Assembly title is missing.");
		var version = assembly.GetName().Version ?? throw new InvalidOperationException("Assembly version is missing.");
		var author = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			             .Single(attribute => attribute.Key == "Authors").Value
		             ?? throw new InvalidOperationException("Assembly author is missing.");
		TitleAndVersion = $"{title} {version}";
		Copyright = $"Copyright(C) by {author}";
	}
}
