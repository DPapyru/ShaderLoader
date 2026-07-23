/*
 * fxcompile.c -- MinGW cross-compiled HLSL effect shader compiler
 *
 * Usage: fxcompile.exe <input.fx> <output.fxb> [profile]
 *
 * Calls D3DCompileFromFile in Effect mode (pEntryPoint = NULL)
 * and writes the compiled bytecode to disk.
 *
 * Build (cross-compile for Windows via MinGW):
 *   x86_64-w64-mingw32-gcc -o fxcompile.exe fxcompile.c -ld3dcompiler -O2 -s
 */

#include <windows.h>
#include <d3dcompiler.h>
#include <stdio.h>

/* Maximum length for a wide-character file path */
#define MAX_WIDE_PATH 1024

/* Convenience macro: release a COM pointer and NULL it */
#define SAFE_RELEASE(p)  do { if (p) { (p)->lpVtbl->Release(p); (p) = NULL; } } while (0)

static int wide_from_utf8(LPCSTR utf8, LPWSTR wide, int wideLen)
{
    return MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide, wideLen);
}

int main(int argc, char *argv[])
{
    const char *inputPath;
    const char *outputPath;
    const char *profile;
    WCHAR wideInput[MAX_WIDE_PATH];
    FILE *outFile;
    ID3DBlob *shaderBlob;
    ID3DBlob *errorBlob;
    HRESULT hr;

    /* ------------------------------------------------------------------ */
    /*  1. Parse arguments                                                */
    /* ------------------------------------------------------------------ */
    if (argc < 3 || argc > 4) {
        fprintf(stderr, "Usage: fxcompile.exe <input.fx> <output.fxb> [profile]\n");
        return 1;
    }

    inputPath  = argv[1];
    outputPath = argv[2];
    profile    = (argc == 4) ? argv[3] : "fx_2_0";

    /* ------------------------------------------------------------------ */
    /*  2. Convert input path to wide characters (D3DCompileFromFile       */
    /*     expects LPCWSTR).                                              */
    /* ------------------------------------------------------------------ */
    if (!wide_from_utf8(inputPath, wideInput, MAX_WIDE_PATH)) {
        fprintf(stderr, "Failed to convert input path to wide string\n");
        return 1;
    }

    /* ------------------------------------------------------------------ */
    /*  3. Compile the shader                                             */
    /* ------------------------------------------------------------------ */
    shaderBlob = NULL;
    errorBlob  = NULL;

    hr = D3DCompileFromFile(
             wideInput,                      /* pFileName */
             NULL,                           /* pDefines */
             NULL,                           /* pInclude */
             NULL,                           /* pEntryPoint – NULL = Effect mode */
             (LPCSTR)profile,                /* pTarget */
             D3DCOMPILE_OPTIMIZATION_LEVEL3, /* Flags1 */
             0,                              /* Flags2 */
             &shaderBlob,                    /* ppCode */
             &errorBlob                      /* ppErrorMsgs */
         );

    if (FAILED(hr)) {
        if (errorBlob) {
            fprintf(stderr, "Compilation failed (HRESULT 0x%08lx): %.*s\n",
                    (unsigned long)hr,
                    (int)errorBlob->lpVtbl->GetBufferSize(errorBlob),
                    (const char *)errorBlob->lpVtbl->GetBufferPointer(errorBlob));
        } else {
            fprintf(stderr, "Compilation failed (HRESULT 0x%08lx)\n",
                    (unsigned long)hr);
        }
        SAFE_RELEASE(shaderBlob);
        SAFE_RELEASE(errorBlob);
        return 2;
    }

    /* ------------------------------------------------------------------ */
    /*  4. Write compiled bytecode to disk                                */
    /* ------------------------------------------------------------------ */
    outFile = fopen(outputPath, "wb");
    if (!outFile) {
        fprintf(stderr, "Failed to open output file '%s'\n", outputPath);
        SAFE_RELEASE(shaderBlob);
        SAFE_RELEASE(errorBlob);
        return 3;
    }

    fwrite(shaderBlob->lpVtbl->GetBufferPointer(shaderBlob),
           1,
           shaderBlob->lpVtbl->GetBufferSize(shaderBlob),
           outFile);
    fclose(outFile);

    /* ------------------------------------------------------------------ */
    /*  5. Cleanup                                                        */
    /* ------------------------------------------------------------------ */
    SAFE_RELEASE(shaderBlob);
    SAFE_RELEASE(errorBlob);

    return 0;
}
