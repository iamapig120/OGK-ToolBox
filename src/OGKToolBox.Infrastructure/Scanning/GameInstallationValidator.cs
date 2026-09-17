using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Scanning;

public sealed class GameInstallationValidator : IGameInstallationValidator
{
    public bool TryValidate(string rootPath, out GameInstallation? installation, out string? error)
    {
        installation = null;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            error = "请选择游戏 package 目录。";
            return false;
        }

        var fullPath = Path.GetFullPath(rootPath.Trim());
        var candidate = new GameInstallation(fullPath);
        if (!Directory.Exists(candidate.BaseGameDataPath))
        {
            error = "所选目录缺少 mu3_Data/StreamingAssets/GameData/A000。";
            return false;
        }

        if (!Directory.Exists(candidate.BaseAssetsPath))
        {
            error = "所选目录缺少 mu3_Data/StreamingAssets/assets。";
            return false;
        }

        installation = candidate;
        error = null;
        return true;
    }
}
