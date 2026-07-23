using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShaderLoader.CompilerBackend;

/// <summary>
/// Linux/macOS Shader 编译器后端。
/// 通过 Wine 调用 fxcompile.exe 将 .fx 文件编译为 .fxb 字节码。
/// </summary>
public class WineFxCompiler : IShaderCompiler
{
    private string? _lastError;
    private static bool? _isAvailableCache;
    private static string? _exePath;

    /// <summary>
    /// 获取 Wine 和 fxcompile.exe 是否可用。
    /// 首次检查后缓存结果。
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_isAvailableCache.HasValue)
                return _isAvailableCache.Value;

            _isAvailableCache = CheckAvailability();
            return _isAvailableCache.Value;
        }
    }

    /// <summary>
    /// fxcompile.exe 的路径。在可用性检查时解析。
    /// </summary>
    public static string? ExecutablePath => _exePath;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <summary>
    /// fxcompile.exe 的可选显式路径。
    /// 设置后将覆盖自动搜索。
    /// </summary>
    public static string? ExplicitExePath { get; set; }

    /// <inheritdoc />
    public byte[] Compile(string fxPath, string profile = "fx_2_0")
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        _lastError = null;

        string fullPath = Path.GetFullPath(fxPath);
        if (!File.Exists(fullPath))
        {
            _lastError = $"Shader file not found: {fullPath}";
            throw new ShaderCompilationException(_lastError);
        }

        if (!IsAvailable)
        {
            string exeName = _exePath ?? "fxcompile.exe";
            _lastError = $"Wine or {exeName} is not available. Install Wine and ensure fxcompile.exe is in PATH or Effects/fxcompile/.";
            throw new PlatformNotSupportedException(_lastError);
        }

        // 创建临时输出路径
        string tempOutput = Path.GetTempFileName();
        try
        {
            string args = $"\"{_exePath}\" \"{fullPath}\" \"{tempOutput}\" {profile}";

            var psi = new ProcessStartInfo
            {
                FileName = "wine",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // 读取 stderr 以获取失败信息
            string stderr = process.StandardError.ReadToEnd();
            // 也需要读取 stdout 以避免死锁
            string stdout = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                _lastError = $"Compilation timed out after 30 seconds: {fullPath}";
                throw new ShaderCompilationException(_lastError);
            }

            if (process.ExitCode != 0)
            {
                string errorMsg = !string.IsNullOrEmpty(stderr)
                    ? stderr.Trim()
                    : $"fxcompile.exe exited with code {process.ExitCode}";
                _lastError = errorMsg;
                throw new ShaderCompilationException(errorMsg);
            }

            // 读取编译后的字节码
            if (!File.Exists(tempOutput))
            {
                _lastError = $"Compilation succeeded but output file was not created: {tempOutput}";
                throw new ShaderCompilationException(_lastError);
            }

            byte[] result = File.ReadAllBytes(tempOutput);
            if (result.Length == 0)
            {
                _lastError = "Compilation produced empty output";
                throw new ShaderCompilationException(_lastError);
            }

            return result;
        }
        finally
        {
            // 清理临时文件
            if (File.Exists(tempOutput))
            {
                try { File.Delete(tempOutput); }
                catch { /* 尽力清理 */ }
            }
        }
    }

    /// <summary>
    /// 重置缓存的可用性状态。
    /// 如果 fxcompile.exe 或 Wine 在首次检查后变为可用，调用此方法。
    /// </summary>
    public static void ResetCache()
    {
        _isAvailableCache = null;
        _exePath = null;
    }

    /// <summary>
    /// 检查 Wine 是否可用并查找 fxcompile.exe。
    /// </summary>
    private static bool CheckAvailability()
    {
        // 检查 wine 命令是否存在
        if (!IsWineAvailable())
            return false;

        // 查找 fxcompile.exe
        _exePath = FindFxCompileExe();
        return _exePath != null;
    }

    private static bool IsWineAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "wine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null)
                return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按优先级顺序搜索 fxcompile.exe：
    /// 1. ExplicitExePath（如果已设置）
    /// 2. Effects/fxcompile/ 相对于工作目录
    /// 3. 当前工作目录
    /// 4. PATH 环境变量
    /// 5. 工作目录向上最多 5 层（当 cwd 为子目录时）
    /// </summary>
    private static string? FindFxCompileExe()
    {
        // 1. 显式路径
        if (!string.IsNullOrEmpty(ExplicitExePath) && File.Exists(ExplicitExePath))
            return ExplicitExePath;

        // 2-3. 本地搜索路径
        string[] searchPaths =
        [
            Path.Combine(Environment.CurrentDirectory, "Effects", "fxcompile", "fxcompile.exe"),
            Path.Combine(Environment.CurrentDirectory, "fxcompile.exe"),
        ];

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        // 4. 上级目录搜索（处理嵌套工作目录）
        string? dir = Path.GetDirectoryName(Environment.CurrentDirectory);
        for (int i = 0; i < 5 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "Effects", "fxcompile", "fxcompile.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            candidate = Path.Combine(dir, "fxcompile.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            dir = Path.GetDirectoryName(dir);
        }

        // 5. PATH 环境变量
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string p in pathEnv.Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(p, "fxcompile.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
