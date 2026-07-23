# embed.ps1 — 将 ShaderLoader 源码嵌入到你的 tModLoader Mod 项目中
# 用法: .\embed.ps1 <你的 Mod 目录路径>

param(
    [Parameter(Mandatory=$true)]
    [string]$TargetDir
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Create target ShaderLoader directory
$ShaderTarget = Join-Path $TargetDir "ShaderLoader"
New-Item -ItemType Directory -Path $ShaderTarget -Force | Out-Null

# Copy .cs files
Get-ChildItem -Path "$ScriptDir\*.cs" -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $ShaderTarget
}

# Copy CompilerBackend subdirectory
Copy-Item -Path "$ScriptDir\CompilerBackend" -Destination $ShaderTarget -Recurse -Force

# Copy fxcompile (Linux/macOS shader compiler via Wine)
$FxCompileDir = Join-Path $ScriptDir "fxcompile"
if (Test-Path $FxCompileDir) {
    $EffectsTarget = Join-Path $TargetDir "Effects"
    Copy-Item -Path $FxCompileDir -Destination $EffectsTarget -Recurse -Force
}

Write-Host "✓ ShaderLoader 已嵌入到 $ShaderTarget"
Write-Host "  SDK .csproj 会自动编译这些文件，无需手动修改项目文件。"
Write-Host "  在你的 Mod 类中加入 using ShaderLoader; 即可使用 AutoLoadShaderAttribute。"
