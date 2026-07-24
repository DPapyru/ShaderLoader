using log4net;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShaderLoader;

/// <summary>
/// Shader Effect 加载、缓存和管理的统一入口。
/// 开发模式：用 fxc.exe 编译 .fx → .fxb，然后通过 EffectFactoryOverride 创建 Effect。
/// 缓存基于 .fx 文件路径。
/// </summary>
public static class DynamicEffectLoader
{
    private static readonly Dictionary<string, Effect> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ILog? Logger { get; set; }

    /// <summary>
    /// 由 AutoShaderModSystem 注入的工厂方法。FNA 要求 Effect 在主线程创建。
    /// 签名：Func&lt;byte[] fxbBytes, string fxPath, Effect?&gt;
    /// </summary>
    internal static Func<byte[], string, Effect?>? EffectFactoryOverride { get; set; }

    /// <summary>
    /// 编译并加载 .fx Shader Effect。
    /// 1. 用 fxc.exe 编译 .fx → .fxb
    /// 2. 通过 EffectFactoryOverride 创建 Effect
    /// </summary>
    public static Effect Load(string fxPath, string profile = "fx_2_0")
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        string fullPath = Path.GetFullPath(fxPath);

        if (_cache.TryGetValue(fullPath, out Effect? cached))
            return cached;

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($".fx 未找到: {fullPath}");

        // 编译 .fx → .fxb
        Logger?.Info($"[AutoShader] 编译: {fullPath}");
        byte[] fxbBytes = FxEffectCompiler.Compile(fullPath);

        // 创建 Effect
        Effect? effect = EffectFactoryOverride?.Invoke(fxbBytes, fullPath);
        if (effect == null)
            throw new InvalidOperationException("EffectFactoryOverride 返回 null。AutoShaderModSystem 必须在 Load() 中设置。");

        _cache[fullPath] = effect;
        return effect;
    }

    /// <summary>
    /// 卸载并释放单个缓存的 Effect。
    /// </summary>
    public static void Unload(string fxPath)
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        string fullPath = Path.GetFullPath(fxPath);
        if (_cache.TryGetValue(fullPath, out Effect? effect))
        {
            _cache.Remove(fullPath);
            effect.Dispose();
        }
    }

    /// <summary>
    /// 卸载并释放所有缓存的 Effect。
    /// </summary>
    public static void UnloadAll()
    {
        foreach (var effect in _cache.Values)
            effect.Dispose();
        _cache.Clear();
    }

    /// <summary>
    /// 加载 Effect 并附带文件系统监控以支持自动热重载。
    /// 修改 .fx 源文件后自动重新编译并调用 onReloaded 回调。
    /// Watcher 监控 .fx 文件的变更，在 500ms 防抖后自动：
    ///   1. 清除缓存
    ///   2. 重新编译 .fx 并通过 EffectFactoryOverride 创建新 Effect
    /// </summary>
    /// <param name="fxPath">.fx Shader 源文件路径。</param>
    /// <param name="onReloaded">每次成功重新编译后的回调。</param>
    /// <param name="profile">HLSL profile（默认："fx_2_0"）。</param>
    /// <returns>
    /// 包含已加载的 Effect 和可销毁的 <see cref="ShaderWatcher"/> 的元组。
    /// 销毁 watcher 以停止文件监控。
    /// </returns>
    public static (Effect effect, IDisposable? watcher) LoadWithWatcher(
        string fxPath, Action<Effect> onReloaded, string profile = "fx_2_0")
    {
        Effect effect = Load(fxPath, profile);
        var watcher = new ShaderWatcher(
            fxPath,
            onReloaded,
            (path) =>
            {
                string full = Path.GetFullPath(path);

                // 清除缓存并重新加载（Load 会重新调用 fxc.exe 编译 .fx）
                _cache.Remove(full);
                return Load(path, profile);
            },
            debounceMs: 500
        );
        watcher.Start();
        return (effect, watcher);
    }
}
