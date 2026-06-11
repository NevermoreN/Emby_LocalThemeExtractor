using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;

namespace LocalThemeExtractor;

public class Plugin : BasePluginSimpleUI<LteOptions>
{
	public static Plugin Instance { get; private set; }

	public override string Name => "Local Theme Extractor";

	public override Guid Id => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

	public override string Description => "Extracts theme songs from local media using Emby's existing intro markers and ffmpeg — no external services required for TV shows.";

	static Plugin()
	{
		// Some Emby environments or co-installed plugins reference SkiaSharp
		// which may not be present. Prevent FileNotFoundException from crashing us.
		AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
	}

	private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
	{
		// We don't use SkiaSharp — if something in the host tries to load it
		// and it's missing, just ignore silently rather than crashing.
		string name = new AssemblyName(args.Name).Name;
		if (string.Equals(name, "SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(name, "SkiaSharp.HarfBuzz", StringComparison.OrdinalIgnoreCase))
		{
			// Return null = "not found, keep looking" — .NET still throws, but
			// we'll catch it in Execute. The key is we don't crash during load.
			return null;
		}
		return null;
	}

	public Plugin(IApplicationHost applicationHost)
		: base(applicationHost)
	{
		Instance = this;
	}

	public LteOptions GetCurrentOptions()
	{
		return base.GetOptions();
	}

	protected override LteOptions OnBeforeShowUI(LteOptions options)
	{
		try
		{
			List<LibraryDbHelper.LibraryInfo> libraries = LibraryDbHelper.GetLibraries();
			options.LibraryList = libraries
				.Select(lib => new EditorSelectOption(lib.Id, lib.Name, true, null, null, null, null))
				.ToList();
		}
		catch
		{
			options.LibraryList = new List<EditorSelectOption>();
		}
		return options;
	}
}
