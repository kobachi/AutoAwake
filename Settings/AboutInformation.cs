using System;
using System.Linq;
using System.Reflection;

namespace Kobachi.AutoAwake {
	internal sealed class AboutInformation {
		public string TitleAndVersion { get; }
		public string Copyright { get; }

		public AboutInformation(Assembly assembly) {
			string title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
				?? throw new InvalidOperationException("Assembly title is missing.");
			Version version = assembly.GetName().Version
				?? throw new InvalidOperationException("Assembly version is missing.");
			string author = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
				.Single(attribute => attribute.Key == "Authors").Value
				?? throw new InvalidOperationException("Assembly author is missing.");
			TitleAndVersion = $"{title} {version}";
			Copyright = $"Copyright(C) by {author}";
		}
	}
}
