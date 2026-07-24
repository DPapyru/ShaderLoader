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
/// 开发模式（ModSources）：用 fxc.exe 编译 .fx → .fxb → new Effect()，支持热重载
/// 发布模式：从 .xnb 加载 via ModContent.Request&lt;Effect&gt;
/// </summary>
public sealed class AutoShaderModSystem : ModSystem
{
    public static bool EnableWatcher { get; set; } = true;

    private static List<(FieldInfo field, AutoLoadShaderAttribute attr)> _scannedFields = [];
    private static readonly List<IDisposable> _watchers = [];

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

    private static string FxPathToAssetName(string fxPath)
    {
        return Path.ChangeExtension(fxPath, null).Replace('\\', '/');
    }

    private static string? FindFxcPath(string? sourceFolder = null)
    {
        string[] candidates =
        {
            // 模组源码目录下的 ShaderLoader/fxc/fxc.exe（最优先）
            sourceFolder != null ? Path.Combine(sourceFolder, "ShaderLoader", "fxc", "fxc.exe") : null,
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
                return Path.GetFullPath(c);
        }
        return null;
    }

    public override void Load()
    {
        // 扫描字段
        _scannedFields = ScanAssembly(GetType().Assembly);

        if (Mod.SourceFolder != null)
        {
            // 寻找 fxc.exe（优先从模组源码目录的 ShaderLoader/ 子目录读取）
            string? fxcPath = FindFxcPath(Mod.SourceFolder);
            if (fxcPath != null)
            {
                FxEffectCompiler.SetFxcPath(fxcPath);
                Mod.Logger.Info($"[AutoShader] fxc.exe: {fxcPath}");
            }
            else
            {
                Mod.Logger.Warn("[AutoShader] fxc.exe 未找到！Shader 编译将失败。请将 fxc.exe 放入模组的 ShaderLoader/ 目录。");
            }

            // 设置 EffectFactoryOverride: FNA 需要 Effect 在主线程创建
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

            DynamicEffectLoader.Logger = Mod.Logger;
        }
    }

    public override void PostSetupContent()
    {
        var mod = this.Mod;
        bool isDevMode = mod.SourceFolder != null;

        foreach (var (field, attr) in _scannedFields)
        {
            try
            {
                if (isDevMode)
                {
                    // === 开发模式：编译 .fx → .fxb → new Effect() ===
                    string fxDiskPath = Path.GetFullPath(
                        Path.Combine(mod.SourceFolder, attr.FxPath));

                    if (!File.Exists(fxDiskPath))
                    {
                        mod.Logger.Warn($"[AutoShader] .fx 未找到: {fxDiskPath}");
                        continue;
                    }

                    if (EnableWatcher)
                    {
                        // 热重载
                        var (effect, watcher) = DynamicEffectLoader.LoadWithWatcher(
                            fxDiskPath,
                            newEffect =>
                            {
                                field.SetValue(null, newEffect);
                                mod.Logger.Info($"[AutoShader] 热重载: {Path.GetFileName(fxDiskPath)}");
                            }
                        );
                        field.SetValue(null, effect);
                        if (watcher != null)
                        {
                            _watchers.Add(watcher);
                            mod.Logger.Info($"[AutoShader] 已加载 + 热重载: {Path.GetFileName(fxDiskPath)}");
                        }
                    }
                    else
                    {
                        var effect = DynamicEffectLoader.Load(fxDiskPath);
                        field.SetValue(null, effect);
                        mod.Logger.Info($"[AutoShader] 已加载: {Path.GetFileName(fxDiskPath)}");
                    }
                }
                else
                {
                    // === 发布模式：加载 .xnb via ModContent.Request ===
                    string assetName = FxPathToAssetName(attr.FxPath);
                    var asset = mod.Assets.Request<Effect>(assetName,
                        ReLogic.Content.AssetRequestMode.ImmediateLoad);
                    field.SetValue(null, asset.Value);
                    mod.Logger.Info($"[AutoShader] 已加载 (发布): {assetName}");
                }
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
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();

        DynamicEffectLoader.UnloadAll();
        _scannedFields.Clear();
    }
}
