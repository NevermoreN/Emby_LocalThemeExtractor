using System;
using System.Collections.Generic;
using System.Linq;
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
