using System.Globalization;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.Graph;

/// <summary>One line of the Stats tab: a compiled-shader figure per stage.</summary>
public sealed record GraphStatRow(string Label, string Pixel, string Vertex, string Tip = "");

/// <summary>
/// M829: the Stats tab's numbers. Every figure is read from the resolved permutation's own bytecode (DXBC
/// reflection and the STAT chunk) or counted from the graph; a figure the blob does not carry reads "n/a",
/// never a guess. Unreal's Stats panel shows instruction counts, texture samplers (x/16) and interpolators; the
/// D3D11 equivalents are below.
/// </summary>
public static class MaterialGraphStats
{
    /// <summary>D3D11 gives each shader stage 16 sampler-state slots.</summary>
    public const int SamplerSlots = 16;

    public static IReadOnlyList<GraphStatRow> Shader(DxbcShader? vs, DxbcShader? ps)
    {
        var rows = new List<GraphStatRow>();
        var pStat = ps is null ? null : DxbcStats.Read(ps);
        var vStat = vs is null ? null : DxbcStats.Read(vs);
        string N(int? v) => v is int i ? i.ToString("n0", CultureInfo.CurrentCulture) : "n/a";

        rows.Add(new("Instructions", N(pStat?.Instructions), N(vStat?.Instructions),
            "The compiler's own count from the DXBC STAT chunk. It can run a little above a plain opcode recount."));
        rows.Add(new("Temp registers", N(pStat?.TempRegisters), N(vStat?.TempRegisters)));
        rows.Add(new("Texture samples", N(pStat?.TextureSamples), N(vStat?.TextureSamples),
            "Sample opcodes counted in the shader itself: sample, sample_b, sample_l, sample_d, sample_c. (The compiler's own STAT dword leaves sample_l out, so it is not used.)"));
        rows.Add(new("Texture inputs", N(ps?.Textures.Count()), N(vs?.Textures.Count()), "SRV bind points the stage declares."));
        rows.Add(new("Texture samplers", Slots(ps?.Samplers.Count()), Slots(vs?.Samplers.Count()),
            "Sampler states declared, out of the 16 slots D3D11 gives a stage."));
        rows.Add(new("Constant buffers", N(ps?.ConstantBuffers.Count), N(vs?.ConstantBuffers.Count)));
        rows.Add(new("Constants (read / declared)", Constants(ps), Constants(vs),
            "Variables the stage reads, out of those its constant buffers declare."));
        rows.Add(new("Stage inputs", N(ps?.Inputs.Count), N(vs?.Inputs.Count),
            "Pixel inputs are the interpolators the vertex stage hands over; vertex inputs are the mesh attributes it needs."));
        rows.Add(new("Stage outputs", N(ps?.Outputs.Count), N(vs?.Outputs.Count)));
        rows.Add(new("Bytecode (bytes)", N(ps?.ByteSize), N(vs?.ByteSize)));
        rows.Add(new("Shader model", ps?.ShaderModel ?? "n/a", vs?.ShaderModel ?? "n/a"));
        return rows;

        static string Slots(int? n) => n is int i ? $"{i} / {SamplerSlots}" : "n/a";
        static string Constants(DxbcShader? s) => s is null ? "n/a"
            : $"{s.ConstantBuffers.Sum(cb => cb.Variables.Count(v => v.IsUsed))} / {s.ConstantBuffers.Sum(cb => cb.Variables.Count)}";
    }

    /// <summary>Counts from the graph itself: what the material authors and how much of it reaches the shader.</summary>
    public static IReadOnlyList<(string Label, string Value)> Material(MaterialGraph g)
    {
        int Count(Func<GraphNode, bool> f) => g.Nodes.Count(f);
        int Wired(GraphNodeKind kind) => g.Wires.Count(w => w.SourceKind == kind);
        bool Authored(GraphNode n) => n.State != GraphNodeState.ShaderDefault;

        int textures = Count(n => n.Kind == GraphNodeKind.Texture && Authored(n));
        int texturesWired = g.Wires.Count(w => w.SourceKind == GraphNodeKind.Texture && g.Find(w.FromNode)!.State == GraphNodeState.Authored);
        var paramKinds = new[] { GraphNodeKind.Scalar, GraphNodeKind.Vector, GraphNodeKind.Color };
        int parameters = Count(n => paramKinds.Contains(n.Kind));
        int parametersWired = g.Wires.Count(w => paramKinds.Contains(w.SourceKind));
        int switches = Count(n => n.Kind == GraphNodeKind.Switch && Authored(n));
        int switchDefaults = Count(n => n.Kind == GraphNodeKind.Switch && !Authored(n));
        int macros = Count(n => n.Kind == GraphNodeKind.Macro);

        return new (string, string)[]
        {
            ("Texture samplers authored", $"{textures}  ({texturesWired} reach the shader)"),
            ("Parameters authored", $"{parameters}  ({parametersWired} reach the shader)"),
            ("Switches authored", $"{switches}  (+ {switchDefaults} shader default(s) shown)"),
            ("Shader macros", macros.ToString(CultureInfo.InvariantCulture)),
            ("Graph", $"{g.Nodes.Count} nodes, {g.Wires.Count} wires"),
        };
    }
}
