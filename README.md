# ShaderLoader

> 基于 [DPapyru/ShaderLoader](https://github.com/DPapyru/ShaderLoader) 的 Windows 适配版。
> 原版面向 Linux/macOS，通过 Wine + D3DCompiler 实现跨平台 shader 编译；
> 此版本使用 Windows 原生 fxc.exe，并改为 DEBUG/RELEASE 模式控制热重载/编译逻辑。

tModLoader Mod 专用的 HLSL Shader 动态编译与热重载模块。嵌入到你的 Mod 中即可使用。

## 功能

- **运行时编译** — 自动将 `.fx` 编译为 `Effect`，无需手动预处理
- **热重载** — 修改 `.fx` 后自动重新编译，游戏内即时生效
- **预编译缓存** — 编译后保存 `.fxb`，后续启动直接加载，无需重新编译
- **发布友好** — Release 构建不含 fxc.exe，玩家只需 `.fxb` 文件

## 嵌入到你的模组

将整个 `ShaderLoader/` 目录复制到你的 Mod 项目根目录下，SDK 风格的 `.csproj` 会自动编译：

```
YourMod/
├── ShaderLoader/
│   ├── fxc/
│   │   ├── fxc.exe              # Windows HLSL 编译器
│   │   └── d3dcompiler_47.dll   # fxc 依赖
│   ├── AutoLoadShaderAttribute.cs
│   ├── AutoShaderModSystem.cs
│   ├── DynamicEffectLoader.cs
│   ├── FxEffectCompiler.cs
│   └── ShaderWatcher.cs
├── Effects/
│   └── MyShader.fx
└── YourMod.cs
```

## 使用

在任意 `static Effect` 字段上标记 `[AutoLoadShader]`：

```csharp
using ShaderLoader;
using Microsoft.Xna.Framework.Graphics;

public class MyMod : Mod
{
    [AutoLoadShader("Effects/MyShader.fx")]
    public static Effect MyShader;
}
```

`AutoShaderModSystem` 在 `PostSetupContent()` 中自动编译、赋值、启动热重载。

## 工作原理

```
DEBUG (Mod Sources 构建):

  Effects/MyShader.fx ──fxc.exe──→ Effects/MyShader.fxb ──→ new Effect()
                                           │
                                   下次启动直接读 .fxb（快）
                                   修改 .fx → 自动热重载

RELEASE (.tmod 构建):

  从 .tmod 提取 .fxb → new Effect()
  （不需要 fxc.exe，不需要源码）
```

## 添加 Shader 文件

1. 在 `Effects/` 下新建 `.fx` 文件，编写 HLSL effect（需包含 `technique` / `pass` / `compile` 块）
2. 在对应类中用 `[AutoLoadShader("Effects/xxx.fx")]` 标记 `static Effect` 字段
3. `Build + Reload`（DEBUG 下会自动编译生成 `.fxb`）

`.fx` 示例：

```hlsl
float uTime;
float3 color;

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    return float4(color, 1.0);
}

technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_3_0 main();
    }
}
```

## 注意事项

- 热重载会直接监控源码目录，如果不想要 `ShaderLoader/fxc/` 下的编译工具在发布时打包（通常也不应该打包进 Release 版模组文件里）。请在模组 `build.txt` 的 `buildIgnore` 中加入：

  ```
  buildIgnore = ..., ShaderLoader\fxc\*
  ```

- `.fx` 文件编码需为 **UTF-8 without BOM**（编译时会自动处理 BOM）
- `.fxb` 文件由编译自动生成，取代原来 `.xnb` 格式的 shader 文件 ，**不要手动编辑**
- 发布前务必在 DEBUG 模式下跑一次 `Build + Reload` 确认所有 `.fxb` 已生成
- `buildIgnore` 中**不要**包含 `*.fxb`，否则将导致 Release 版模组无法加载shader
