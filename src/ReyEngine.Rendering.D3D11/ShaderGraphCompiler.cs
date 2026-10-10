using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Rendering.D3D11;

/// <summary>One generated pixel shader, compiled and checked against the Riot signature it stands in for.</summary>
public sealed class SgCompiled
{
    public required SgSignature Signature { get; init; }
    public required SgHlslResult Hlsl { get; init; }
    public byte[]? Bytecode { get; init; }

    /// <summary>The compiled shader, reflected - what <c>ShaderPreviewRenderer.LoadShaders</c> takes.</summary>
    public DxbcShader? Shader { get; init; }

    /// <summary>The compiler's own text (warnings included), for the Log tab.</summary>
    public string CompilerLog { get; init; } = "";

    /// <summary>Everything wrong with this signature's shader: generation errors, compiler errors (mapped to nodes where a line allows it)
    /// and any difference between the compiled ISGN/OSGN and Riot's.</summary>
    public IReadOnlyList<SgDiagnostic> Errors { get; init; } = Array.Empty<SgDiagnostic>();

    public bool Ok => Errors.Count == 0 && Shader is not null;
}

/// <summary>The result of compiling a whole graph: one <see cref="SgCompiled"/> per input signature of the base.</summary>
public sealed class SgBuild
{
    public required ShaderGraphDocument Document { get; init; }
    public required SgAnalysis Analysis { get; init; }
    public required IReadOnlyList<SgCompiled> Compiled { get; init; }

    /// <summary>The analyser's diagnostics plus every signature's errors (a node's error repeated in several signatures is shown once).</summary>
    public IReadOnlyList<SgDiagnostic> Diagnostics { get; init; } = Array.Empty<SgDiagnostic>();

    public bool Ok => !Analysis.HasErrors && Compiled.Count > 0 && Compiled.All(c => c.Ok);
    public SgCompiled? For(SgSignature s) => Compiled.FirstOrDefault(c => c.Signature.Index == s.Index);

    /// <summary>The compiled shader whose pixel inputs equal those of a loaded Riot pixel shader, when that signature compiled.</summary>
    public SgCompiled? ForRiotPixel(DxbcShader riotPixel) => Compiled.FirstOrDefault(c => c.Ok && c.Signature.Matches(riotPixel));
}

/// <summary>
/// M832: compiles a Shader Graph with the Windows HLSL compiler (<c>d3dcompiler_47</c>, <c>ps_5_0</c> - the compiler Riot's own blobs
/// came from), one pixel shader per input signature of the base shader, and verifies each result against Riot: the input AND output
/// signatures are compared element for element (semantic, index, system value, type, register, mask), the constants a graph declares
/// must be $Globals at b0, the textures must carry Riot's resource names, and the engine cbuffer must have Riot's layout.
/// Windows only (it needs the compiler DLL); everything else about a graph is in Formats.
/// </summary>
public static unsafe class ShaderGraphCompiler
{
    public static SgBuild BuildAll(ShaderGraphDocument doc, SgBase b, SgHlslOptions? options = null, Func<SgSignature, bool>? only = null)
    {
        var analysis = SgAnalyzer.Analyze(doc, b);
        var compiled = new List<SgCompiled>();
        if (!analysis.HasErrors)
            foreach (var sig in b.Signatures)
                if (only is null || only(sig))
                    compiled.Add(Compile(doc, b, sig, analysis, options));

        var all = new List<SgDiagnostic>(analysis.Diagnostics);
        foreach (var c in compiled)
            foreach (var e in c.Errors)
                if (!all.Contains(e)) all.Add(e);
        return new SgBuild { Document = doc, Analysis = analysis, Compiled = compiled, Diagnostics = all };
    }

    public static SgCompiled Compile(ShaderGraphDocument doc, SgBase b, SgSignature sig, SgAnalysis? analysis = null, SgHlslOptions? options = null)
    {
        var hlsl = SgHlsl.Generate(doc, b, sig, analysis, options);
        if (!hlsl.Ok) return new SgCompiled { Signature = sig, Hlsl = hlsl, Errors = hlsl.Errors };

        if (!TryCompile(hlsl.Source, "main", "ps_5_0", out var code, out string log))
        {
            var errs = ParseCompilerErrors(log, hlsl);
            return new SgCompiled { Signature = sig, Hlsl = hlsl, CompilerLog = log, Errors = errs };
        }

        var shader = DxbcReflection.Parse(code!);
        var problems = Verify(shader, sig, b, hlsl);
        return new SgCompiled
        {
            Signature = sig, Hlsl = hlsl, Bytecode = code, Shader = shader, CompilerLog = log,
            Errors = problems.Select(p => new SgDiagnostic("", null, true, p)).ToList(),
        };
    }

