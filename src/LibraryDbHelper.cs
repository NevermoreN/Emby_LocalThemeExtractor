using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LocalThemeExtractor;

internal static class LibraryDbHelper
{
	public struct LibraryInfo
	{
		public string Id;

		public string Name;

		public string CollectionType;
	}

	public struct IntroMarker
	{
		public long ItemId;

		public double StartSec;

		public double EndSec;
	}

	public struct EpisodeInfo
	{
		public long ItemId;

		public string Path;

		public long SeriesItemId;

		public string SeriesName;

		public string SeriesPath;

		public int SeasonNumber;

		public int EpisodeNumber;
	}

	public struct MovieInfo
	{
		public long ItemId;

		public string Name;

		public string Path;
	}

	private const int TypePhysicalFolder = 3;

	private const int TypeCollectionFolder = 4;

	private const int TypeMovie = 5;

	private const int TypeSeries = 6;

	private const int TypeEpisode = 8;

	private const int ExtradataLibraryOptions = 2;

	private const int MarkerIntroStart = 1;

	private const int MarkerIntroEnd = 2;

	private static string DbPath => "/config/data/library.db";

	public static List<LibraryInfo> GetLibraries()
	{
		List<LibraryInfo> result = new List<LibraryInfo>();
		if (!File.Exists(DbPath))
		{
			return result;
		}
		SqliteReader.Query(DbPath, "SELECT cf.Id, cf.Name, ie.Value FROM MediaItems cf LEFT JOIN ItemExtradata ie        ON cf.Id = ie.ItemId AND ie.ExtradataTypeId = " + 2 + " WHERE cf.type = " + 4 + " ORDER BY cf.Name", delegate(Func<int, object> read)
		{
			string id = SqliteReader.GetLong(read, 0, 0L).ToString();
			string name = SqliteReader.GetString(read, 1) ?? "";
			string collectionType = ParseContentType(SqliteReader.GetString(read, 2) ?? "");
			result.Add(new LibraryInfo
			{
				Id = id,
				Name = name,
				CollectionType = collectionType
			});
		});
		return result;
	}

	public static List<(EpisodeInfo ep, IntroMarker intro)> GetIntroEpisodesPerSeries(int preferSeason = 1, int minIntroSeconds = 20, ICollection<string> libraryIds = null)
	{
		List<(EpisodeInfo, IntroMarker)> list = new List<(EpisodeInfo, IntroMarker)>();
		if (!File.Exists(DbPath))
		{
			return list;
		}
		Dictionary<long, IntroMarker> episodeMarkers = new Dictionary<long, IntroMarker>();
		SqliteReader.Query(DbPath, "SELECT ItemId, StartPositionTicks, MarkerType FROM Chapters3 WHERE MarkerType IN (" + 1 + "," + 2 + ") ORDER BY ItemId, StartPositionTicks", delegate(Func<int, object> read)
		{
			long long4 = SqliteReader.GetLong(read, 0, 0L);
			long long5 = SqliteReader.GetLong(read, 1, 0L);
			int int3 = SqliteReader.GetInt(read, 2);
			double num3 = (double)long5 / 10000000.0;
			switch (int3)
			{
			case 1:
				episodeMarkers[long4] = new IntroMarker
				{
					ItemId = long4,
					StartSec = num3,
					EndSec = -1.0
				};
				break;
			case 2:
			{
				if (episodeMarkers.TryGetValue(long4, out var value4) && value4.EndSec < 0.0)
				{
					if (num3 - value4.StartSec >= (double)minIntroSeconds)
					{
						episodeMarkers[long4] = new IntroMarker
						{
							ItemId = long4,
							StartSec = value4.StartSec,
							EndSec = num3
						};
					}
					else
					{
						episodeMarkers.Remove(long4);
					}
				}
				break;
			}
			}
		});
		foreach (long item3 in (from kv in episodeMarkers
			where kv.Value.EndSec < 0.0
			select kv.Key).ToList())
		{
			episodeMarkers.Remove(item3);
		}
		if (episodeMarkers.Count == 0)
		{
			return list;
		}
		HashSet<long> physicalFolderIds = GetPhysicalFolderIds(ParseCfIds(libraryIds));
		string text = string.Join(",", episodeMarkers.Keys);
		string text2 = BuildSeriesPfFilter(physicalFolderIds);
		Dictionary<long, List<(EpisodeInfo, IntroMarker)>> seriesMap = new Dictionary<long, List<(EpisodeInfo, IntroMarker)>>();
		SqliteReader.Query(DbPath, "SELECT ep.Id, ep.Path, ep.IndexNumber, ep.ParentIndexNumber,       ep.SeriesId, ep.SeriesName FROM MediaItems ep WHERE ep.type = " + 8 + "   AND ep.Id IN (" + text + ")   " + text2, delegate(Func<int, object> read)
		{
			long long2 = SqliteReader.GetLong(read, 0, 0L);
			string path = SqliteReader.GetString(read, 1) ?? "";
			int @int = SqliteReader.GetInt(read, 2);
			int int2 = SqliteReader.GetInt(read, 3);
			long long3 = SqliteReader.GetLong(read, 4, 0L);
			string seriesName = SqliteReader.GetString(read, 5) ?? "";
			if (episodeMarkers.TryGetValue(long2, out var value3))
			{
				EpisodeInfo episodeInfo = default(EpisodeInfo);
				episodeInfo.ItemId = long2;
				episodeInfo.Path = path;
				episodeInfo.SeriesItemId = long3;
				episodeInfo.SeriesName = seriesName;
				episodeInfo.SeasonNumber = int2;
				episodeInfo.EpisodeNumber = @int;
				EpisodeInfo item2 = episodeInfo;
				if (!seriesMap.ContainsKey(long3))
				{
					seriesMap[long3] = new List<(EpisodeInfo, IntroMarker)>();
				}
				seriesMap[long3].Add((item2, value3));
			}
		});
		if (seriesMap.Count == 0)
		{
			return list;
		}
		string text3 = string.Join(",", seriesMap.Keys);
		Dictionary<long, string> seriesPaths = new Dictionary<long, string>();
		SqliteReader.Query(DbPath, "SELECT Id, Path FROM MediaItems WHERE type = " + 6 + "   AND Id IN (" + text3 + ")", delegate(Func<int, object> read)
		{
			long @long = SqliteReader.GetLong(read, 0, 0L);
			string value2 = SqliteReader.GetString(read, 1) ?? "";
			seriesPaths[@long] = value2;
		});
		foreach (KeyValuePair<long, List<(EpisodeInfo, IntroMarker)>> item4 in seriesMap)
		{
			List<(EpisodeInfo, IntroMarker)> value = item4.Value;
			value.Sort(delegate((EpisodeInfo, IntroMarker) a, (EpisodeInfo, IntroMarker) b)
			{
				int num = ((a.Item1.SeasonNumber != preferSeason) ? 1 : 0);
				int num2 = ((b.Item1.SeasonNumber != preferSeason) ? 1 : 0);
				if (num != num2)
				{
					return num.CompareTo(num2);
				}
				return (a.Item1.SeasonNumber != b.Item1.SeasonNumber) ? a.Item1.SeasonNumber.CompareTo(b.Item1.SeasonNumber) : a.Item1.EpisodeNumber.CompareTo(b.Item1.EpisodeNumber);
			});
			(EpisodeInfo, IntroMarker) tuple = value[0];
			var (item, _) = tuple;
			seriesPaths.TryGetValue(item.SeriesItemId, out item.SeriesPath);
			list.Add((item, tuple.Item2));
		}
		return list;
	}

