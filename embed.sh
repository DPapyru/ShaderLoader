#!/bin/bash
# embed.sh — 将 ShaderLoader 源码嵌入到你的 tModLoader Mod 项目中
# 用法: ./embed.sh <你的 Mod 目录路径>

set -euo pipefail

if [ $# -lt 1 ]; then
  echo "Usage: $0 <path-to-your-mod-directory>"
  exit 1
fi

TARGET="$1"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$TARGET/ShaderLoader"
cp -a "$SCRIPT_DIR"/CompilerBackend "$TARGET/ShaderLoader/"
cp "$SCRIPT_DIR"/*.cs "$TARGET/ShaderLoader/"

echo "✓ ShaderLoader 已嵌入到 $TARGET/ShaderLoader/"
echo "  SDK .csproj 会自动编译这些文件，无需手动修改项目文件。"
echo "  在你的 Mod 类中加入 using ShaderLoader; 即可使用 AutoLoadShaderAttribute。"
