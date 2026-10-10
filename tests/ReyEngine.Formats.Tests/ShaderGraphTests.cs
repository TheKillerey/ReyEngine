using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M832: the Shader Graph without a game install - type promotion, the analyser's errors, the generated HLSL (a snapshot, ASCII only, a
/// line map back to nodes), the JSON round trip, the canvas projection and the project folder rule. The base shader here is built by hand
/// (two pixel input signatures shaped like DefaultEnv_Flat's, a trimmed PerFramePixelCB); the real 16 signatures are in
/// <see cref="ShaderGraphRealDataTests"/>.
/// </summary>
public sealed class ShaderGraphTests
{
    // ---- a hand-built base: signature 0 = pos, TEXCOORD0 (float4), TEXCOORD1 (float2 = UV0); signature 1 adds TEXCOORD2 (float3)
    private static DxbcSignatureElement In(string sem, uint idx, uint reg, byte mask, uint sv = 0) => new(sem, idx, reg, mask, mask, 3, sv);

    internal static SgBase TestBase(bool withFrame = true)
    {
        var outs = new[] { In("SV_Target", 0, 0, 0xF), In("SV_Target", 1, 1, 0xF) };
        var s0 = new SgSignature
        {
            Index = 0, Outputs = outs, Permutations = Array.Empty<ShaderPermutation>(),
            Inputs = new[] { In("SV_Position", 0, 0, 0xF, 1), In("TEXCOORD", 0, 1, 0xF), In("TEXCOORD", 1, 2, 0x3) },
        };
        var s1 = new SgSignature
        {
            Index = 1, Outputs = outs, Permutations = Array.Empty<ShaderPermutation>(),
            Inputs = new[] { In("SV_Position", 0, 0, 0xF, 1), In("TEXCOORD", 0, 1, 0xF), In("TEXCOORD", 1, 2, 0x3), In("TEXCOORD", 2, 3, 0x7) },
        };
        return new SgBase
        {
            Shader = "Shaders/StaticMesh/DefaultEnv_Flat",
            Textures = new[] { new SgTextureDecl("DiffuseTexture"), new SgTextureDecl("DetailTexture") },
            Parameters = new[] { new SgParameterDecl("TintColor", 4, new float[4]), new SgParameterDecl("Strength", 1, new float[1]) },
            Signatures = new[] { s0, s1 },
            UvSet = 1,
            Frame = withFrame
                ? new SgEngineBuffer("PerFramePixelCB", 528, new[]
                {
                    new SgEngineVar("vCamera", 0, 12, "float3"),
                    new SgEngineVar("TIME", 16, 16, "float4"),
                    new SgEngineVar("RENDERTARGET_IS_SRGB", 512, 4, "uint"),
                })
                : null,
        };
    }

    internal static SgNode Add(ShaderGraphDocument d, string id, string type, params (string, string)[] props)
    {
        var n = new SgNode { Id = id, Type = type, X = d.Nodes.Count * 40 };
        foreach (var (k, v) in props) n.Props[k] = v;
        d.Nodes.Add(n);
        return n;
    }

    internal static void Link(ShaderGraphDocument d, string from, string fromPin, string to, string toPin) =>
        d.Links.Add(new SgLink { FromNode = from, FromPin = fromPin, ToNode = to, ToPin = toPin });

    private static ShaderGraphDocument Doc() => ShaderGraphDocument.CreateEmpty("t", "Shaders/StaticMesh/DefaultEnv_Flat");

    /// <summary>texture x TintColor -> Base Color (rgb), texture alpha -> Opacity: the starter graph the editor makes.</summary>
    private static ShaderGraphDocument TextureTimesTint()
    {
        var d = Doc();
        Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        Add(d, "n3", "Parameter", ("name", "TintColor"));
        Add(d, "n4", "Multiply");
        Add(d, "n5", "ComponentMask", ("mask", "rgb"));
        Link(d, "n2", "RGBA", "n4", "A"); Link(d, "n3", "Value", "n4", "B");
        Link(d, "n4", "Value", "n5", "Value");
        Link(d, "n5", "Value", "n1", SgCatalog.BaseColorPin);
        Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        return d;
    }

    private static string[] Errors(SgAnalysis a) => a.Errors.Select(e => e.Message).ToArray();