	public static List<EpisodeInfo> GetTvSeriesForFallback(HashSet<long> excludeSeriesIds, ICollection<string> libraryIds = null)
	{
		List<EpisodeInfo> list = new List<EpisodeInfo>();
		if (!File.Exists(DbPath))
		{
			return list;
		}
		string text = BuildSeriesPfFilter(GetPhysicalFolderIds(ParseCfIds(libraryIds)));
		string sql = "SELECT ep.Id, ep.Path, ep.IndexNumber, ep.ParentIndexNumber,       ep.SeriesId, ep.SeriesName FROM MediaItems ep INNER JOIN (   SELECT SeriesId,          MAX(ParentIndexNumber * 10000 + IndexNumber) as maxKey   FROM MediaItems   WHERE type = " + 8 + "     AND Path IS NOT NULL   GROUP BY SeriesId ) best ON ep.SeriesId = best.SeriesId       AND (ep.ParentIndexNumber * 10000 + ep.IndexNumber) = best.maxKey WHERE ep.type = " + 8 + "   AND ep.Path IS NOT NULL   " + text;
		Dictionary<long, EpisodeInfo> seriesMap = new Dictionary<long, EpisodeInfo>();
		SqliteReader.Query(DbPath, sql, delegate(Func<int, object> read)
		{
			long long2 = SqliteReader.GetLong(read, 0, 0L);
			string path = SqliteReader.GetString(read, 1) ?? "";
			int @int = SqliteReader.GetInt(read, 2);
			int int2 = SqliteReader.GetInt(read, 3);
			long long3 = SqliteReader.GetLong(read, 4, 0L);
			string seriesName = SqliteReader.GetString(read, 5) ?? "";
			if ((excludeSeriesIds == null || !excludeSeriesIds.Contains(long3)) && !seriesMap.ContainsKey(long3))
			{
				seriesMap[long3] = new EpisodeInfo
				{
					ItemId = long2,
					Path = path,
					SeriesItemId = long3,
					SeriesName = seriesName,
					SeasonNumber = int2,
					EpisodeNumber = @int
				};
			}
		});
		if (seriesMap.Count == 0)
		{
			return list;
		}
		string text2 = string.Join(",", seriesMap.Keys);
		Dictionary<long, string> seriesPaths = new Dictionary<long, string>();
		SqliteReader.Query(DbPath, "SELECT Id, Path FROM MediaItems WHERE type = " + 6 + "   AND Id IN (" + text2 + ")", delegate(Func<int, object> read)
		{
			long @long = SqliteReader.GetLong(read, 0, 0L);
			string value2 = SqliteReader.GetString(read, 1) ?? "";
			seriesPaths[@long] = value2;
		});
		foreach (KeyValuePair<long, EpisodeInfo> item in seriesMap)
		{
			EpisodeInfo value = item.Value;
			seriesPaths.TryGetValue(value.SeriesItemId, out value.SeriesPath);
			list.Add(value);
		}
		return list;
	}

