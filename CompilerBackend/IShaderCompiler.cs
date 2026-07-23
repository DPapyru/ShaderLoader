namespace ShaderLoader;

public interface IShaderCompiler
{
    byte[] Compile(string fxPath, string profile = "fx_2_0");
    string? LastError { get; }
}
