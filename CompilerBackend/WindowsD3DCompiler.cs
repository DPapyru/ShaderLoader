using System.Runtime.InteropServices;
using System.Text;

namespace ShaderLoader.CompilerBackend;

/// <summary>
/// Windows 平台 Shader 编译器，使用 D3DCompileFromFile API（d3dcompiler_47.dll）。
/// </summary>
public class WindowsD3DCompiler : IShaderCompiler
{
    private string? _lastError;

    /// <summary>
    /// 获取 D3D 编译器是否可用。
    /// 检查是否为 Windows 平台，并尝试加载 d3dcompiler_47.dll。
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;

                // 尝试加载 DLL 以验证其可用性
                IntPtr handle = NativeLibrary.Load("d3dcompiler_47.dll");
                NativeLibrary.Free(handle);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string? LastError => _lastError;

    // D3DCOMPILE_OPTIMIZATION_LEVEL3 — 最高优化级别（0x00010000）
    // 参见：https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/d3dcompile-constants
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 0x00010000;

    /// <inheritdoc />
    public byte[] Compile(string fxPath, string profile = "fx_2_0")
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        _lastError = null;

        // 解析为完整路径
        string fullPath = Path.GetFullPath(fxPath);

        if (!File.Exists(fullPath))
        {
            _lastError = $"Shader file not found: {fullPath}";
            throw new ShaderCompilationException(_lastError);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _lastError = "D3DCompileFromFile is only available on Windows.";
            throw new PlatformNotSupportedException(_lastError);
        }

        IntPtr ppCode = IntPtr.Zero;
        IntPtr ppErrorMsgs = IntPtr.Zero;

        try
        {
            // Effect 编译时入口点为 null（effect 文件自行定义）
            int hr = NativeMethods.D3DCompileFromFile(
                fullPath,
                IntPtr.Zero,  // pDefines
                IntPtr.Zero,  // pInclude
                IntPtr.Zero,  // pEntrypoint（Effect 模式为 null）
                profile,
                D3DCOMPILE_OPTIMIZATION_LEVEL3,  // Flags1 — 最高优化
                0,                                // Flags2
                out ppCode,
                out ppErrorMsgs
            );

            if (hr < 0 || ppCode == IntPtr.Zero)
            {
                string errorMsg = ExtractErrorMessage(ppErrorMsgs)
                    ?? $"D3DCompileFromFile failed with HRESULT: 0x{hr:X8}";
                _lastError = errorMsg;
                throw new ShaderCompilationException(errorMsg);
            }

            return ReadBlobData(ppCode);
        }
        finally
        {
            if (ppCode != IntPtr.Zero)
                Marshal.Release(ppCode);
            if (ppErrorMsgs != IntPtr.Zero)
                Marshal.Release(ppErrorMsgs);
        }
    }

    /// <summary>
    /// 从 ID3DBlob 错误 blob 中提取错误信息。
    /// </summary>
    private static string? ExtractErrorMessage(IntPtr errorBlob)
    {
        if (errorBlob == IntPtr.Zero)
            return null;

        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(errorBlob);

            // 使用的 ID3DBlob vtable 布局：
            // [0] QueryInterface
            // [1] AddRef
            // [2] Release
            // [3] GetBufferPointer
            // [4] GetBufferSize
            IntPtr getBufferPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            IntPtr getSizePtr = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);

            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferPtrDelegate>(getBufferPtr);
            var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(getSizePtr);

            IntPtr dataPtr = getBuffer(errorBlob);
            int size = getSize(errorBlob);

            if (dataPtr != IntPtr.Zero && size > 0)
            {
                byte[] buffer = new byte[size];
                Marshal.Copy(dataPtr, buffer, 0, size);
                // 错误信息通常以 null 结尾
                int nullTerm = Array.IndexOf<byte>(buffer, 0);
                int length = nullTerm >= 0 ? nullTerm : size;
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
        }
        catch
        {
            // 尽力提取错误信息
        }

        return null;
    }

    /// <summary>
    /// 从 ID3DBlob 指针读取缓冲区数据。
    /// </summary>
    private static byte[] ReadBlobData(IntPtr blob)
    {
        IntPtr vtable = Marshal.ReadIntPtr(blob);

        // ID3DBlob vtable：GetBufferPointer 在索引 3，GetBufferSize 在索引 4
        IntPtr getBufferPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        IntPtr getSizePtr = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);

        var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferPtrDelegate>(getBufferPtr);
        var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(getSizePtr);

        IntPtr dataPtr = getBuffer(blob);
        int size = getSize(blob);

        if (dataPtr == IntPtr.Zero || size <= 0)
            return [];

        byte[] result = new byte[size];
        Marshal.Copy(dataPtr, result, 0, size);
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetBufferPtrDelegate(IntPtr pBlob);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetBufferSizeDelegate(IntPtr pBlob);

    /// <summary>
    /// d3dcompiler_47.dll 的本地 P/Invoke 方法。
    /// </summary>
    private static partial class NativeMethods
    {
        private const string DllName = "d3dcompiler_47.dll";

        /// <summary>
        /// D3DCompileFromFile：从文件编译 HLSL Shader。
        /// 成功时返回 S_OK。
        /// </summary>
        /// <param name="pFileName">Shader 文件路径（LPCWSTR）。</param>
        /// <param name="pDefines">可选的 D3D_SHADER_MACRO 数组。</param>
        /// <param name="pInclude">可选的 ID3DInclude 接口。</param>
        /// <param name="pEntrypoint">入口点名称（Effect 模式为 null）。</param>
        /// <param name="pTarget">Shader profile/target 字符串。</param>
        /// <param name="flags1">D3DCOMPILE 标志。</param>
        /// <param name="flags2">Effect 标志。</param>
        /// <param name="ppCode">输出：包含编译后字节码的 ID3DBlob。</param>
        /// <param name="ppErrorMsgs">输出：包含错误信息的 ID3DBlob。</param>
        /// <returns>HRESULT</returns>
        [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int D3DCompileFromFile(
            [MarshalAs(UnmanagedType.LPWStr)] string pFileName,
            IntPtr pDefines,
            IntPtr pInclude,
            IntPtr pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            uint flags1,
            uint flags2,
            out IntPtr ppCode,
            out IntPtr ppErrorMsgs
        );
    }
}
