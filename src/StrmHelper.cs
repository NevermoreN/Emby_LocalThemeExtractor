using System;
using System.IO;

namespace LocalThemeExtractor;

internal static class StrmHelper
{
	public static string ResolveMediaUrl(string itemPath)
	{
		if (string.IsNullOrWhiteSpace(itemPath))
		{
			return null;
		}
		if (string.Equals(Path.GetExtension(itemPath), ".strm", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				string text = File.ReadAllText(itemPath).Trim();
				return string.IsNullOrEmpty(text) ? null : text;
			}
			catch
			{
				return null;
			}
		}
		if (!File.Exists(itemPath))
		{
			return null;
		}
		return itemPath;
	}

	public static string GetItemDirectory(string itemPath)
	{
		if (string.IsNullOrEmpty(itemPath))
		{
			return null;
		}
		if (File.Exists(itemPath))
		{
			return Path.GetDirectoryName(itemPath);
		}
		if (Directory.Exists(itemPath))
		{
			return itemPath;
		}
		return null;
	}
}
