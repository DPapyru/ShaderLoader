using System.Runtime.InteropServices;
using log4net;
using Microsoft.Xna.Framework.Graphics;
using ShaderLoader.CompilerBackend;

namespace ShaderLoader;

/// <summary>
/// Shader Effect 加载、缓存和管理的统一入口。
/// 优先加载 .fxb 编译文件（无 Wine 依赖），回退到运行时 .fx 编译。
/// </summary>
public static class DynamicEffectLoader
{
    // Effect 缓存：key = 归一化的完整 .fx 路径
    private static readonly Dictionary<string, Effect> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 编译器后端实例（懒初始化）
    private static IShaderCompiler? _compiler;

    /// <summary>由 AutoShaderModSystem.Load() 注入，供 ShaderWatcher 等写日志到 client.log</summary>
    public static ILog? Logger { get; set; }

    /// <summary>
    /// 测试用途：允许覆盖编译器后端。
    /// </summary>
    internal static IShaderCompiler? CompilerOverride
    {
        get => _compiler;
        set => _compiler = value;
    }

    /// <summary>
    /// 测试用途：允许覆盖 Effect 工厂方法，避免需要 GraphicsDevice。
    /// 签名：Func&lt;byte[] compiledBytes, string fxPath, Effect?&gt;
    /// 返回 null 则回退到默认的 Effect 构造函数。
    /// </summary>
    internal static Func<byte[], string, Effect?>? EffectFactoryOverride { get; set; }

    /// <summary>
    /// 测试用途：暴露当前的编译器（必要时自动创建）。
    /// </summary>
    internal static IShaderCompiler GetCompiler()
    {
        if (_compiler != null)
            return _compiler;

        EnsureCompilerInitialized();
        return _compiler!;
    }

    /// <summary>
    /// 测试用途：替换当前的编译器。
    /// </summary>
    internal static void SetCompiler(IShaderCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    /// <summary>
    /// 根据 .fx 路径推导对应的 .fxb 路径。
    /// 如果 .fx 在 Source 子目录中，将 .fxb 放在 Source 的父目录中。
    /// 例如：
    ///   Effects/Source/LaserDistort.fx → Effects/LaserDistort.fxb
    ///   Effects/LaserDistort.fx       → Effects/LaserDistort.fxb
    /// </summary>
    private static string GetFxbPath(string fxPath)
    {
        string fullPath = Path.GetFullPath(fxPath);
        string dir = Path.GetDirectoryName(fullPath) ?? ".";
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);

        string dirName = Path.GetFileName(dir);
        string? parentDir = Path.GetDirectoryName(dir);

        // Strip "Source" subdirectory if present (case-insensitive)
        if (string.Equals(dirName, "Source", StringComparison.OrdinalIgnoreCase) && parentDir != null)
        {
            return Path.Combine(parentDir, nameWithoutExt + ".fxb");
        }

        return Path.Combine(dir, nameWithoutExt + ".fxb");
    }

