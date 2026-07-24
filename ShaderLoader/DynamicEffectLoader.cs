using log4net;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShaderLoader;

/// <summary>
/// Shader Effect 加载、缓存和管理的统一入口。
/// 加载策略：优先读 .fxb（预编译），无 .fxb 则用 fxc.exe 编译 .fx 并保存 .fxb。
/// </summary>
public static class DynamicEffectLoader
{
    private static readonly Dictionary<string, Effect> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ILog? Logger { get; set; }

    /// <summary>
    /// 由 AutoShaderModSystem 注入。FNA 要求 Effect 在主线程创建。
    /// </summary>
    internal static Func<byte[], string, Effect?>? EffectFactoryOverride { get; set; }

    /// <summary>
    /// 根据 .fx 路径推导对应的 .fxb 路径（同目录同名，换扩展名）。
    /// </summary>
    public static string GetFxbPath(string fxPath)
    {
        return Path.ChangeExtension(fxPath, ".fxb");
    }

    /// <summary>
    /// 加载 Shader Effect。
    /// 1. 检查缓存
    /// 2. .fxb 存在 → 直接加载
    /// 3. 编译 .fx → 保存 .fxb → 创建 Effect
    /// </summary>
    public static Effect Load(string fxPath)
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        string fullPath = Path.GetFullPath(fxPath);

        if (_cache.TryGetValue(fullPath, out Effect? cached))
            return cached;

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($".fx 未找到: {fullPath}");

        string fxbPath = GetFxbPath(fullPath);

        // 优先读预编译的 .fxb
        if (File.Exists(fxbPath))
        {
            Logger?.Info($"[DynamicEffectLoader] 加载 .fxb: {fxbPath}");
            byte[] fxbBytes = File.ReadAllBytes(fxbPath);
            Effect? effect = CreateEffect(fxbBytes, fullPath);
            _cache[fullPath] = effect;
            return effect;
        }

        // 编译 .fx → .fxb
        Logger?.Info($"[DynamicEffectLoader] 编译: {fullPath}");
        byte[] compiled = FxEffectCompiler.Compile(fullPath);

        // 保存 .fxb 供后续加载 / 发布模式使用
        try
        {
            File.WriteAllBytes(fxbPath, compiled);
            Logger?.Info($"[DynamicEffectLoader] .fxb 已保存: {fxbPath}");
        }
        catch (Exception ex)
        {
            Logger?.Warn($"[DynamicEffectLoader] 无法保存 .fxb: {ex.Message}");
        }

        Effect? effect2 = CreateEffect(compiled, fullPath);
        _cache[fullPath] = effect2;
        return effect2;
    }

    private static Effect CreateEffect(byte[] compiledBytes, string name)
    {
        Effect? effect = EffectFactoryOverride?.Invoke(compiledBytes, name);
        if (effect == null)
            throw new InvalidOperationException(
                "EffectFactoryOverride 返回 null。AutoShaderModSystem 必须在 Load() 之前设置。");
        return effect;
    }

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

    public static void UnloadAll()
    {
        foreach (var effect in _cache.Values)
            effect.Dispose();
        _cache.Clear();
    }

    /// <summary>
    /// 加载 Effect 并附带轮询热重载监控。
    /// 热重载时重新编译 .fx → 覆盖 .fxb → 清除缓存 → 重新加载。
    /// </summary>
    public static (Effect effect, IDisposable? watcher) LoadWithWatcher(
        string fxPath, Action<Effect> onReloaded)
    {
        Effect effect = Load(fxPath);
        var watcher = new ShaderWatcher(
            fxPath,
            onReloaded,
            (path) =>
            {
                string full = Path.GetFullPath(path);

                // 重新编译 → 覆盖 .fxb
                byte[] compiled = FxEffectCompiler.Compile(full);
                string fxbPath = GetFxbPath(full);
                try
                {
                    File.WriteAllBytes(fxbPath, compiled);
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"[DynamicEffectLoader] 热重载保存 .fxb 失败: {ex.Message}");
                }

                // 清除缓存并重新加载（此时命中新 .fxb）
                _cache.Remove(full);
                return Load(path);
            },
            debounceMs: 500
        );
        watcher.Start();
        return (effect, watcher);
    }
}
