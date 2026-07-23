namespace ShaderLoader;

public class ShaderCompilationException : Exception
{
    public ShaderCompilationException(string message) : base(message) { }
    public ShaderCompilationException(string message, Exception inner) : base(message, inner) { }
}
