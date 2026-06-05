using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

namespace LocalThemeExtractor;

public class LteOptions : EditableOptionsBase
{
	public override string EditorTitle => "本地主题曲提取器";

	[Browsable(false)]
	public List<EditorSelectOption> LibraryList { get; set; } = new List<EditorSelectOption>();


	[DisplayName("媒体库范围（空 = 全部）")]
	[Description("勾选要提取主题曲的媒体库。不勾选等同于处理全部。")]
	[SelectItemsSource("LibraryList")]
	[EditMultilSelect]
	public string LibraryScope { get; set; } = "";


	[DisplayName("并行线程数（1 = 顺序执行，较慢但稳定）")]
	[MinValue(1)]
	[MaxValue(8)]
	public int MaxParallelism { get; set; } = 1;


	[DisplayName("顺序执行时每项延迟（秒，0 = 不延迟）")]
	[MinValue(0)]
	[MaxValue(60)]
	public int ThrottleSeconds { get; set; } = 2;


	[DisplayName("已存在 theme.mp3 时重新提取（覆盖）")]
	public bool OverwriteExisting { get; set; }

	[DisplayName("提取片头（使用 Emby 已有的片头标记）")]
	public bool TvExtractIntro { get; set; } = true;


	[DisplayName("优先使用第几季（1 = 第 1 季，0 = 最早有标记的季）")]
	[MinValue(0)]
	[MaxValue(99)]
	public int TvPreferSeasonNumber { get; set; } = 1;


	[DisplayName("最短片头时长（秒），低于此值视为广告，跳过")]
	[MinValue(5)]
	[MaxValue(120)]
	public int TvMinIntroSeconds { get; set; } = 20;


	[DisplayName("无片头标记时，尝试检测片尾字幕黑帧并提取片尾音乐（回退）")]
	public bool TvFallbackToCredits { get; set; }

	[DisplayName("检测片尾字幕黑帧，提取片尾音乐（ffmpeg blackdetect）")]
	public bool MovieExtractCredits { get; set; } = true;


	[DisplayName("从片尾往前扫描多少秒（默认 600 = 最后 10 分钟）")]
	[MinValue(60)]
	[MaxValue(1800)]
	public int MovieCreditsLookbackSeconds { get; set; } = 600;


	[DisplayName("片尾最短时长（秒），低于此值不提取")]
	[MinValue(10)]
	[MaxValue(300)]
	public int MovieMinCreditsSeconds { get; set; } = 30;


	[DisplayName("黑帧检测无结果时，回退提取片尾固定窗口（适合无黑帧但有片尾曲的电影）")]
	public bool MovieFallbackToEndWindow { get; set; }

	[DisplayName("回退模式：提取最后多少秒（秒）")]
	[MinValue(30)]
	[MaxValue(300)]
	public int MovieFallbackWindowSeconds { get; set; } = 90;


	[DisplayName("启用硬件视频解码（电影片尾扫描时有效）")]
	public bool UseHwDecode { get; set; }

	[DisplayName("硬件解码器名称（nvdec / vaapi / videotoolbox / dxva2）")]
	public string HwDecoderName { get; set; } = "nvdec";


	[DisplayName("输出音频码率（kbps）")]
	[MinValue(64)]
	[MaxValue(320)]
	public int AudioBitrateKbps { get; set; } = 192;

}
