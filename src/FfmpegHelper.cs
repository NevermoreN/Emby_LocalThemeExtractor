using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LocalThemeExtractor
{
    internal static class FfmpegHelper
    {
        private static string _ffmpegPath;

        public static string FfmpegPath
        {
            get
            {
                if (_ffmpegPath != null) return _ffmpegPath;
                string[] candidates = {
                    "/app/emby/bin/ffmpeg",
                    "/usr/local/bin/ffmpeg",
                    "/usr/bin/ffmpeg",
                    "/app/ffmpeg",
                    "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                };
                foreach (var p in candidates)
                    if (File.Exists(p)) { _ffmpegPath = p; return p; }
                try
                {
                    var psi = new ProcessStartInfo("which", "ffmpeg")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    using (var proc = Process.Start(psi))
                    {
                        string r = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit();
                        if (proc.ExitCode == 0 && File.Exists(r)) { _ffmpegPath = r; return r; }
                    }
                }
                catch { }
                return null;
            }
        }

        // ── Pick a decodable audio stream ───────────────────────────────

        /// <summary>
        /// Audio-relative index of the first stream ffmpeg can actually decode.
        /// 国产 4K WEB-DL（QHstudIo/OurTV 等）常把 Audio Vivid（菁彩声，tag av3a）放在
        /// 第一条音轨，ffmpeg 无此解码器，-map 0:a:0 会以 "no decoder found for: none"
        /// 瞬间失败；ffprobe 里这类轨的 codec_name 是 unknown/空。跳过它们选第一条
        /// 正常轨。探测不到就返回 0（维持旧行为）。
        /// </summary>
        public static async Task<int> GetDecodableAudioIndexAsync(
            string sourceUrl, ILogger logger, CancellationToken ct)
        {
            string probe = FfmpegPath?.Replace("ffmpeg", "ffprobe");
            if (probe == null || !File.Exists(probe)) return 0;

            string args = string.Format(
                "-v error -select_streams a -show_entries stream=index,codec_name -of csv=p=0 \"{0}\"",
                sourceUrl);
            string output = await RunProcessCaptureStdoutAsync(probe, args, logger, ct);
            if (string.IsNullOrEmpty(output)) return 0;

            // 每行一条音频轨（"文件流号,codec_name"），行序即音频相对序。
            // 行首永远有流号，codec_name 缺失时为空 —— 以行计数保证索引对齐。
            int rel = 0;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                int comma = line.IndexOf(',');
                string codec = comma >= 0 ? line.Substring(comma + 1).Trim() : "";
                if (codec.Length > 0 &&
                    !codec.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                    !codec.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return rel;
                }
                rel++;
            }
            return 0;
        }

        // ── Extract audio to memory (pipe:1) ────────────────────────────

        /// <summary>
        /// Extract an audio segment and return as byte[] in memory.
        /// ffmpeg outputs MP3 to stdout (pipe:1), no temp files written.
        /// </summary>
        public static async Task<byte[]> ExtractAudioToMemoryAsync(
            string sourceUrl,
            double startSec,
            double endSec,
            int bitrateKbps,
            bool useHwDecode,
            string hwDecoderName,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(FfmpegPath))
            {
                logger.Warn("[LTE] ffmpeg not found");
                return null;
            }

            double duration = endSec - startSec;
            if (duration <= 0)
            {
                logger.Warn("[LTE] Invalid segment: start={0} end={1}", startSec, endSec);
                return null;
            }

            // pipe:1 = output to stdout, -f mp3 = force MP3 format
            // -map 0:a instead of -vn: tells demuxer to only read audio packets,
            // saving bandwidth on CloudDrive (avoids reading interleaved video data)
            // No -hwaccel needed: audio-only extraction has no video decode
            // -ar 48000: 绝大多数影视音轨本身就是 48kHz，保持原采样率，避免多做一次
            // 48k→44.1k 的有损重采样
            int aIdx = await GetDecodableAudioIndexAsync(sourceUrl, logger, ct);
            if (aIdx > 0)
                logger.Info("[LTE] 首音轨不可解码（av3a 菁彩声等），改用音轨 {0}", aIdx);
            string args = string.Format(CultureInfo.InvariantCulture,
                "-ss {0:F3} -i \"{1}\" -t {2:F3} -map 0:a:{4} -acodec libmp3lame -ab {3}k -ar 48000 -f mp3 -y pipe:1",
                startSec, sourceUrl, duration, bitrateKbps, aIdx);

            logger.Info("[LTE] ffmpeg pipe extract: {0}s-{1}s from {2}", startSec, endSec, sourceUrl);
            return await RunFfmpegPipeAsync(args, logger, ct);
        }

        // ── Silence detection (audio only, very low bandwidth) ──────────

        public static async Task<double?> DetectMusicStartAsync(
            string sourceUrl,
            double totalDurationSec,
            int lookbackSec,
            double minMusicSeconds,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(FfmpegPath)) return null;

            double scanStart = Math.Max(0, totalDurationSec - lookbackSec);

            // silencedetect 需要真解码，同样必须避开 av3a 等不可解码的首音轨
            int aIdx = await GetDecodableAudioIndexAsync(sourceUrl, logger, ct);
            string args = string.Format(CultureInfo.InvariantCulture,
                "-ss {0:F0} -i \"{1}\" -map 0:a:{2} -af \"silencedetect=noise=-35dB:d=1.5\" -f null -",
                scanStart, sourceUrl, aIdx);

            logger.Info("[LTE] silencedetect (audio only, last {0}s): {1}", lookbackSec, sourceUrl);

            string stderr = await RunFfmpegCaptureStderrAsync(args, logger, ct);
            if (stderr == null) return null;

            var regex = new Regex(@"silence_end:\s*(\d+\.?\d*)", RegexOptions.IgnoreCase);
            double? bestMusicStart = null;

            foreach (Match m in regex.Matches(stderr))
            {
                double relSec = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double absSec = scanStart + relSec;

                if ((totalDurationSec - 5 - absSec) >= minMusicSeconds)
                {
                    if (bestMusicStart == null || absSec > bestMusicStart)
                        bestMusicStart = absSec;
                }
            }

            if (bestMusicStart != null)
                logger.Info("[LTE] silence -> music at {0:F1}s (remaining {1:F0}s)",
                    bestMusicStart, totalDurationSec - bestMusicStart);
            else
                logger.Info("[LTE] no silence transition found (last {0}s)", lookbackSec);

            return bestMusicStart;
        }

        // ── Black-frame detection (needs video decode, high bandwidth) ──

        public static async Task<double?> DetectCreditsStartAsync(
            string sourceUrl,
            double totalDurationSec,
            int lookbackSec,
            bool useHwDecode,
            string hwDecoderName,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(FfmpegPath)) return null;

            double scanStart = Math.Max(0, totalDurationSec - lookbackSec);

            string hwArg = useHwDecode && !string.IsNullOrEmpty(hwDecoderName)
                ? string.Format("-hwaccel {0} ", hwDecoderName)
                : "";

            string args = string.Format(CultureInfo.InvariantCulture,
                "{0}-ss {1:F0} -i \"{2}\" -vf \"blackdetect=d=0.5:pix_th=0.10\" -an -f null -",
                hwArg, scanStart, sourceUrl);

            logger.Info("[LTE] blackdetect (video, last {0}s): {1}", lookbackSec, sourceUrl);

            string stderr = await RunFfmpegCaptureStderrAsync(args, logger, ct);
            if (stderr == null) return null;

            var regex = new Regex(@"black_start:(\d+\.?\d*)", RegexOptions.IgnoreCase);
            double? firstBlackAbsolute = null;

            foreach (Match m in regex.Matches(stderr))
            {
                double relSec = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double absSec = scanStart + relSec;

                if (firstBlackAbsolute == null || absSec < firstBlackAbsolute)
                    firstBlackAbsolute = absSec;
            }

            if (firstBlackAbsolute != null)
                logger.Info("[LTE] black frame at {0:F1}s", firstBlackAbsolute);
            else
                logger.Info("[LTE] no black frame (last {0}s)", lookbackSec);

            return firstBlackAbsolute;
        }

        // ── Probe duration ───────────────────────────────────────────────

        public static async Task<double?> ProbeDurationAsync(
            string sourceUrl, ILogger logger, CancellationToken ct)
        {
            string probe = FfmpegPath?.Replace("ffmpeg", "ffprobe");
            if (probe != null && File.Exists(probe))
            {
                string args = string.Format(
                    "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{0}\"",
                    sourceUrl);
                string output = await RunProcessCaptureStdoutAsync(probe, args, logger, ct);
                if (output != null && double.TryParse(output.Trim(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                {
                    return d;
                }
            }

            if (FfmpegPath == null) return null;
            // Only read container headers (no decoding), exit immediately after parsing metadata
            string stderr = await RunFfmpegCaptureStderrAsync(
                string.Format("-i \"{0}\" -map 0:a -t 0 -f null -", sourceUrl), logger, ct);
            if (stderr == null) return null;

            var m = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+\.?\d*)");
            if (m.Success)
            {
                double h   = double.Parse(m.Groups[1].Value);
                double min = double.Parse(m.Groups[2].Value);
                double sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return h * 3600 + min * 60 + sec;
            }
            return null;
        }

        // ── Private: pipe stdout as binary ───────────────────────────────

        private static async Task<byte[]> RunFfmpegPipeAsync(
            string args, ILogger logger, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                using (var ms = new MemoryStream())
                {
                    // Start draining both streams concurrently
                    var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct);
                    var stderrTask = proc.StandardError.ReadToEndAsync();

                    // Wait for process exit FIRST (has timeout).
                    // When process exits, stdout closes → copyTask completes.
                    // If process hangs, timeout fires → we kill it → stdout closes.
                    bool exited = await WaitForExitAsync(proc, 300000, ct);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        logger.Warn("[LTE] ffmpeg pipe timed out");
                        return null;
                    }

                    // Process exited, stdout is closed, copyTask should finish immediately
                    try { await copyTask; } catch { }

                    if (proc.ExitCode != 0)
                    {
                        string err = await stderrTask;
                        try { File.WriteAllText("/config/data/lte-ffmpeg-last-error.txt", err); } catch { }
                        string errTail = err.Length > 500 ? err.Substring(err.Length - 500) : err;
                        logger.Warn("[LTE] ffmpeg pipe exit {0}: {1}", proc.ExitCode, errTail);
                        return null;
                    }

                    byte[] data = ms.ToArray();
                    if (data.Length < 1000)
                    {
                        logger.Warn("[LTE] ffmpeg output too small ({0} bytes)", data.Length);
                        return null;
                    }
                    logger.Info("[LTE] ffmpeg pipe OK: {0} bytes", data.Length);
                    return data;
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("[LTE] ffmpeg pipe error", ex, Array.Empty<object>());
                return null;
            }
        }

        // ── Private: capture stderr ──────────────────────────────────────

        private static async Task<string> RunFfmpegCaptureStderrAsync(
            string args, ILogger logger, CancellationToken ct)
        {
            return await RunProcessCaptureStderrAsync(FfmpegPath, args, logger, ct);
        }

        private static async Task<string> RunProcessCaptureStderrAsync(
            string binary, string args, ILogger logger, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();

                    bool exited = await WaitForExitAsync(proc, 600000, ct);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }
                    return await stderrTask;
                }
            }
            catch (Exception ex)
            {
                logger.Debug("[LTE] process error: {0}", ex.Message);
                return null;
            }
        }

        private static async Task<string> RunProcessCaptureStdoutAsync(
            string binary, string args, ILogger logger, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    proc.StandardError.ReadToEndAsync();

                    bool exited = await WaitForExitAsync(proc, 120000, ct);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }
                    return await stdoutTask;
                }
            }
            catch (Exception ex)
            {
                logger.Debug("[LTE] process error: {0}", ex.Message);
                return null;
            }
        }

        private static Task<bool> WaitForExitAsync(Process proc, int timeoutMs, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) => tcs.TrySetResult(true);
            if (proc.HasExited) tcs.TrySetResult(true);

            var reg = ct.Register(() =>
            {
                try { proc.Kill(); } catch { }
                tcs.TrySetResult(false);
            });

            Task.Delay(timeoutMs).ContinueWith(_ => tcs.TrySetResult(false));

            return tcs.Task.ContinueWith(t => { reg.Dispose(); return t.Result; });
        }
    }
}
