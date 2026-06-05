using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace LocalThemeExtractor
{
    public class ExtractionTask : IScheduledTask
    {
        public string Key         => "LocalThemeExtractorTask";
        public string Name        => "提取本地主题曲";
        public string Description => "利用 Emby 片头标记和 ffmpeg 从本地媒体库提取主题曲，无需外部下载。";
        public string Category    => "Local Theme Extractor";

        private readonly ILogger _logger;

        // Cache writable check per mount root to avoid repeated I/O (thread-safe)
        private readonly ConcurrentDictionary<string, bool> _writableCache = new ConcurrentDictionary<string, bool>();

        public ExtractionTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("LocalThemeExtractor");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type        = TaskTriggerInfo.TriggerWeekly,
                    DayOfWeek   = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        // ── Execute ──────────────────────────────────────────────────────

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance?.GetCurrentOptions() ?? new LteOptions();

            _logger.Info("[LTE] CONFIG: Instance={0} LibraryScope='{1}' Overwrite={2}",
                Plugin.Instance != null ? "OK" : "NULL",
                config.LibraryScope ?? "(null)",
                config.OverwriteExisting);

            var enabledLibs = ParseLibraryScope(config.LibraryScope);

            _logger.Info("[LTE] 任务开始。线程={0}，媒体库={1}",
                config.MaxParallelism,
                enabledLibs == null ? "全部" : string.Join(",", enabledLibs));

            progress.Report(0);

            // ── TV (intro markers) ─────────────────────────────────────
            var processedSeriesIds = new HashSet<long>();
            double tvEnd = (config.TvExtractIntro && config.TvFallbackToCredits) ? 33 :
                           config.TvExtractIntro ? 50 : 0;

            if (config.TvExtractIntro)
                processedSeriesIds = await ProcessTv(config, enabledLibs, progress, 0, tvEnd, cancellationToken);

            // ── TV fallback (credits) ──────────────────────────────────
            double tvFallbackEnd = config.MovieExtractCredits ? 50 : 100;
            if (config.TvFallbackToCredits)
                await ProcessTvFallback(config, enabledLibs, processedSeriesIds,
                    progress, tvEnd, tvFallbackEnd, cancellationToken);

            // ── Movies ─────────────────────────────────────────────────
            double movieFrom = (config.TvExtractIntro || config.TvFallbackToCredits) ? 50 : 0;
            if (config.MovieExtractCredits)
                await ProcessMovies(config, enabledLibs, progress, movieFrom, 100, cancellationToken);

            progress.Report(100);
            _logger.Info("[LTE] 任务完成");
        }

        // ── TV series processing ─────────────────────────────────────────

        private async Task<HashSet<long>> ProcessTv(
            LteOptions config, HashSet<string> enabledLibs,
            IProgress<double> progress, double pctFrom, double pctTo,
            CancellationToken ct)
        {
            var seriesList = LibraryDbHelper.GetIntroEpisodesPerSeries(
                config.TvPreferSeasonNumber, config.TvMinIntroSeconds, enabledLibs);

            _logger.Info("[LTE] 电视剧：共 {0} 个系列有片头标记", seriesList.Count);

            var allSeriesWithIntro = new HashSet<long>();
            foreach (var item in seriesList)
                allSeriesWithIntro.Add(item.Item1.SeriesItemId);

            var pending = new List<(LibraryDbHelper.EpisodeInfo, LibraryDbHelper.IntroMarker)>();
            foreach (var item in seriesList)
            {
                string seriesDir = GetSeriesDir(item.Item1);
                if (string.IsNullOrEmpty(seriesDir)) continue;
                string themeFile = Path.Combine(seriesDir, "theme.mp3");
                if (!config.OverwriteExisting && File.Exists(themeFile))
                {
                    _logger.Debug("[LTE] 跳过（已有）: {0}", item.Item1.SeriesName);
                    continue;
                }
                pending.Add(item);
            }

            _logger.Info("[LTE] 电视剧：待处理 {0} 个系列", pending.Count);
            await RunParallel(pending, config,
                async (item, cfg, tok) => { await ProcessOneTvSeries(item.Item1, item.Item2, cfg, tok); },
                progress, pctFrom, pctTo, ct);

            return allSeriesWithIntro;
        }

        private async Task ProcessOneTvSeries(
            LibraryDbHelper.EpisodeInfo ep, LibraryDbHelper.IntroMarker marker,
            LteOptions config, CancellationToken ct)
        {
            string seriesDir = GetSeriesDir(ep);
            if (string.IsNullOrEmpty(seriesDir)) return;
            if (!IsDirectoryWritable(seriesDir))
            {
                _logger.Debug("[LTE] 目录不可写，跳过：{0}", ep.SeriesName);
                return;
            }

            string mediaUrl = StrmHelper.ResolveMediaUrl(ep.Path);
            if (string.IsNullOrEmpty(mediaUrl)) return;

            string outputPath = Path.Combine(seriesDir, "theme.mp3");

            _logger.Info("[LTE] 电视剧 [{0}] S{1:D2}E{2:D2} {3:F1}s-{4:F1}s",
                ep.SeriesName, ep.SeasonNumber, ep.EpisodeNumber,
                marker.StartSec, marker.EndSec);

            byte[] audioData = await FfmpegHelper.ExtractAudioToMemoryAsync(
                mediaUrl, marker.StartSec, marker.EndSec,
                config.AudioBitrateKbps, config.UseHwDecode, config.HwDecoderName,
                _logger, ct);

            if (audioData != null)
            {
                WriteThemeFile(outputPath, audioData);
                _logger.Info("[LTE] 电视剧保存：{0}", outputPath);
            }
            else
            {
                _logger.Warn("[LTE] 电视剧提取失败：{0}", ep.SeriesName);
            }
        }

        // ── TV fallback (credits detection) ──────────────────────────────

        private async Task ProcessTvFallback(
            LteOptions config, HashSet<string> enabledLibs,
            HashSet<long> excludeSeriesIds,
            IProgress<double> progress, double pctFrom, double pctTo,
            CancellationToken ct)
        {
            var episodes = LibraryDbHelper.GetTvSeriesForFallback(excludeSeriesIds, enabledLibs);
            _logger.Info("[LTE] 电视剧片尾回退：共 {0} 个无片头标记的系列", episodes.Count);

            var pending = new List<LibraryDbHelper.EpisodeInfo>();
            foreach (var ep in episodes)
            {
                string seriesDir = GetSeriesDir(ep);
                if (string.IsNullOrEmpty(seriesDir)) continue;
                string themeFile = Path.Combine(seriesDir, "theme.mp3");
                if (!config.OverwriteExisting && File.Exists(themeFile)) continue;
                pending.Add(ep);
            }

            _logger.Info("[LTE] 电视剧片尾回退：待处理 {0} 个系列", pending.Count);
            await RunParallel(pending, config,
                async (ep, cfg, tok) => { await ProcessOneTvSeriesFallback(ep, cfg, tok); },
                progress, pctFrom, pctTo, ct);
        }

        private async Task ProcessOneTvSeriesFallback(
            LibraryDbHelper.EpisodeInfo ep, LteOptions config, CancellationToken ct)
        {
            string seriesDir = GetSeriesDir(ep);
            if (string.IsNullOrEmpty(seriesDir)) return;
            if (!IsDirectoryWritable(seriesDir))
            {
                _logger.Debug("[LTE] 回退：目录不可写，跳过：{0}", ep.SeriesName);
                return;
            }

            string mediaUrl = StrmHelper.ResolveMediaUrl(ep.Path);
            if (string.IsNullOrEmpty(mediaUrl)) return;

            double? totalDur = await FfmpegHelper.ProbeDurationAsync(mediaUrl, _logger, ct);
            if (totalDur == null || totalDur < config.MovieMinCreditsSeconds + 60) return;

            // Detect extract range: silencedetect (cheap) → blackdetect (expensive)
            double creditsEnd = totalDur.Value - 5;
            var range = await DetectExtractRange(
                mediaUrl, totalDur.Value, creditsEnd,
                config.MovieCreditsLookbackSeconds, config.MovieMinCreditsSeconds,
                config.UseHwDecode, config.HwDecoderName, false, 0, ct);

            if (range == null)
            {
                _logger.Info("[LTE] 回退：未检测到片尾起点：{0}", ep.SeriesName);
                return;
            }

            double extractStart = range.Value.Item1;
            double extractEnd   = range.Value.Item2;
            string outputPath = Path.Combine(seriesDir, "theme.mp3");

            _logger.Info("[LTE] 电视剧片尾回退 [{0}] S{1:D2}E{2:D2} {3:F1}s-{4:F1}s",
                ep.SeriesName, ep.SeasonNumber, ep.EpisodeNumber,
                extractStart, extractEnd);

            byte[] audioData = await FfmpegHelper.ExtractAudioToMemoryAsync(
                mediaUrl, extractStart, extractEnd,
                config.AudioBitrateKbps, config.UseHwDecode, config.HwDecoderName,
                _logger, ct);

            if (audioData != null)
            {
                WriteThemeFile(outputPath, audioData);
                _logger.Info("[LTE] 电视剧片尾回退保存：{0}", outputPath);
            }
            else
            {
                _logger.Warn("[LTE] 电视剧片尾回退失败：{0}", ep.SeriesName);
            }
        }

        // ── Movie processing ─────────────────────────────────────────────

        private async Task ProcessMovies(
            LteOptions config, HashSet<string> enabledLibs,
            IProgress<double> progress, double pctFrom, double pctTo,
            CancellationToken ct)
        {
            var movies = LibraryDbHelper.GetAllMovies(enabledLibs);
            _logger.Info("[LTE] 电影：共 {0} 部", movies.Count);

            var pending = new List<LibraryDbHelper.MovieInfo>();
            foreach (var movie in movies)
            {
                if (string.IsNullOrEmpty(movie.Path)) continue;
                string dir = GetMovieDir(movie.Path);
                if (string.IsNullOrEmpty(dir)) continue;
                string themeFile = Path.Combine(dir, "theme.mp3");
                if (!config.OverwriteExisting && File.Exists(themeFile)) continue;
                pending.Add(movie);
            }

            _logger.Info("[LTE] 电影：待处理 {0} 部", pending.Count);
            await RunParallel(pending, config,
                async (movie, cfg, tok) => { await ProcessOneMovie(movie, cfg, tok); },
                progress, pctFrom, pctTo, ct);
        }

        private async Task ProcessOneMovie(
            LibraryDbHelper.MovieInfo movie, LteOptions config, CancellationToken ct)
        {
            string mediaUrl = StrmHelper.ResolveMediaUrl(movie.Path);
            if (string.IsNullOrEmpty(mediaUrl)) return;

            string movieDir   = GetMovieDir(movie.Path);
            if (!IsDirectoryWritable(movieDir))
            {
                _logger.Debug("[LTE] 电影目录不可写，跳过：{0}", movie.Name);
                return;
            }
            string outputPath = Path.Combine(movieDir, "theme.mp3");

            double? totalDur = await FfmpegHelper.ProbeDurationAsync(mediaUrl, _logger, ct);
            if (totalDur == null || totalDur < config.MovieMinCreditsSeconds + 60) return;

            double creditsEnd = totalDur.Value - 5;

            // Detect: silencedetect (2MB) → blackdetect (200MB) → fixed window
            var range = await DetectExtractRange(
                mediaUrl, totalDur.Value, creditsEnd,
                config.MovieCreditsLookbackSeconds, config.MovieMinCreditsSeconds,
                config.UseHwDecode, config.HwDecoderName,
                config.MovieFallbackToEndWindow, config.MovieFallbackWindowSeconds, ct);

            if (range == null)
            {
                _logger.Info("[LTE] 电影：所有检测均无结果，跳过：{0}", movie.Name);
                return;
            }

            double extractStart = range.Value.Item1;
            double extractEnd   = range.Value.Item2;
            if (extractEnd - extractStart < 10) return;

            _logger.Info("[LTE] 电影提取 [{0}] {1:F1}s-{2:F1}s", movie.Name, extractStart, extractEnd);

            byte[] audioData = await FfmpegHelper.ExtractAudioToMemoryAsync(
                mediaUrl, extractStart, extractEnd,
                config.AudioBitrateKbps, config.UseHwDecode, config.HwDecoderName,
                _logger, ct);

            if (audioData != null)
            {
                WriteThemeFile(outputPath, audioData);
                _logger.Info("[LTE] 电影保存：{0}", outputPath);
            }
            else
            {
                _logger.Warn("[LTE] 电影提取失败：{0}", movie.Name);
            }
        }

        // ── Unified detection: silence → blackframe → fallback window ────

        /// <summary>
        /// Returns (extractStart, extractEnd).
        /// 1. silencedetect (audio only, ~2MB) — cheapest, runs first
        /// 2. blackdetect (video decode, ~200MB) — only if silence found nothing
        /// 3. fixed end window — no I/O, last resort
        /// Silence false positives (dialogue pauses) are filtered by minMusicSeconds:
        /// a pause at -35s leaving only 30s would barely qualify; the code picks
        /// the LATEST qualifying transition, which is closest to actual credits.
        /// </summary>
        private async Task<(double, double)?> DetectExtractRange(
            string mediaUrl, double totalDur, double creditsEnd,
            int lookbackSec, int minCreditsSeconds,
            bool useHwDecode, string hwDecoderName,
            bool allowFallbackWindow, int fallbackWindowSeconds,
            CancellationToken ct)
        {
            // 1. silencedetect — audio only, cheap (~2MB)
            double? musicStart = await FfmpegHelper.DetectMusicStartAsync(
                mediaUrl, totalDur, lookbackSec, minCreditsSeconds, _logger, ct);
            if (musicStart != null)
            {
                _logger.Info("[LTE] silencedetect 命中: {0:F1}s", musicStart);
                double end = Math.Min(musicStart.Value + 180, creditsEnd);
                return (musicStart.Value, end);
            }

            // 2. blackdetect — video decode, expensive (~200MB), only when silence found nothing
            double? blackStart = await FfmpegHelper.DetectCreditsStartAsync(
                mediaUrl, totalDur, lookbackSec, useHwDecode, hwDecoderName, _logger, ct);
            if (blackStart != null && (creditsEnd - blackStart.Value) >= minCreditsSeconds)
            {
                _logger.Info("[LTE] blackdetect 命中: {0:F1}s", blackStart);
                double end = Math.Min(blackStart.Value + 180, creditsEnd);
                return (blackStart.Value, end);
            }

            // 3. Fixed end window — no extra I/O, last resort
            if (allowFallbackWindow)
            {
                double fallbackStart = Math.Max(0, totalDur - fallbackWindowSeconds);
                _logger.Info("[LTE] 固定窗口: 最后 {0}s", fallbackWindowSeconds);
                return (fallbackStart, creditsEnd);
            }

            return null;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static void WriteThemeFile(string outputPath, byte[] data)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.WriteAllBytes(outputPath, data);
        }

        private async Task RunParallel<T>(
            List<T> items, LteOptions config,
            Func<T, LteOptions, CancellationToken, Task> action,
            IProgress<double> progress, double pctFrom, double pctTo,
            CancellationToken ct)
        {
            if (items.Count == 0) { progress.Report(pctTo); return; }

            int parallelism = Math.Max(1, Math.Min(config.MaxParallelism, items.Count));
            var sem   = new SemaphoreSlim(parallelism, parallelism);
            var tasks = new List<Task>();
            int done  = 0;

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                await sem.WaitAsync(ct);
                var captured = item;
                tasks.Add(Task.Run(async () =>
                {
                    try   { await action(captured, config, ct); }
                    catch (Exception ex) { _logger.Warn("[LTE] 处理错误: {0}", ex.Message); }
                    finally
                    {
                        sem.Release();
                        int d = Interlocked.Increment(ref done);
                        progress.Report(pctFrom + (pctTo - pctFrom) * d / items.Count);
                    }
                }, ct));

                if (config.ThrottleSeconds > 0 && parallelism == 1)
                    await Task.Delay(TimeSpan.FromSeconds(config.ThrottleSeconds), ct);
            }
            await Task.WhenAll(tasks);
        }

        private static HashSet<string> ParseLibraryScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) return null;
            var result = new HashSet<string>();
            foreach (var part in scope.Split(','))
            {
                var id = part.Trim();
                if (!string.IsNullOrEmpty(id)) result.Add(id);
            }
            return result.Count > 0 ? result : null;
        }

        private static string GetSeriesDir(LibraryDbHelper.EpisodeInfo ep)
        {
            if (!string.IsNullOrEmpty(ep.SeriesPath)) return ep.SeriesPath;
            if (string.IsNullOrEmpty(ep.Path)) return null;
            string seasonDir = Path.GetDirectoryName(ep.Path);
            string seriesDir = seasonDir != null ? Path.GetDirectoryName(seasonDir) : null;
            return string.IsNullOrEmpty(seriesDir) ? seasonDir : seriesDir;
        }

        private static string GetMovieDir(string moviePath)
        {
            if (File.Exists(moviePath))   return Path.GetDirectoryName(moviePath);
            if (Directory.Exists(moviePath)) return moviePath;
            return null;
        }

        /// <summary>
        /// Quick check: can we create files in this directory?
        /// Result cached per mount root (first 3 path segments) to avoid repeated I/O.
        /// </summary>
        private bool IsDirectoryWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;

            string cacheKey = GetMountRoot(dir);
            return _writableCache.GetOrAdd(cacheKey, key =>
            {
                string probe = Path.Combine(dir, ".lte_write_test");
                bool writable;
                try
                {
                    File.WriteAllBytes(probe, new byte[] { 0 });
                    File.Delete(probe);
                    writable = true;
                }
                catch
                {
                    writable = false;
                }

                if (!writable)
                    _logger.Info("[LTE] 挂载点不可写，将跳过该路径下所有项：{0}", key);

                return writable;
            });
        }

        private static string GetMountRoot(string path)
        {
            // Extract first 3 segments: /mnt/share2/115open → /mnt/share2/115open
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int take = Math.Min(3, parts.Length);
            return "/" + string.Join("/", parts, 0, take);
        }
    }
}
