# fxcompile.exe -- MinGW 交叉编译 Shader 编译器

## 用途

`fxcompile.exe` 是一个极简的 Windows 命令行工具，用于编译 HLSL `.fx` 着色器文件为 D3D Effect 字节码 (`.fxb`)。

它通过调用 `D3DCompileFromFile()` 在 **Effect 模式** (`pEntryPoint = NULL`) 下编译着色器，使用优化级别 `D3DCOMPILE_OPTIMIZATION_LEVEL3`。

在 Linux / macOS 上，此工具通过 **Wine** 调用，因此需要 MinGW 交叉编译为 Windows PE 可执行文件。

## 依赖

- MinGW-w64 交叉编译器
- `d3dcompiler` 链接库（MinGW 自带，无需单独安装）

## 安装 MinGW

### Ubuntu / Debian

```bash
sudo apt install mingw-w64
```

### Arch Linux

```bash
sudo pacman -S mingw-w64-gcc
```

### macOS (Homebrew)

```bash
brew install mingw-w64
```

## 编译

```bash
cd Effects/fxcompile
x86_64-w64-mingw32-gcc -o fxcompile.exe fxcompile.c -ld3dcompiler -O2 -s
```

参数说明：
- `-ld3dcompiler` -- 链接 D3DCompiler 库
- `-O2` -- 启用优化
- `-s` -- 去除符号表，减小文件体积

## 使用

```bash
# 基本用法（默认 profile: fx_2_0）
wine fxcompile.exe input.fx output.fxb

# 指定 profile
wine fxcompile.exe input.fx output.fxb fx_5_0
```

### 参数

| 参数 | 说明 |
|------|------|
| `input.fx` | 输入 HLSL 着色器源文件 |
| `output.fxb` | 输出编译后字节码文件 |
| `[profile]` | (可选) D3D Effect profile，默认 `fx_2_0` |

### 退出码

| 退出码 | 含义 |
|--------|------|
| 0 | 成功 |
| 1 | 参数错误 |
| 2 | 编译失败（错误信息输出到 stderr） |
| 3 | 输出文件写入失败 |

## 在 HumanBoss 模组中的位置

编译后的 `.fxb` 文件应放置于模组资源目录，由 C# 端通过 `ModContent.Request<Effect>()` 或直接加载字节数组使用。
