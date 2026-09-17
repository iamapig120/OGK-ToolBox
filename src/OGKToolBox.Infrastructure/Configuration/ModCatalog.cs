namespace OGKToolBox.Infrastructure.Configuration;

internal sealed record ModCatalogEntry(string ChineseName, string Description);

/// <summary>
/// User-facing descriptions translated from the mu3-mods Wiki Mod list.
/// Unknown assemblies retain their technical name and receive a neutral description.
/// </summary>
internal static class ModCatalog
{
    private static readonly IReadOnlyDictionary<string, ModCatalogEntry> Entries =
        new Dictionary<string, ModCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["AttractVideoPlayer"] = new("禁用待机时视频展示", "禁用待机时的视频播放器。"),
            ["BetterGiveUp"] = new("快速放弃与重开", "红色菜单键返回选曲，黄色菜单键重开；需按住 1 秒以防误触。"),
            ["Blacklist"] = new("自制谱面成绩拦截", "阻止自制谱面的成绩上传。"),
            ["Inohara"] = new("成绩上传拦截兼容", "为 Inohara 相关功能提供成绩上传拦截范围设置。"),
            ["DisableEncryption"] = new("禁用 TLS 加密", "禁用 TLS。"),
            ["DisableGP"] = new("移除投币和更改 GP", "移除投币检查，并将 GP 固定为设定值。"),
            ["DisableMaintenance"] = new("禁用维护时段", "禁用维护时间限制。"),
            ["DisableTimers"] = new("禁用全部计时器", "禁用游戏中的全部计时器。"),
            ["ExclusiveAudio"] = new("独占音频兼容", "允许独占模式使用任意采样率，并可选用双声道独占音频。"),
            ["ForceEnableTournamentScoreboard"] = new("全模式技术分板", "在所有游玩模式下，强制启用通常仅在游戏设为赛事模式时显示的技术分记分板。"),
            ["FrameRate"] = new("帧率解锁与修复", "修复高刷新率或多显示器环境，并可调整或解除帧率限制。"),
            ["InfiniteStory"] = new("无限剧情次数", "取消每次投币只能游玩一段剧情的限制。"),
            ["JudgeUITweaks"] = new("判定计分板显示优化", "对游戏内判定计分板显示进行便捷性微调。"),
            ["LoadBoost"] = new("加速启动加载", "显著缩短游戏启动时间。"),
            ["ManualGC"] = new("谱面期间禁用垃圾回收", "谱面开始时强制关闭 Unity 的 C# 垃圾回收，谱面结束或强制返回运营菜单时恢复自动回收，以避免游戏中途 GC 引起的卡顿和掉帧。兼容 BetterGiveUp 和 Practice；检测到任意倒带时会手动执行一次回收。本功能仅处理 GC 导致的掉帧，不能解决其它性能问题。"),
            ["MoreProfileOptions"] = new("扩展档案选项", "增加 Rating 计算方式和曲目跳过条件选项。"),
            ["Pause"] = new("游戏暂停", "为 FN2／服务键添加暂停功能，内置 5 秒冷却。"),
            ["PlatinumTiming"] = new("白金判定早晚统计", "显示 Critical Break 的 Early／Late 次数。"),
            ["SelectBGM"] = new("选择主界面 BGM", "允许通过配置更换主界面主题曲。"),
            ["SkipCutscenes"] = new("跳过演出与奖励", "使用红色菜单键跳过战斗前后演出及登录奖励。"),
            ["SkipNotices"] = new("跳过安全提示与公告", "跳过安全警告画面和活动公告。"),
            ["SortByInternalDifficulty"] = new("按谱面定数排序", "增加按内部难度（谱面定数）分组和排序的选项。"),
            ["TestMenuConfig"] = new("测试菜单 Mod 配置", "在测试菜单中增加 Mod 配置入口。"),
            ["TestMenuScaling"] = new("测试模式分辨率修复", "修复非 1080×1920 分辨率下的测试模式和启动序列。"),
            ["UnlockAllMusic"] = new("解锁全部乐曲", "解锁全部乐曲。"),
            ["UnlockAndSetJewelBoostNine"] = new("解锁并设置宝石助力 X9", "解锁相关内容并将宝石助力设置为 X9。"),
            ["UnlockGameEvents"] = new("解锁游戏活动", "解锁游戏活动内容。"),
            ["UnlockMasterDifficulty"] = new("解锁 MASTER 难度", "解锁 MASTER 难度。"),
            ["UnlockMemoryChapters"] = new("解锁记忆章节", "解锁 Memory Chapters（记忆章节）。")
        };

    public static ModCatalogEntry Describe(string technicalName) => Entries.TryGetValue(technicalName, out var entry)
        ? entry
        : new(technicalName, "未在 mu3-mods Wiki 中找到该 Mod 的说明。");
}