    /// <summary>
    /// 加载（或从缓存获取）已编译的 Shader Effect。
    /// 加载策略（按优先级顺序）：
    ///   1. .fxb 文件存在 → 直接从 .fxb 加载（无需 Wine）
    ///   2. 编译器可用 → 编译 .fx → .fxb，保存 .fxb，加载
    ///   3. 两者不可用 → 抛出 PlatformNotSupportedException
    /// </summary>
    /// <param name="fxPath">.fx Shader 源文件路径。</param>
    /// <param name="profile">HLSL profile（默认："fx_2_0"）。</param>
    /// <returns>缓存或新编译的 Effect。</returns>
    /// <exception cref="ShaderCompilationException">编译失败时抛出。</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// 没有可用的编译器后端且无预编译 .fxb 时抛出。
    /// </exception>
    public static Effect Load(string fxPath, string profile = "fx_2_0")
    {
        ArgumentNullException.ThrowIfNull(fxPath);

        // 归一化路径以确保缓存一致性
        string fullPath = Path.GetFullPath(fxPath);

        // 优先检查缓存
        if (_cache.TryGetValue(fullPath, out Effect? cached))
        {
            return cached;
        }

        // Step 1: 优先加载 .fxb（无需编译器，无需 Wine）
        string fxbPath = GetFxbPath(fullPath);
        if (File.Exists(fxbPath))
        {
            Logger?.Info($"[DynamicEffectLoader] 加载预编译 .fxb: {fxbPath}");
            byte[] compiledBytes = File.ReadAllBytes(fxbPath);

            Effect? effect = EffectFactoryOverride?.Invoke(compiledBytes, fullPath);
            if (effect == null)
            {
                effect = CreateEffect(compiledBytes, fullPath);
            }

            _cache[fullPath] = effect;
            return effect;
        }

        // Step 2: 尝试编译 .fx（需要编译器/Wine）
        EnsureCompilerInitialized();
        if (_compiler == null)
        {
            throw new PlatformNotSupportedException(
                "No pre-compiled .fxb file found and no shader compiler is available. " +
                "Pre-compile shaders on a development machine by running with Wine available, " +
                "or include .fxb files in the mod package.");
        }

        Logger?.Info($"[DynamicEffectLoader] 编译 .fx → .fxb: {fxPath}");

        byte[] compiled;
        try
        {
            compiled = _compiler.Compile(fullPath, profile);
        }
        catch (ShaderCompilationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ShaderCompilationException(
                $"Unexpected error during compilation of '{fullPath}': {ex.Message}", ex);
        }

        // 保存 .fxb 文件以供后续运行直接加载
        try
        {
            string? dir = Path.GetDirectoryName(fxbPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(fxbPath, compiled);
            Logger?.Info($"[DynamicEffectLoader] .fxb 已保存: {fxbPath}");
        }
        catch (Exception ex)
        {
            Logger?.Warn($"[DynamicEffectLoader] 无法保存 .fxb '{fxbPath}': {ex.Message}");
        }

        // 创建 Effect（可经过测试覆盖）
        Effect? effect2 = EffectFactoryOverride?.Invoke(compiled, fullPath);
        if (effect2 == null)
        {
            effect2 = CreateEffect(compiled, fullPath);
        }

        _cache[fullPath] = effect2;
        return effect2;
    }

    /// <summary>
    /// 移除缓存的 Effect 并释放其资源。
    /// </summary>
    /// <param name="fxPath">Load() 时使用的 .fx 文件路径。</param>
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
    /// 释放并移除所有缓存的 Effect。
    /// </summary>
    public static void UnloadAll()
    {
        foreach (Effect effect in _cache.Values)
        {
            effect.Dispose();
        }

        _cache.Clear();
    }

    /// <summary>
    /// 加载 Effect 并附带文件系统监控以支持自动热重载。
    /// Watcher 监控 .fx 文件的变更，在 500ms 防抖后自动：
    ///   1. 编译 .fx → .fxb（如果编译器可用）
    ///   2. 清除缓存
    ///   3. 重新加载（此时会命中新的 .fxb）
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
                string fxb = GetFxbPath(full);

                // 热重载：先编译 .fx → .fxb（如果编译器可用）
                EnsureCompilerInitialized();
                if (_compiler != null)
                {
                    try
                    {
                        byte[] compiled = _compiler.Compile(full, profile);
                        string? dir = Path.GetDirectoryName(fxb);
                        if (dir != null) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(fxb, compiled);
                        Logger?.Info($"[DynamicEffectLoader] 热重载编译 .fxb: {fxb}");
                    }
                    catch (Exception ex)
                    {
                        Logger?.Warn(
                            $"[DynamicEffectLoader] 热重载编译失败: {ex.Message}。" +
                            "将回退到现有 .fxb（如果存在）。");
                    }
                }

                // 清除缓存并重新加载（此时将命中 .fxb）
                _cache.Remove(full);
                return Load(path, profile);
            },
            debounceMs: 500
        );
        watcher.Start();
        return (effect, watcher);
    }

    /// <summary>
    /// 根据当前平台初始化编译器后端。
    /// Windows + D3DCompiler 可用 → WindowsD3DCompiler。
    /// 否则 → 尝试 Wine 后端。
    /// </summary>
    private static void EnsureCompilerInitialized()
    {
        if (_compiler != null)
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var d3dCompiler = new WindowsD3DCompiler();
            if (d3dCompiler.IsAvailable)
            {
                _compiler = d3dCompiler;
                return;
            }
        }

        // 非 Windows 或 Windows 没有 D3DCompiler → 尝试 Wine 后端
        if (WineFxCompiler.IsAvailable)
        {
            _compiler = new WineFxCompiler();
            return;
        }

        // 没有可用的编译器 - Load() 会抛出带有明确信息的异常
    }

    /// <summary>
    /// 从编译后的 Shader 字节码创建 Effect。
    /// 生产环境需要 GraphicsDevice；使用前通过 SetGraphicsDevice() 设置。
    /// </summary>
    private static Effect CreateEffect(byte[] compiledBytes, string name)
    {
        throw new NotSupportedException(
            $"Cannot create Effect '{name}' without a GraphicsDevice. " +
            "Set DynamicEffectLoader.GraphicsDevice during mod initialization, " +
            "or use EffectFactoryOverride for testing.");
    }
}