    // ================================================================================= type promotion

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 3, 3)]
    [InlineData(3, 1, 3)]
    [InlineData(4, 4, 4)]
    [InlineData(2, 2, 2)]
    public void A_scalar_broadcasts_and_equal_vectors_match(int a, int b, int expected)
    {
        var d = Doc();
        Add(d, "a", "Constant", ("value", string.Join(" ", Enumerable.Repeat("1", a))));
        Add(d, "b", "Constant", ("value", string.Join(" ", Enumerable.Repeat("2", b))));
        foreach (string op in new[] { "Add", "Subtract", "Multiply", "Divide", "Min", "Max", "Power" })
        {
            var dd = d.Clone();
            Add(dd, "op", op);
            Link(dd, "a", "Value", "op", SgCatalog.Get(op)!.Inputs[0].Name);
            Link(dd, "b", "Value", "op", SgCatalog.Get(op)!.Inputs[1].Name);
            var r = SgAnalyzer.Analyze(dd, null);
            Assert.Equal(expected, r.OutputWidth("op", 0));
            Assert.Empty(r.Errors);
        }
    }

    [Fact]
    public void Two_different_vectors_are_an_error_on_the_node_and_the_wire_input_and_the_output_carries_no_value()
    {
        var d = Doc();
        Add(d, "a", "Constant", ("value", "1 2 3"));
        Add(d, "b", "Constant", ("value", "1 2"));
        Add(d, "m", "Add");
        Link(d, "a", "Value", "m", "A"); Link(d, "b", "Value", "m", "B");
        Link(d, "m", "Value", "n1", SgCatalog.BaseColorPin);
        var r = SgAnalyzer.Analyze(d, null);
        Assert.Equal(0, r.OutputWidth("m", 0));
        var err = Assert.Single(r.Errors, e => e.NodeId == "m");
        Assert.Contains("float3", err.Message);
        Assert.Contains("float2", err.Message);
        Assert.Contains("only a scalar can be mixed with a vector", err.Message);
        // the broken node's consumer does not pile a second error on top of it
        Assert.DoesNotContain(r.Errors, e => e.NodeId == "n1");
    }

    [Fact]
    public void Lerp_alpha_must_be_a_scalar_or_the_blended_width()
    {
        var d = Doc();
        Add(d, "a", "Constant", ("value", "1 2 3")); Add(d, "b", "Constant", ("value", "4 5 6"));
        Add(d, "al", "Constant", ("value", "0.5 0.5")); Add(d, "l", "Lerp");
        Link(d, "a", "Value", "l", "A"); Link(d, "b", "Value", "l", "B"); Link(d, "al", "Value", "l", "Alpha");
        var r = SgAnalyzer.Analyze(d, null);
        Assert.Contains(r.Diagnostics, e => e.NodeId == "l" && e.Pin == "Alpha");
        d.Links.RemoveAll(l => l.ToPin == "Alpha");
        Add(d, "s", "Constant", ("value", "0.25"));
        Link(d, "s", "Value", "l", "Alpha");
        Assert.Equal(3, SgAnalyzer.Analyze(d, null).OutputWidth("l", 0));
    }

    [Fact]
    public void Dot_gives_a_scalar_Append_adds_widths_and_stops_at_four()
    {
        var d = Doc();
        Add(d, "a", "Constant", ("value", "1 2 3")); Add(d, "b", "Constant", ("value", "1 2 3"));
        Add(d, "dot", "Dot"); Link(d, "a", "Value", "dot", "A"); Link(d, "b", "Value", "dot", "B");
        Add(d, "c", "Constant", ("value", "1 2")); Add(d, "ap", "Append"); Link(d, "a", "Value", "ap", "A"); Link(d, "c", "Value", "ap", "B");
        Add(d, "x", "Constant", ("value", "9")); Add(d, "ap2", "Append"); Link(d, "a", "Value", "ap2", "A"); Link(d, "x", "Value", "ap2", "B");
        var r = SgAnalyzer.Analyze(d, null);
        Assert.Equal(1, r.OutputWidth("dot", 0));
        Assert.Equal(0, r.OutputWidth("ap", 0));               // 3 + 2 = 5
        Assert.Contains(r.Diagnostics, e => e.NodeId == "ap" && e.Message.Contains("5 components"));
        Assert.Equal(4, r.OutputWidth("ap2", 0));              // 3 + 1
    }

    [Fact]
    public void Mask_and_Split_only_reach_components_that_exist()
    {
        var d = Doc();
        Add(d, "v", "Constant", ("value", "1 2")); Add(d, "m", "ComponentMask", ("mask", "rgb")); Link(d, "v", "Value", "m", "Value");
        Add(d, "m2", "ComponentMask", ("mask", "gr")); Link(d, "v", "Value", "m2", "Value");
        Add(d, "m3", "ComponentMask", ("mask", "q")); Link(d, "v", "Value", "m3", "Value");
        Add(d, "sp", "Split"); Link(d, "v", "Value", "sp", "Value");
        var r = SgAnalyzer.Analyze(d, null);
        Assert.Contains(r.Diagnostics, e => e.NodeId == "m" && e.Message.Contains("component b"));
        Assert.Equal(2, r.OutputWidth("m2", 0));
        Assert.Contains(r.Diagnostics, e => e.NodeId == "m3");
        Assert.Equal(new[] { 1, 1, 0, 0 }, new[] { r.OutputWidth("sp", 0), r.OutputWidth("sp", 1), r.OutputWidth("sp", 2), r.OutputWidth("sp", 3) });
        // using Split.B of a float2 is an error where it is used
        Add(d, "use", "Saturate"); Link(d, "sp", "B", "use", "Value");
        Assert.Contains(SgAnalyzer.Analyze(d, null).Diagnostics, e => e.NodeId == "use" && e.Message.Contains("carries no value"));
    }

    [Fact]
    public void Normalize_needs_a_vector_and_Output_pins_take_their_width_or_a_scalar()
    {
        var d = Doc();
        Add(d, "s", "Constant", ("value", "1")); Add(d, "n", "Normalize"); Link(d, "s", "Value", "n", "Value");
        Add(d, "v2", "Constant", ("value", "1 2")); Link(d, "v2", "Value", "n1", SgCatalog.BaseColorPin);
        Add(d, "v3", "Constant", ("value", "1 2 3")); Link(d, "v3", "Value", "n1", SgCatalog.OpacityPin);
        var r = SgAnalyzer.Analyze(d, null);
        Assert.Contains(r.Diagnostics, e => e.NodeId == "n" && e.Message.Contains("needs a vector"));
        Assert.Contains(r.Errors, e => e.NodeId == "n1" && e.Pin == SgCatalog.BaseColorPin);
        Assert.Contains(r.Errors, e => e.NodeId == "n1" && e.Pin == SgCatalog.OpacityPin);
        // a scalar is fine for the colour (it broadcasts)
        d.Links.Clear();
        Link(d, "s", "Value", "n1", SgCatalog.BaseColorPin);
        Assert.Empty(SgAnalyzer.Analyze(d, null).Errors);
    }

    [Fact]
    public void A_wire_loop_is_an_error_and_a_graph_needs_exactly_one_output()
    {
        var d = Doc();
        Add(d, "a", "Add"); Add(d, "b", "Add");
        Link(d, "a", "Value", "b", "A"); Link(d, "b", "Value", "a", "A");
        Link(d, "a", "Value", "n1", SgCatalog.BaseColorPin);
        Assert.Contains(SgAnalyzer.Analyze(d, null).Errors, e => e.Message.Contains("loop"));

        var none = Doc(); none.Nodes.Clear();
        Assert.Contains(SgAnalyzer.Analyze(none, null).Errors, e => e.Message.Contains("no Shader Output"));
        var two = Doc(); Add(two, "o2", "Output");
        Assert.Contains(SgAnalyzer.Analyze(two, null).Errors, e => e.NodeId == "o2");
    }

    [Fact]
    public void Textures_and_parameters_are_limited_to_what_the_base_declares()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "t", "TextureSample", ("texture", "BAKED_LIGHT"));
        Add(d, "p", "Parameter", ("name", "ReyProbe"));
        Add(d, "t0", "TextureSample");
        Link(d, "t", "RGB", "n1", SgCatalog.BaseColorPin);
        Link(d, "p", "Value", "n1", SgCatalog.OpacityPin);
        var r = SgAnalyzer.Analyze(d, b);
        Assert.Contains(r.Errors, e => e.NodeId == "t" && e.Message.Contains("not a texture"));
        Assert.Contains(r.Errors, e => e.NodeId == "p" && e.Message.Contains("not a parameter"));
        // a node that nothing reads is only a warning
        Assert.DoesNotContain(r.Errors, e => e.NodeId == "t0");
        Assert.Contains(r.Diagnostics, e => e.NodeId == "t0" && !e.IsError);
    }

    [Fact]
    public void Parameter_width_follows_the_declaration_and_Time_and_VertexColor_need_a_provider()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "strength", "Parameter", ("name", "Strength"));
        Add(d, "tint", "Parameter", ("name", "TintColor"));
        Add(d, "time", "Time");
        Add(d, "vc", "VertexColor");
        Add(d, "uv", "TexCoord", ("set", "1"));
        Add(d, "uv9", "TexCoord", ("set", "9"));
        var r = SgAnalyzer.Analyze(d, b);
        Assert.Equal(1, r.OutputWidth("strength", 0));
        Assert.Equal(4, r.OutputWidth("tint", 0));
        Assert.Equal(1, r.OutputWidth("time", 0));          // TestBase has a TIME
        Assert.Equal(2, r.OutputWidth("uv", 0));
        Assert.Contains(r.Diagnostics, e => e.NodeId == "vc" && e.Message.Contains("COLOR"));
        Assert.Contains(r.Diagnostics, e => e.NodeId == "uv9" && e.Message.Contains("TEXCOORD9"));
        Assert.Contains(SgAnalyzer.Analyze(d, TestBase(withFrame: false)).Diagnostics, e => e.NodeId == "time" && e.Message.Contains("TIME"));
        // TEXCOORD2 exists in one of the two signatures only: a warning that says so
        var d2 = Doc(); Add(d2, "uv2", "TexCoord", ("set", "2")); Link(d2, "uv2", "UV", "n1", SgCatalog.OpacityPin);
        Assert.Contains(SgAnalyzer.Analyze(d2, b).Diagnostics, e => e.NodeId == "uv2" && !e.IsError && e.Message.Contains("1 of 2"));
    }

    // ================================================================================= HLSL

    [Fact]
    public void The_hlsl_for_a_texture_times_a_parameter_is_stable()
    {
        var b = TestBase();
        var r = SgHlsl.Generate(TextureTimesTint(), b, b.Signatures[0]);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        const string expected = """
            // ReyEngine Shader Graph "t" on Shaders/StaticMesh/DefaultEnv_Flat
            // Shipped when a material is assigned to this graph.
            // Pixel input signature #0: SV_Position0:xyzw@r0/sv1/t3 TEXCOORD0:xyzw@r1/sv0/t3 TEXCOORD1:xy@r2/sv0/t3 => SV_Target0:xyzw@r0/sv0/t3 SV_Target1:xyzw@r1/sv0/t3

            // ---- $Globals: base shader parameters the graph reads (the client fills them by name)
            float4 TintColor;

            // ---- textures, named like Riot's (the client binds <Name>__TX and <Name>__SMP by name)
            Texture2D DiffuseTexture__TX;
            SamplerState DiffuseTexture__SMP;

            // ---- the engine's per-frame cbuffer, redeclared with Riot's exact layout
            cbuffer PerFramePixelCB : register(b1)
            {
                float3 vCamera : packoffset(c0.x);
                float4 TIME : packoffset(c1.x);
                uint RENDERTARGET_IS_SRGB : packoffset(c32.x);
            };

            struct PSIn
            {
                float4 pos : SV_Position0;
                float4 tc0 : TEXCOORD0;
                float2 tc1 : TEXCOORD1;
            };

            void main(PSIn i, out float4 o0 : SV_Target0, out float4 o1 : SV_Target1)
            {
                float4 v0 = DiffuseTexture__TX.Sample(DiffuseTexture__SMP, i.tc1.xy);   // Texture Sample
                float4 v1 = TintColor;   // Parameter
                float4 v2 = (v0 * v1);   // Multiply
                float3 v3 = (v2).xyz;   // Component Mask

                float3 baseColor = v3;
                float opacity = v0.a;
                // Riot's colour blobs end with this: a render target flagged sRGB gets the colour raised to 2.2
                if (RENDERTARGET_IS_SRGB != 0) baseColor = pow(max(baseColor, 0.0), 2.2);
                o0 = float4(baseColor, opacity);
                o1 = float4(0.0, 0.0, 0.0, 1.0);   // every cooked colour blob writes this constant to the extra targets
            }

            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), r.Source);
        Assert.All(r.Source, c => Assert.True(c == '\n' || (c >= ' ' && c <= '~'), $"non-ASCII character U+{(int)c:X4} in the generated HLSL"));
        Assert.Equal(new[] { "DiffuseTexture" }, r.Textures);
        Assert.Equal(new[] { "TintColor" }, r.Parameters);
        // every statement line names its node, so a compiler error can land on it
        Assert.Equal("n2", r.LineNodes.Single(kv => r.Source.Split('\n')[kv.Key - 1].Contains("Sample(")).Value);
    }

    [Fact]
    public void Only_nodes_the_output_depends_on_are_emitted_and_defaults_fill_unwired_inputs()
    {
        var b = TestBase(withFrame: false);
        var d = Doc();
        Add(d, "dead", "Constant", ("value", "5 6 7"));
        Add(d, "lerp", "Lerp");
        d.Find("lerp")!.Props["A"] = "0.25";     // an unwired input's own value
        Link(d, "lerp", "Value", "n1", SgCatalog.BaseColorPin);
        var r = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.DoesNotContain("5.0", r.Source);
        Assert.Contains("lerp(0.25, 1.0, 0.5)", r.Source);
        Assert.Contains("float3 baseColor = ((float3)v0);", r.Source);
        Assert.DoesNotContain("cbuffer", r.Source);           // no engine buffer in this base, nothing to redeclare
        Assert.DoesNotContain("RENDERTARGET_IS_SRGB", r.Source);
    }

    [Fact]
    public void A_signature_without_the_interpolant_a_node_needs_is_an_error_for_that_signature_only()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "uv2", "TexCoord", ("set", "2"));
        Add(d, "m", "ComponentMask", ("mask", "x"));
        Link(d, "uv2", "UV", "m", "Value"); Link(d, "m", "Value", "n1", SgCatalog.OpacityPin);
        var a = SgAnalyzer.Analyze(d, b);
        Assert.False(a.HasErrors);
        var s0 = SgHlsl.Generate(d, b, b.Signatures[0], a);
        var s1 = SgHlsl.Generate(d, b, b.Signatures[1], a);
        Assert.False(s0.Ok);
        Assert.Contains(s0.Errors, e => e.NodeId == "uv2" && e.Message.Contains("TEXCOORD2"));
        Assert.True(s1.Ok, string.Join("; ", s1.Errors));
        Assert.Contains("float3 tc2 : TEXCOORD2;", s1.Source);
        Assert.Contains("i.tc2.xy", s1.Source);
    }

    [Fact]
    public void A_graph_with_errors_generates_nothing()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "a", "Constant", ("value", "1 2 3")); Add(d, "b", "Constant", ("value", "1 2")); Add(d, "m", "Add");
        Link(d, "a", "Value", "m", "A"); Link(d, "b", "Value", "m", "B"); Link(d, "m", "Value", "n1", SgCatalog.BaseColorPin);
        var r = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.False(r.Ok);
        Assert.Empty(r.Source);
        Assert.Contains(r.Errors, e => e.NodeId == "m");
    }

    [Fact]
    public void Time_reads_TIME_x_and_declares_the_engine_cbuffer()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "t", "Time"); Add(d, "s", "Sine"); Link(d, "t", "Seconds", "s", "Value"); Link(d, "s", "Value", "n1", SgCatalog.BaseColorPin);
        var r = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.True(r.UsesTime);
        Assert.Contains("float v0 = TIME.x;", r.Source);
        Assert.Contains("sin(v0)", r.Source);
    }

    // ================================================================================= compile (Windows)

    [Fact]
    public void Every_signature_of_the_test_base_compiles_and_matches()
    {
        if (!OperatingSystem.IsWindows()) return;
        var b = TestBase();
        var build = ShaderGraphCompiler.BuildAll(TextureTimesTint(), b);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));
        Assert.Equal(2, build.Compiled.Count);
        foreach (var c in build.Compiled)
        {
            Assert.True(SgSignature.SameList(c.Signature.Inputs, c.Shader!.Inputs));
            Assert.True(SgSignature.SameList(c.Signature.Outputs, c.Shader.Outputs));
            var globals = Assert.Single(c.Shader.ConstantBuffers, cb => cb.Name == "$Globals");
            Assert.Equal(0, globals.BindPoint);
            Assert.Equal("TintColor", Assert.Single(globals.Variables).Name);
            Assert.Contains(c.Shader.Resources, r => r.Name == "DiffuseTexture__TX" && r.Kind == DxbcResourceKind.Texture);
            Assert.Contains(c.Shader.Resources, r => r.Name == "DiffuseTexture__SMP" && r.Kind == DxbcResourceKind.Sampler);
        }
        Assert.Same(build.Compiled[1], build.ForRiotPixel(build.Compiled[1].Shader!));
    }

    [Fact]
    public void A_compiler_message_is_mapped_to_the_node_whose_line_it_names()
    {
        var b = TestBase();
        var good = SgHlsl.Generate(TextureTimesTint(), b, b.Signatures[0]);
        // break the generated line of the Multiply node, as a generator bug would, and compile for real
        var lines = good.Source.Split('\n');
        int mul = Array.FindIndex(lines, l => l.Contains("// Multiply"));
        lines[mul] = "    float4 v2 = (v0 * undeclared_thing);   // Multiply";
        string broken = string.Join('\n', lines);
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(ShaderGraphCompiler.TryCompile(broken, "main", "ps_5_0", out _, out string log));
        var errs = ShaderGraphCompiler.ParseCompilerErrors(log, good);
        var e = Assert.Single(errs, x => x.Message.Contains("undeclared_thing"));
        Assert.Equal("n4", e.NodeId);
        Assert.Contains($"HLSL line {mul + 1}", e.Message);
    }

    [Fact]
    public void A_compiler_log_without_a_line_still_becomes_one_error()
    {
        var b = TestBase();
        var hlsl = SgHlsl.Generate(TextureTimesTint(), b, b.Signatures[0]);
        var errs = ShaderGraphCompiler.ParseCompilerErrors("the compiler crashed", hlsl);
        Assert.Single(errs);
        Assert.Contains("the compiler crashed", errs[0].Message);
    }

    // ================================================================================= document

    [Fact]
    public void A_document_round_trips_byte_for_byte_and_keeps_every_node_wire_and_setting()
    {
        var d = TextureTimesTint();
        d.Name = "Round Trip";
        d.Find("n2")!.X = 12.5; d.Find("n2")!.Y = -40;
        d.Find("n4")!.Props["A"] = "0.75";
        string json = ShaderGraphJson.Serialize(d);
        var back = ShaderGraphJson.TryDeserialize(json, out var error);
        Assert.NotNull(back);
        Assert.Null(error);
        Assert.Equal(json, ShaderGraphJson.Serialize(back!));
        Assert.Equal(d.Nodes.Count, back!.Nodes.Count);
        Assert.Equal(d.Links.Count, back.Links.Count);
        Assert.Equal("DiffuseTexture", back.Find("n2")!.Prop("texture"));
        Assert.Equal(12.5, back.Find("n2")!.X);
        Assert.Equal("0.75", back.Find("n4")!.Prop("A"));
        Assert.Equal("Shaders/StaticMesh/DefaultEnv_Flat", back.BaseShader);
        Assert.Equal(1, back.Version);
        Assert.Contains("\"version\": 1", json);
    }

    [Fact]
    public void Unknown_node_types_survive_a_round_trip_and_are_reported_not_dropped()
    {
        var d = Doc();
        Add(d, "x", "FutureNode", ("k", "v"));
        var back = ShaderGraphJson.TryDeserialize(ShaderGraphJson.Serialize(d), out _)!;
        Assert.Equal("FutureNode", back.Find("x")!.Type);
        Assert.Equal("v", back.Find("x")!.Prop("k"));
        Assert.Contains(SgAnalyzer.Analyze(back, null).Diagnostics, e => e.NodeId == "x" && e.Message.Contains("unknown node type"));   // not connected: a warning, never dropped
    }

    [Theory]
    [InlineData("{ \"version\": 2, \"nodes\": [], \"links\": [] }", "version 2")]
    [InlineData("{ \"version\": 1, \"nodes\": [ { \"id\": \"a\", \"type\": \"Add\" }, { \"id\": \"a\", \"type\": \"Add\" } ], \"links\": [] }", "repeated")]
    [InlineData("not json", "not a Shader Graph file")]
    [InlineData("{ \"version\": 1, \"nodes\": [ { \"id\": \"\", \"type\": \"Add\" } ], \"links\": [] }", "empty or repeated")]
    public void A_document_this_build_cannot_read_is_refused_with_a_reason(string json, string reason)
    {
        Assert.Null(ShaderGraphJson.TryDeserialize(json, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Too_many_nodes_are_refused_before_anything_is_built()
    {
        var d = Doc();
        for (int i = 0; i < ShaderGraphDocument.MaxNodes + 1; i++) d.Nodes.Add(new SgNode { Id = "q" + i, Type = "Add" });
        Assert.Null(ShaderGraphJson.TryDeserialize(ShaderGraphJson.Serialize(d), out var error));
        Assert.Contains("nodes", error);
    }

    [Fact]
    public void A_graph_lives_in_the_projects_editor_data_folder_which_the_packers_skip()
    {
        string path = ShaderGraphDocument.PathIn(@"C:\proj", "My Graph/1");
        Assert.Equal(Path.Combine(@"C:\proj", ".reyengine", "shadergraphs", "My_Graph_1.shadergraph.json"), path);
        Assert.Equal("ShaderGraph", ShaderGraphDocument.SafeFileStem("  "));

        // Build Package / the asset scan skip .reyengine/ entirely: look at the real rule in the sources
        string root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ReyEngine.slnx"))) root = Path.GetDirectoryName(root)!;
        if (root is null) return;
        string pack = File.ReadAllText(Path.Combine(root, "src", "ReyEngine.Core", "Build", "WadPackService.cs"));
        string mount = File.ReadAllText(Path.Combine(root, "src", "ReyEngine.Core", "Assets", "AssetMount.cs"));
        Assert.Contains("rel.StartsWith(\".reyengine/\"", pack);
        Assert.Contains(".reyengine", mount);
        // and nothing in the Shader Graph writes into a bin or the shader cache
        foreach (string f in Directory.GetFiles(Path.Combine(root, "src", "ReyEngine.Formats", "Materials", "ShaderGraph"), "*.cs"))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("StoreOverrideBytes", text);
            // M833: ONE file ships a graph (the pack-time TOC/container builder); the editor's own files still never touch the cache
            if (Path.GetFileName(f) == "ShaderGraphShip.cs") continue;
            Assert.DoesNotContain("REY_GRAPH", text);
            Assert.DoesNotContain("ShaderCachePatchWriter", text);
        }
    }

    // ================================================================================= palette and projection

    [Fact]
    public void The_palette_lists_declared_inputs_and_every_math_node_and_hides_what_the_base_lacks()
    {
        var b = TestBase();
        var pal = SgCatalog.Palette(b);
        string[] titles = pal.Select(p => p.Title).ToArray();
        Assert.Contains("Texture Sample: DiffuseTexture", titles);
        Assert.Contains("Texture Sample: DetailTexture", titles);
        Assert.Contains("Parameter: TintColor", titles);
        Assert.Contains("TexCoord 1 (UV0)", titles);
        Assert.Contains("Time", titles);
        Assert.DoesNotContain("Vertex Color", titles);        // the test base writes no COLOR
        foreach (string t in new[] { "Add", "Subtract", "Multiply", "Divide", "Lerp", "Power", "Saturate", "One Minus", "Abs", "Min", "Max", "Clamp", "Dot", "Normalize", "Sine", "Component Mask", "Append", "Split" })
            Assert.Contains(t, titles);
        Assert.Contains("Constant (float)", titles); Assert.Contains("Constant2 (float2)", titles); Assert.Contains("Constant3 (float3)", titles); Assert.Contains("Constant4 (float4)", titles);

        Assert.Equal("Lerp", SgCatalog.Search(pal, "lerp")[0].Title);
        Assert.Contains(SgCatalog.Search(pal, "tint"), p => p.Title == "Parameter: TintColor");
        Assert.Empty(SgCatalog.Search(pal, "zzzz"));
        Assert.Equal(pal.Count, SgCatalog.Search(pal, "").Count);
    }

    [Fact]
    public void The_canvas_projection_marks_error_nodes_and_wires_and_dims_unconnected_ones()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "a", "Constant", ("value", "1 2 3")); Add(d, "c", "Constant", ("value", "1 2")); Add(d, "m", "Add");
        Add(d, "orphan", "Constant", ("value", "0.5"));
        Link(d, "a", "Value", "m", "A"); Link(d, "c", "Value", "m", "B"); Link(d, "m", "Value", "n1", SgCatalog.BaseColorPin);
        var analysis = SgAnalyzer.Analyze(d, b);
        var g = SgGraphView.Build(d, b, analysis);
        Assert.Equal(GraphNodeState.Error, g.Find("m")!.State);
        Assert.Contains("only a scalar", g.Find("m")!.Message);
        Assert.Equal(GraphNodeState.Unused, g.Find("orphan")!.State);
        Assert.Equal(GraphNodeKind.Math, g.Find("m")!.Kind);
        Assert.Equal(GraphNodeKind.Input, g.Find("a")!.Kind);
        Assert.Equal(GraphNodeKind.Output, g.Find("n1")!.Kind);
        Assert.Equal(3, g.Wires.Count);
        // both operands of the mismatch are drawn red; the wire that merely carries the broken node's (absent) result is not
        Assert.All(g.Wires.Where(w => w.ToNode == "m"), w => Assert.True(w.Error));
        Assert.False(g.Wires.Single(w => w.ToNode == "n1").Error);
        // typed pins: width 1 is scalar, wider is a vector, Base Color is a colour
        Assert.Equal(GraphPinKind.Vector, g.Find("a")!.Outputs[0].Kind);
        Assert.Equal(GraphPinKind.Color, g.Find("n1")!.Inputs[0].Kind);
        Assert.True(g.Find("m")!.Inputs[0].Linked);
        Assert.Equal("float3", g.Find("m")!.Inputs[0].Detail);
        Assert.True(g.Bounds.Width > 0);
    }

    [Fact]
    public void The_hlsl_generator_never_emits_a_literal_the_compiler_could_misread()
    {
        Assert.Equal("1.0", SgHlsl.Float(1f));
        Assert.Equal("0.5", SgHlsl.Float(0.5f));
        Assert.Equal("-2.0", SgHlsl.Float(-2f));
        Assert.Contains(SgHlsl.Float(1e-7f), new[] { "1E-07", "1e-07", "1E-7", "1e-7" });
    }

    // ================================================================================= the markup

    private static string? Source(params string[] parts)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ReyEngine.slnx"))) root = Path.GetDirectoryName(root);
        if (root is null) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Avalonia resolves a binding at runtime, so a typo passes the build: every name the window binds for the Shader Graph is a real
    /// member (the headless window probe drives the real view; this pins the names), the code-behind handlers exist, and the markup holds
    /// no literal colour of its own (the graph is coloured by the ReyGraph* theme keys every palette carries).</summary>
    [Fact]
    public void The_window_markup_binds_only_members_that_exist_and_adds_no_literal_colours()
    {
        string? xaml = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml");
        string? code = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml.cs");
        if (xaml is null || code is null) return;
        const System.Reflection.BindingFlags pub = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding !?ShaderGraph\.([A-Za-z]+)"))
            Assert.True(typeof(ReyEngine.App.ViewModels.ShaderGraphViewModel).GetProperty(m.Groups[1].Value, pub) is not null, "ShaderGraphViewModel has no " + m.Groups[1].Value);
        foreach (string name in new[] { "NewShaderGraphCommand", "CloseShaderGraphCommand", "CanNewShaderGraph", "ShaderGraphActive", "MaterialGraphActive", "GraphPreviewNote", "ShaderGraph" })
        {
            Assert.Contains("{Binding " + name, xaml);
            Assert.True(typeof(ReyEngine.App.ViewModels.ShaderPreviewViewModel).GetProperty(name, pub) is not null, "ShaderPreviewViewModel has no " + name);
        }
        foreach (string member in new[] { "Text", "Choices", "HasChoices", "Hint", "Label", "CommitCommand" })
            Assert.True(typeof(ReyEngine.App.ViewModels.SgPropRow).GetProperty(member, pub) is not null, "SgPropRow has no " + member);
        foreach (string handler in new[] { "OnOpenShaderGraphClick", "OnSavedGraphClick" })
        {
            Assert.Contains("=\"" + handler + "\"", xaml);
            Assert.Contains("void " + handler + "(", code);
        }
        Assert.Contains("x:Name=\"ShaderGraphCanvas\"", xaml);
        Assert.Contains("ShaderEditor=\"{Binding ShaderGraph}\"", xaml);
        Assert.Contains("Shader Graph HLSL (generated)", xaml);
        Assert.Contains("Graphs ship when assigned: they are rebuilt from the installed game on every export, so re-export after each patch.", xaml);

        // the one literal colour that was there before (the preview surface's hit-test fill) is the only one
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, "=\"#[0-9A-Fa-f]{3,8}\""));
        // and no control characters crept in through a scripted edit
        Assert.DoesNotContain(xaml, c => c < ' ' && c is not ('\n' or '\r' or '\t'));
    }

    // ================================================================================= hostile files

    [Fact]
    public void Null_links_nodes_and_values_in_a_file_are_refused_or_read_as_empty_and_never_throw_later()
    {
        Assert.Null(ShaderGraphJson.TryDeserialize("{ \"version\": 1, \"nodes\": [], \"links\": [ null ] }", out var e1));
        Assert.Contains("link entry is null", e1);
        Assert.Null(ShaderGraphJson.TryDeserialize("{ \"version\": 1, \"nodes\": [ null ], \"links\": [] }", out var e2));
        Assert.Contains("node entry is null", e2);

        var doc = ShaderGraphJson.TryDeserialize(
            "{ \"version\": 1, \"name\": null, \"baseShader\": null, \"nodes\": [ { \"id\": \"o\", \"type\": \"Output\", \"props\": null }, "
            + "{ \"id\": \"a\", \"type\": null, \"props\": { \"A\": null, \"texture\": null } } ], "
            + "\"links\": [ { \"fromNode\": null, \"fromPin\": null, \"toNode\": null, \"toPin\": null }, { \"fromNode\": \"a\", \"toNode\": \"o\" } ] }", out var err);
        Assert.NotNull(doc);
        Assert.Null(err);
        Assert.Equal("", doc!.Find("a")!.Prop("A", "x"));
        Assert.All(doc.Links, l => { Assert.NotNull(l.FromNode); Assert.NotNull(l.FromPin); Assert.NotNull(l.ToNode); Assert.NotNull(l.ToPin); });
        var a = SgAnalyzer.Analyze(doc, TestBase());          // reported, not thrown
        Assert.NotNull(a);
        Assert.Contains(a.Diagnostics, d => d.NodeId == "a");
        SgGraphView.Build(doc, TestBase(), a);
        SgHlsl.Generate(doc, TestBase(), TestBase().Signatures[0]);
    }

    [Fact]
    public void An_oversized_setting_or_file_is_refused()
    {
        string longValue = new('x', ShaderGraphDocument.MaxPropLength + 1);
        Assert.Null(ShaderGraphJson.TryDeserialize("{ \"version\": 1, \"nodes\": [ { \"id\": \"a\", \"type\": \"Add\", \"props\": { \"A\": \"" + longValue + "\" } } ], \"links\": [] }", out var error));
        Assert.Contains("too long", error);
        Assert.Null(ShaderGraphJson.TryDeserialize(new string(' ', ShaderGraphDocument.MaxFileBytes + 1), out error));
        Assert.Contains("too large", error);
    }

    [Fact]
    public void A_long_chain_of_nodes_is_analysed_without_recursion_and_a_loop_is_still_found()
    {
        var b = TestBase();
        var d = Doc();
        Add(d, "c0", "Constant", ("value", "1"));
        string prev = "c0";
        for (int i = 1; i < 4000; i++)
        {
            Add(d, "c" + i, "Saturate");
            Link(d, prev, "Value", "c" + i, "Value");
            prev = "c" + i;
        }
        Link(d, prev, "Value", "n1", SgCatalog.OpacityPin);
        var r = SgAnalyzer.Analyze(d, b);
        Assert.False(r.HasErrors);
        Assert.Equal(1, r.OutputWidth(prev, 0));
        Assert.Equal(4002 - 1, r.Order.Count);            // 4000 chain nodes + the output node, dependencies first
        Assert.Equal("c0", r.Order[0]);
        Assert.Equal("n1", r.Order[^1]);
        // close the chain into a loop
        Link(d, prev, "Value", "c1", "Value");
        Assert.Contains(SgAnalyzer.Analyze(d, b).Errors, e => e.Message.Contains("loop"));
    }
}