    /// <summary>Everything that must hold before a compiled graph shader may stand in for the Riot pixel shader of <paramref name="sig"/>.</summary>
    public static IReadOnlyList<string> Verify(DxbcShader ps, SgSignature sig, SgBase b, SgHlslResult hlsl)
    {
        var bad = new List<string>();
        if (!SgSignature.SameList(sig.Inputs, ps.Inputs))
            bad.Add($"the compiled input signature differs from Riot's: [{SgSignature.Describe(ps.Inputs)}] vs Riot [{SgSignature.Describe(sig.Inputs)}]");
        if (!SgSignature.SameList(sig.Outputs, ps.Outputs))
            bad.Add($"the compiled output signature differs from Riot's: [{SgSignature.Describe(ps.Outputs)}] vs Riot [{SgSignature.Describe(sig.Outputs)}]");

        var globals = ps.ConstantBuffers.FirstOrDefault(c => c.Name == "$Globals");
        if (globals is not null)
        {
            if (globals.BindPoint != 0) bad.Add($"$Globals is at b{globals.BindPoint}; the client expects b0");
            foreach (var v in globals.Variables)
                if (b.Parameter(v.Name) is null) bad.Add($"$Globals declares '{v.Name}', which the base shader does not declare");
        }
        else if (hlsl.Parameters.Count > 0) bad.Add("the graph reads parameters but the compiled shader has no $Globals");

        foreach (string t in hlsl.Textures)
        {
            var decl = b.Texture(t);
            if (decl is null) continue;
            if (!ps.Resources.Any(r => r.Kind == DxbcResourceKind.Texture && r.Name == decl.Resource))
                bad.Add($"the compiled shader lost the texture {decl.Resource}");
            if (!ps.Resources.Any(r => r.Kind == DxbcResourceKind.Sampler && r.Name == decl.Sampler))
                bad.Add($"the compiled shader lost the sampler {decl.Sampler}");
        }
        foreach (var r in ps.Resources)
            if (r.Kind == DxbcResourceKind.Texture && b.Textures.All(t => t.Resource != r.Name))
                bad.Add($"the compiled shader binds a texture '{r.Name}' the base shader does not declare");

        var frame = ps.ConstantBuffers.FirstOrDefault(c => c.Name == "PerFramePixelCB");
        if (frame is not null && b.Frame is not null)
        {
            if (frame.Size != b.Frame.Size) bad.Add($"PerFramePixelCB is {frame.Size} bytes in the compiled shader, {b.Frame.Size} in Riot's");
            foreach (var v in frame.Variables)
            {
                var riot = b.Frame.Var(v.Name);
                if (riot is null || riot.Offset != v.Offset || riot.Size != v.Size)
                    bad.Add($"PerFramePixelCB.{v.Name} is at {v.Offset} ({v.Size} B) in the compiled shader, {(riot is null ? "absent" : $"{riot.Offset} ({riot.Size} B)")} in Riot's");
            }
        }
        return bad;
    }

    // ================================================================================== the compiler

    // one API object for the process: GetApi() loads the compiler library each time it is called
    private static readonly Lazy<D3DCompiler> Api = new(() => D3DCompiler.GetApi());

    private static readonly Regex ErrorLine = new(@"\((\d+),\d+(?:-\d+)?\):\s*(error|warning)\s+(X\d+):\s*(.*)$", RegexOptions.Compiled);

    /// <summary>fxc writes <c>shader(12,30-45): error X3004: ...</c>; the line is mapped to the node whose statement it is.</summary>
    public static IReadOnlyList<SgDiagnostic> ParseCompilerErrors(string log, SgHlslResult hlsl)
    {
        var list = new List<SgDiagnostic>();
        foreach (string raw in log.Split('\n'))
        {
            var m = ErrorLine.Match(raw.Trim());
            if (!m.Success) continue;
            if (m.Groups[2].Value != "error") continue;
            int line = int.Parse(m.Groups[1].Value);
            string node = hlsl.LineNodes.TryGetValue(line, out var id) ? id : "";
            list.Add(new SgDiagnostic(node, null, true, $"{m.Groups[3].Value}: {m.Groups[4].Value.Trim()} (HLSL line {line})"));
        }
        if (list.Count == 0) list.Add(new SgDiagnostic("", null, true, "the HLSL compiler failed: " + (log.Trim().Length > 0 ? log.Trim() : "no message")));
        return list;
    }

    /// <summary>D3DCompile. <paramref name="log"/> holds the compiler's messages (warnings too) on success and failure alike.</summary>
    public static bool TryCompile(string source, string entry, string profile, out byte[]? bytecode, out string log)
    {
        bytecode = null;
        log = "";
        var src = Encoding.ASCII.GetBytes(source);
        var entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        var profileBytes = Encoding.ASCII.GetBytes(profile + "\0");
        ID3D10Blob* code = null;
        ID3D10Blob* errors = null;
        int hr;
        try
        {
            var compiler = Api.Value;
            fixed (byte* sourcePtr = src)
            fixed (byte* entryPtr = entryBytes)
            fixed (byte* profilePtr = profileBytes)
                hr = compiler.Compile(sourcePtr, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                    entryPtr, profilePtr, 0u, 0u, &code, &errors);
        }
        catch (Exception ex)
        {
            log = "the Windows HLSL compiler is unavailable: " + ex.Message;
            return false;
        }

        try
        {
            if (errors is not null)
                log = Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), checked((int)errors->GetBufferSize()))?.TrimEnd('\0') ?? "";
            if (hr < 0 || code is null)
            {
                if (log.Length == 0) log = $"{profile} compilation failed: 0x{hr:X8}";
                return false;
            }
            int length = checked((int)code->GetBufferSize());
            bytecode = new byte[length];
            Marshal.Copy((nint)code->GetBufferPointer(), bytecode, 0, length);
            return true;
        }
        finally
        {
            if (errors is not null) errors->Release();
            if (code is not null) code->Release();
        }
    }
}
