using System;
using System.Diagnostics;
using System.IO;

namespace ShaderLoader;

/// <summary>
/// 运行时 .fx Effects 编译器。
/// 使用 fxc.exe 配合 /T fx_2_0 profile 编译完整 .fx 文件（含 technique/pass/pixel shader），
/// 输出为 FNA/MojoShader 可直接消费的编译后 Effect 字节码。
/// </summary>
public static class FxEffectCompiler
{
    private static string? _fxcPath;
    public static void SetFxcPath(string path) => _fxcPath = path;

    /// <summary>
    /// 编译 .fx 文件为 FNA Effect 字节码。
    /// fxc.exe /T fx_2_0 以 effects profile 编译完整文件（含 technique/pass/compile 指令），
    /// 输出直接传入 new Effect(GraphicsDevice, bytes)。
    /// </summary>
    public static byte[] Compile(string fxFilePath)
    {
        if (_fxcPath == null)
            throw new InvalidOperationException("FXC path not set");

        string tmp = Path.Combine(Path.GetTempPath(), "SL_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string srcFile = Path.Combine(tmp, "s.fx");
        string outFile = Path.Combine(tmp, "s.fxo");
        try
        {
            // 读取 .fx 源文件（ReadAllText 自动识别并丢弃 UTF-8 BOM）
            // 写入临时文件（WriteAllText 默认不带 BOM），fxc 不接受 BOM
            string content = File.ReadAllText(fxFilePath);
            File.WriteAllText(srcFile, content);

            // /T fx_2_0 → effects profile，编译完整 .fx（含 technique/pass/compile）
            var psi = new ProcessStartInfo(_fxcPath)
            {
                Arguments = $"/nologo /T fx_2_0 /Fo \"{outFile}\" \"{srcFile}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = new Process { StartInfo = psi };
            var stderr = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(30000))
            {
                proc.Kill();
                throw new ShaderCompilationException("fxc.exe 编译超时（30 秒）");
            }
            if (proc.ExitCode != 0)
                throw new ShaderCompilationException($"fxc: {stderr}");

            return File.ReadAllBytes(outFile);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}

public class ShaderCompilationException : Exception
{
    public ShaderCompilationException(string m) : base(m) { }
    public ShaderCompilationException(string m, Exception e) : base(m, e) { }
}
