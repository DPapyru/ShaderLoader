# ShaderLoader

一个 tModLoader 模组专用的 HLSL Shader 动态加载与热重载模块。

## 功能

- **运行时编译** — 自动将 `.fx` 编译为 `Effect`，无需预编译步骤
- **热重载** — 修改 `.fx` 文件后自动重新编译，游戏内即时生效
- **跨平台编译器** — Windows 使用 D3DCompiler，Linux/macOS 通过 Wine + fxcompile.exe（C 源码见 `fxcompile/` 目录）
- **自动加载** — `[AutoLoadShader]` 属性标记 + `ModSystem` 自动扫描

## 嵌入到你的模组

### 方式一：脚本嵌入（推荐）

```bash
# Linux / macOS
./embed.sh /path/to/你的Mod目录

# Windows PowerShell
.\embed.ps1 C:\path\to\你的Mod目录
```

脚本会将 ShaderLoader 源码复制到你的项目 `ShaderLoader/` 子目录。SDK 风格 `.csproj` 会自动编译。

### 方式二：Git Submodule

```bash
cd /path/to/你的Mod目录
git submodule add <仓库URL> ShaderLoader
```

## 使用

在你的 Mod 类中：

```csharp
using ShaderLoader;

public class MyMod : Mod
{
    [AutoLoadShader("Effects/Source/MyShader.fx")]
    public static Effect MyShader;
}
```

`AutoShaderModSystem` 在 `PostSetupContent()` 中自动编译并赋值。

## 构建

```bash
# 自行指定 FNA/tModLoader 路径
dotnet build -p:FNAPath=/path/to/FNA.dll -p:TMLPath=/path/to/tModLoader.dll

# 或设置环境变量
export FNA_PATH=/path/to/FNA.dll
export TML_PATH=/path/to/tModLoader.dll
dotnet build
```

## 测试

```bash
dotnet test ShaderLoader.Tests/ShaderLoader.Tests.csproj
```

## Linux/macOS 前置准备

Shader 编译需要 fxcompile.exe，位于 `fxcompile/` 目录。如果缺失，可以自行编译：

```bash
# 安装 MinGW 交叉编译器
sudo apt install mingw-w64   # Debian/Ubuntu
brew install mingw-w64       # macOS

# 编译
cd fxcompile
x86_64-w64-mingw32-gcc -o fxcompile.exe fxcompile.c -ld3dcompiler -O2 -s
```

编译后的 `fxcompile.exe` 会被 `WineFxCompiler` 自动搜索到。

## 依赖

- [tModLoader](https://github.com/tModLoader/tModLoader) — 提供 `ModSystem` 基类
- [FNA](https://fna-xna.github.io/) — XNA 开源实现，提供 `Effect` 类型
- Windows: `d3dcompiler_47.dll`（系统自带）
- Linux/macOS: Wine + fxcompile.exe