	public static List<MovieInfo> GetAllMovies(ICollection<string> libraryIds = null)
	{
		List<MovieInfo> result = new List<MovieInfo>();
		if (!File.Exists(DbPath))
		{
			return result;
		}
		string text = BuildMoviePfFilter(GetPhysicalFolderIds(ParseCfIds(libraryIds)));
		SqliteReader.Query(DbPath, "SELECT Id, Name, Path FROM MediaItems WHERE type = " + 5 + "   AND Path IS NOT NULL   " + text, delegate(Func<int, object> read)
		{
			long @long = SqliteReader.GetLong(read, 0, 0L);
			string name = SqliteReader.GetString(read, 1) ?? "";
			string path = SqliteReader.GetString(read, 2) ?? "";
			result.Add(new MovieInfo
			{
				ItemId = @long,
				Name = name,
				Path = path
			});
		});
		return result;
	}

	private static HashSet<long> ParseCfIds(ICollection<string> cfIds)
	{
		if (cfIds == null || cfIds.Count == 0)
		{
			return null;
		}
		HashSet<long> hashSet = new HashSet<long>();
		foreach (string cfId in cfIds)
		{
			if (long.TryParse(cfId, out var result))
			{
				hashSet.Add(result);
			}
		}
		if (hashSet.Count <= 0)
		{
			return null;
		}
		return hashSet;
	}

	private static HashSet<long> GetPhysicalFolderIds(HashSet<long> cfIds)
	{
		HashSet<long> pfIds = new HashSet<long>();
		if (cfIds == null || cfIds.Count == 0)
		{
			return pfIds;
		}
		string text = string.Join(",", cfIds);
		HashSet<string> physicalPaths = new HashSet<string>(StringComparer.Ordinal);
		SqliteReader.Query(DbPath, "SELECT Value FROM ItemExtradata WHERE ExtradataTypeId = " + 2 + "   AND ItemId IN (" + text + ")", delegate(Func<int, object> read)
		{
			foreach (string item in ParsePathInfos(SqliteReader.GetString(read, 0) ?? ""))
			{
				if (!string.IsNullOrEmpty(item))
				{
					physicalPaths.Add(item);
				}
			}
		});
		if (physicalPaths.Count == 0)
		{
			return pfIds;
		}
		List<string> list = new List<string>();
		foreach (string item2 in physicalPaths)
		{
			list.Add("'" + item2.Replace("'", "''") + "'");
		}
		SqliteReader.Query(DbPath, "SELECT Id FROM MediaItems WHERE type = " + 3 + "   AND Path IN (" + string.Join(",", list) + ")", delegate(Func<int, object> read)
		{
			pfIds.Add(SqliteReader.GetLong(read, 0, 0L));
		});
		return pfIds;
	}

	private static string BuildSeriesPfFilter(HashSet<long> allowedPfIds)
	{
		if (allowedPfIds == null || allowedPfIds.Count == 0)
		{
			return "";
		}
		string text = string.Join(",", allowedPfIds);
		return "AND ep.SeriesId IN (SELECT Id FROM MediaItems WHERE type = " + 6 + "   AND ParentId IN (" + text + "))";
	}

	private static string BuildMoviePfFilter(HashSet<long> allowedPfIds)
	{
		if (allowedPfIds == null || allowedPfIds.Count == 0)
		{
			return "";
		}
		string text = string.Join(",", allowedPfIds);
		return "AND Id IN (SELECT ItemId FROM AncestorIds2 WHERE AncestorId IN (" + text + "))";
	}

	private static List<string> ParsePathInfos(string json)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrEmpty(json))
		{
			return list;
		}
		Match match = Regex.Match(json, "\"PathInfos\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
		if (!match.Success)
		{
			return list;
		}
		foreach (Match item in Regex.Matches(match.Groups[1].Value, "\"Path\"\\s*:\\s*\"([^\"]+)\""))
		{
			if (item.Success)
			{
				list.Add(item.Groups[1].Value);
			}
		}
		return list;
	}

	private static string ParseContentType(string json)
	{
		if (string.IsNullOrEmpty(json))
		{
			return "";
		}
		int num = json.IndexOf("\"ContentType\":\"", StringComparison.Ordinal);
		if (num < 0)
		{
			return "";
		}
		int num2 = num + "\"ContentType\":\"".Length;
		int num3 = json.IndexOf('"', num2);
		if (num3 < 0)
		{
			return "";
		}
		return json.Substring(num2, num3 - num2);
	}
}
