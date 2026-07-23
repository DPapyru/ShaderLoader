using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using ShaderLoader.CompilerBackend;
using Terraria;
using Terraria.ModLoader;

namespace ShaderLoader;

/// <summary>
/// 自动加载 [AutoLoadShader] 标记的 static Effect 字段。
/// Load() 中扫描当前 Mod 程序集，PostSetupContent() 中编译并赋值，Unload() 中释放资源。
/// 支持开发模式（ModSources 磁盘路径）和发布模式（.tmod 档案缓存）两种路径解析。
/// </summary>
public sealed class AutoShaderModSystem : ModSystem
{
    // 全局热重载模式开关（开发者可通过配置文件或条件编译控制）
    public static bool EnableWatcher { get; set; } = true;

    // 扫描结果缓存：字段 + 对应的 Attribute
    private static List<(FieldInfo field, AutoLoadShaderAttribute attr)> _scannedFields = [];

    // 启动的 ShaderWatcher 列表（用于热重载）
    private static readonly List<IDisposable> _watchers = [];

    /// <summary>
    /// 解析 [AutoLoadShader] 的 .fx 路径为可用的磁盘路径。
    /// 开发模式（sourceFolder != null）返回 ModSources 下的绝对路径；
    /// 发布模式返回 <paramref name="cacheBaseDir"/> 下的缓存路径。
    /// </summary>
    /// <param name="fxPath">属性中标记的路径（如 "Effects/Source/DistortShader.fx"）</param>
    /// <param name="sourceFolder">Mod 源码目录（开发模式），null 表示发布模式</param>
    /// <param name="cacheBaseDir">缓存基础目录（发布模式必传）</param>
    /// <returns>规范化磁盘路径</returns>
    internal static string ResolveShaderPath(
        string fxPath,
        string? sourceFolder,
        string? cacheBaseDir = null)
    {
        if (sourceFolder != null)
        {
            // 开发模式：相对于 Mod 源码目录
            return Path.GetFullPath(Path.Combine(sourceFolder, fxPath));
        }

        // 发布模式：写入缓存目录
        ArgumentNullException.ThrowIfNull(cacheBaseDir);
        return Path.GetFullPath(Path.Combine(
            cacheBaseDir,
            fxPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// 扫描指定程序集中所有 [AutoLoadShader] 标记的 public static Effect 字段。
    /// </summary>
    internal static List<(FieldInfo field, AutoLoadShaderAttribute attr)> ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var results = new List<(FieldInfo field, AutoLoadShaderAttribute attr)>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                // 只处理 Effect 类型的字段
                if (!typeof(Effect).IsAssignableFrom(field.FieldType))
                    continue;

                var attr = field.GetCustomAttribute<AutoLoadShaderAttribute>();
                if (attr == null)
                    continue;

                results.Add((field, attr));
            }
        }

        return results;
    }

    public override void Load()
    {
        // 开发模式：为 WineFxCompiler 设置 fxcompile.exe 路径（Linux/macOS）
        if (Mod.SourceFolder != null)
        {
            string fxCompilePath = Path.Combine(
                Mod.SourceFolder, "Effects", "fxcompile", "fxcompile.exe");
            if (File.Exists(fxCompilePath))
                WineFxCompiler.ExplicitExePath = fxCompilePath;
        }

        // 设置 EffectFactoryOverride，使 DynamicEffectLoader 能在运行时创建 Effect
        // FNA（Linux/macOS）要求 Effect 创建在主线程，用 QueueMainThreadAction 编组
        if (DynamicEffectLoader.EffectFactoryOverride == null)
        {
            DynamicEffectLoader.EffectFactoryOverride = (bytes, _) =>
            {
                Effect result = null;
                var done = new ManualResetEvent(false);
                Main.QueueMainThreadAction(() =>
                {
                    result = new Effect(Main.instance.GraphicsDevice, bytes);
                    done.Set();
                });
                done.WaitOne();
                return result;
            };
        }

        // 设置日志，供 ShaderWatcher/DynamicEffectLoader 写 client.log
        DynamicEffectLoader.Logger = Mod.Logger;

        // 扫描当前 Mod 程序集中的标记字段
        var assembly = GetType().Assembly;
        _scannedFields = ScanAssembly(assembly);
    }

    public override void PostSetupContent()
    {
        var mod = this.Mod;
        bool isDevMode = mod.SourceFolder != null;

        // 遍历扫描结果，加载每个 Effect 并反射赋值
        foreach (var (field, attr) in _scannedFields)
        {
            string diskPath;

            if (isDevMode)
            {
                // 开发模式：相对于 ModSources 磁盘路径，支持 FileSystemWatcher
                diskPath = ResolveShaderPath(attr.FxPath, mod.SourceFolder);
            }
            else
            {
                // 发布模式：从 .tmod 档案提取 .fx 到缓存目录
                string cacheDir = Path.Combine(Main.SavePath, "ModCache", "Shaders", mod.Name);
                diskPath = ResolveShaderPath(attr.FxPath, null, cacheDir);

                // 确保缓存目录存在
                string? dir = Path.GetDirectoryName(diskPath);
                if (dir != null) Directory.CreateDirectory(dir);

                // 仅在缓存未命中时从档案提取
                if (!File.Exists(diskPath))
                {
                    byte[] fxBytes = mod.GetFileBytes(attr.FxPath);
                    File.WriteAllBytes(diskPath, fxBytes);
                }
            }

            if (!File.Exists(diskPath))
            {
                throw new FileNotFoundException(
                    $"[AutoShaderModSystem] .fx 文件未找到: '{diskPath}' " +
                    $"(字段: {field.DeclaringType?.Name}.{field.Name})");
            }

            if (EnableWatcher && isDevMode)
            {
                // 开发模式：启动热重载监控
                var (effect, watcher) = DynamicEffectLoader.LoadWithWatcher(
                    diskPath,
                    newEffect =>
                    {
                        field.SetValue(null, newEffect);
                        mod.Logger.Info($"[AutoShader] 热重载完成: {Path.GetFileName(diskPath)}");
                    }
                );
                field.SetValue(null, effect);

                if (watcher != null)
                {
                    _watchers.Add(watcher);
                    mod.Logger.Info($"[AutoShader] 已加载 + 热重载监控: {Path.GetFileName(diskPath)}");
                }
            }
            else
            {
                // 发布模式或 watcher 关闭：普通加载
                var effect = DynamicEffectLoader.Load(diskPath);
                field.SetValue(null, effect);
                mod.Logger.Info($"[AutoShader] 已加载（无热重载）: {Path.GetFileName(diskPath)}");
            }
        }
    }

    public override void Unload()
    {
        // 停止所有 watcher
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();

        // 释放所有动态加载的 Effect 资源
        DynamicEffectLoader.UnloadAll();

        // 清除扫描缓存
        _scannedFields.Clear();
    }
}
