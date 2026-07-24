using System;

namespace ShaderLoader;

/// <summary>
/// 标记 static Effect 字段，声明自动加载的 .fx 文件路径。
/// 配合 AutoShaderModSystem 在 Mod 加载时自动完成 Effect 的编译、创建和赋值。
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class AutoLoadShaderAttribute : Attribute
{
    /// <summary>
    /// 相对于 Mod 源码目录的 .fx 文件路径。
    /// 使用 "/" 分隔符，不含 Mod 名称前缀。 例如: "Effects/Source/DistortShader.fx"
    /// </summary>
    public string FxPath { get; }

    /// <param name="fxPath">相对于 Mod 源码目录的 .fx 文件路径（例如 "Effects/Source/DistortShader.fx"）</param>
    public AutoLoadShaderAttribute(string fxPath)
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        FxPath = fxPath;
    }
}
