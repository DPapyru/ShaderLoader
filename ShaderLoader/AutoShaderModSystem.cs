using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Terraria;
using Terraria.ModLoader;

namespace ShaderLoader;

/// <summary>
/// 自动加载 [AutoLoadShader] 标记的 static Effect 字段。
/// DEBUG：fxc.exe 编译 .fx → 保存 .fxb → new Effect()，支持热重载
/// RELEASE：从 .tmod 读取预编译的 .fxb → new Effect()
/// </summary>
public sealed class AutoShaderModSystem : ModSystem
{
#if DEBUG
    public static bool EnableWatcher { get; set; } = true;
    private static readonly List<IDisposable> _watchers = [];
#endif

    private static List<(FieldInfo field, AutoLoadShaderAttribute attr)> _scannedFields = [];

    internal static List<(FieldInfo field, AutoLoadShaderAttribute attr)> ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var results = new List<(FieldInfo field, AutoLoadShaderAttribute attr)>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!typeof(Effect).IsAssignableFrom(field.FieldType))
                    continue;
                var attr = field.GetCustomAttribute<AutoLoadShaderAttribute>();
                if (attr == null) continue;
                results.Add((field, attr));
            }
        }
        return results;
    }

    public override void Load()
    {
        _scannedFields = ScanAssembly(GetType().Assembly);

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

        DynamicEffectLoader.Logger = Mod.Logger;

#if DEBUG
        if (!string.IsNullOrEmpty(Mod.SourceFolder))
        {
            string fxcPath = Path.GetFullPath(
                Path.Combine(Mod.SourceFolder, "ShaderLoader", "fxc", "fxc.exe"));
            if (File.Exists(fxcPath))
            {
                FxEffectCompiler.SetFxcPath(fxcPath);
                Mod.Logger.Info($"[AutoShader] fxc.exe: {fxcPath}");
            }
            else
            {
                Mod.Logger.Warn("[AutoShader] fxc.exe 未找到！请将 fxc.exe 放入 ShaderLoader/fxc/ 目录。");
            }
        }
#endif
    }

    public override void PostSetupContent()
    {
        var mod = this.Mod;

        foreach (var (field, attr) in _scannedFields)
        {
            try
            {
#if DEBUG
                // === DEBUG：编译 .fx → 保存 .fxb → new Effect()，支持热重载 ===
                string diskPath = Path.GetFullPath(
                    Path.Combine(mod.SourceFolder!, attr.FxPath));

                if (!File.Exists(diskPath))
                {
                    mod.Logger.Warn($"[AutoShader] .fx 未找到: {diskPath}");
                    continue;
                }

                if (EnableWatcher)
                {
                    var (effect, watcher) = DynamicEffectLoader.LoadWithWatcher(
                        diskPath,
                        newEffect =>
                        {
                            field.SetValue(null, newEffect);
                            mod.Logger.Info($"[AutoShader] 热重载: {Path.GetFileName(diskPath)}");
                        }
                    );
                    field.SetValue(null, effect);
                    if (watcher != null)
                    {
                        _watchers.Add(watcher);
                        mod.Logger.Info($"[AutoShader] 已加载 + 热重载: {Path.GetFileName(diskPath)}");
                    }
                }
                else
                {
                    var effect = DynamicEffectLoader.Load(diskPath);
                    field.SetValue(null, effect);
                    mod.Logger.Info($"[AutoShader] 已加载: {Path.GetFileName(diskPath)}");
                }
#else
                // === RELEASE：从 .tmod 提取 .fxb → new Effect() ===
                string cacheDir = Path.Combine(
                    Main.SavePath, "ModCache", "Shaders", mod.Name);
                string diskPath = Path.GetFullPath(Path.Combine(
                    cacheDir, attr.FxPath.Replace('/', Path.DirectorySeparatorChar)));

                string fxbAsset = Path.ChangeExtension(attr.FxPath, ".fxb");
                string fxbDisk = Path.ChangeExtension(diskPath, ".fxb");

                // 从 .tmod 提取 .fxb
                if (!File.Exists(fxbDisk) && mod.FileExists(fxbAsset))
                {
                    string? dir = Path.GetDirectoryName(fxbDisk);
                    if (dir != null) Directory.CreateDirectory(dir);

                    byte[] fxbBytes = mod.GetFileBytes(fxbAsset);
                    File.WriteAllBytes(fxbDisk, fxbBytes);
                }

                if (!File.Exists(fxbDisk))
                {
                    mod.Logger.Warn($"[AutoShader] .fxb 未找到: {fxbAsset}（请先在 DEBUG 下构建生成 .fxb）");
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(fxbDisk);
                var effect = DynamicEffectLoader.EffectFactoryOverride!.Invoke(bytes, fxbDisk)
                    ?? throw new InvalidOperationException("EffectFactoryOverride 返回 null");

                field.SetValue(null, effect);
                mod.Logger.Info($"[AutoShader] 已加载: {Path.GetFileName(fxbDisk)}");
#endif
            }
            catch (Exception ex)
            {
                mod.Logger.Error(
                    $"[AutoShader] 加载失败 '{attr.FxPath}' " +
                    $"(字段: {field.DeclaringType?.Name}.{field.Name}): {ex.Message}");
            }
        }
    }

    public override void Unload()
    {
#if DEBUG
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
#endif

        DynamicEffectLoader.UnloadAll();
        _scannedFields.Clear();
    }
}
