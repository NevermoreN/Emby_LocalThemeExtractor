using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;

namespace LocalThemeExtractor;

public class Plugin : BasePluginSimpleUI<LteOptions>, IHasThumbImage
{
	public static Plugin Instance { get; private set; }

	public override string Name => "Local Theme Extractor";

	public override Guid Id => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

	public override string Description => "Extracts theme songs from local media using Emby's existing intro markers and ffmpeg — no external services required for TV shows.";

	public ImageFormat ThumbImageFormat => (ImageFormat)2;

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
			options.LibraryList = ((IEnumerable<LibraryDbHelper.LibraryInfo>)libraries).Select((Func<LibraryDbHelper.LibraryInfo, EditorSelectOption>)((LibraryDbHelper.LibraryInfo lib) => new EditorSelectOption(lib.Id, lib.Name, true, (string)null, (string)null, (string)null, (string)null))).ToList();
		}
		catch
		{
			options.LibraryList = new List<EditorSelectOption>();
		}
		return options;
	}

	public Stream GetThumbImage()
	{
		return ((object)this).GetType().Assembly.GetManifestResourceStream("LocalThemeExtractor.thumb.jpg");
	}
}
