using System.Globalization;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>M835: one starting point offered by "New Shader Graph".</summary>
public sealed record SgTemplate(string Id, string Title, string Description);

/// <summary>
/// M835: the starting graphs of "New Shader Graph" (Unreal's material templates). Every template is a valid graph on the base shader that compiles for all of
/// its pixel input signatures and is laid out in columns, left to right. The texture is the base's <c>DiffuseTexture</c> (else its first declared texture); a template
/// that needs a second or third texture declares custom textures (which render in the editor, and export once custom textures are proven in game).
/// </summary>
public static class SgTemplates
{
    public const string Riot = "riot", Tinted = "tinted", Scroll = "scroll", Blend = "blend", Pulse = "pulse", Empty = "empty";

    public static IReadOnlyList<SgTemplate> All { get; } = new[]
    {
        new SgTemplate(Riot, "Riot look (Texture x TintColor overlay, identical to Riot's shader)",
            "Reproduces what Riot's DefaultEnv_Flat does: the texture blended with TintColor by an Overlay. Start here to change one thing about the stock look."),
        new SgTemplate(Tinted, "Tinted texture",
            "The texture multiplied by a colour parameter called Tint. A material sets the colour."),
        new SgTemplate(Scroll, "Scrolling texture (Panner)",
            "The texture slides across the surface: a Panner moves the UVs with the engine time."),
        new SgTemplate(Blend, "Two-texture blend by mask",
            "Two textures mixed by a third (a mask): black shows the first, white the second. The second and the mask are custom textures."),
        new SgTemplate(Pulse, "Pulsing emissive (Time + Sine)",
            "The texture plus a glow colour that pulses with Time and a Sine."),
        new SgTemplate(Empty, "Empty",
            "Only the Shader Output. Black until you wire something in."),
    };

    private const double C = 250;   // column width

    /// <summary>Why a template cannot be built on <paramref name="b"/> (it needs a texture, or the engine time), or null.</summary>
    public static string? Problem(string id, SgBase b)
    {
        if (id == Empty) return null;
        if (b.Textures.Count == 0) return "the base shader declares no texture";
        if (id == Pulse && !b.HasTime) return "the base shader has no engine time";
        return null;
    }

    public static ShaderGraphDocument Build(string id, string name, SgBase b)
    {
        var d = ShaderGraphDocument.CreateEmpty(name, b.Shader);
        if (!All.Any(t => t.Id == id)) throw new ArgumentException($"unknown template '{id}'", nameof(id));
        if (Problem(id, b) is { } why) throw new InvalidOperationException($"The '{id}' template needs more than {b.Shader} has: {why}.");
        if (id == Empty) { d.Nodes[0].X = 300; d.Nodes[0].Y = 40; return d; }

        string tex = (b.Texture("DiffuseTexture") ?? b.Textures[0]).Name;
        int n = 1;
        string N(string type, double col, double y, params (string, string)[] props)
        {
            string nid = "n" + (++n);
            var node = new SgNode { Id = nid, Type = type, X = col * C, Y = y };
            foreach (var (k, v) in props) node.Props[k] = v;
            d.Nodes.Add(node);
            return nid;
        }
        void L(string from, string fp, string to, string tp) => d.Links.Add(new SgLink { FromNode = from, FromPin = fp, ToNode = to, ToPin = tp });
        void Out(double col, double y) { d.Nodes[0].X = col * C; d.Nodes[0].Y = y; }
        const string BC = SgCatalog.BaseColorPin, OP = SgCatalog.OpacityPin;

        switch (id)
        {
            case Riot:
            {
                var t = N("TextureSample", 0, 60, ("texture", tex));
                if (b.Parameter("TintColor") is null)
                {
                    // a base without TintColor: the texture alone
                    L(t, "RGB", "n1", BC); L(t, "A", "n1", OP); Out(1.4, 60);
                    break;
                }
                var p = N("Parameter", 0, 400, ("name", "TintColor"));
                var pm = N("ComponentMask", 1, 400, ("mask", "rgb"));
                var half = N("Constant", 1, -150, ("value", "0.5"));
                var two = N("Constant", 1, 600, ("value", "2"));
                var mul = N("Multiply", 2, -40); var it = N("OneMinus", 2, 140); var ip = N("OneMinus", 2, 330);
                var step = N("Step", 3, -170); var lo = N("Multiply", 3, -40); var m2 = N("Multiply", 3, 230);
                var hi2 = N("Multiply", 4, 230); var hi = N("OneMinus", 5, 230);
                var mix = N("Lerp", 6, 40);
                L(p, "Value", pm, "Value");
                L(t, "RGB", mul, "A"); L(pm, "Value", mul, "B"); L(mul, "Value", lo, "A"); L(two, "Value", lo, "B");
                L(t, "RGB", it, "Value"); L(pm, "Value", ip, "Value"); L(it, "Value", m2, "A"); L(ip, "Value", m2, "B");
                L(m2, "Value", hi2, "A"); L(two, "Value", hi2, "B"); L(hi2, "Value", hi, "Value");
                L(half, "Value", step, "Edge"); L(t, "RGB", step, "X");
                L(lo, "Value", mix, "A"); L(hi, "Value", mix, "B"); L(step, "Value", mix, "Alpha");
                L(mix, "Value", "n1", BC); L(t, "A", "n1", OP);
                Out(7, 40);
                break;
            }
            case Tinted:
            {
                d.Params.Add(new SgCustomParam { Name = "Tint", Components = 3, IsColor = true, Default = "1 0.6 0.35" });
                var t = N("TextureSample", 0, 40, ("texture", tex));
                var p = N("Parameter", 0, 330, ("name", "Tint"));
                var mul = N("Multiply", 1.4, 130);
                L(t, "RGB", mul, "A"); L(p, "Value", mul, "B");
                L(mul, "Value", "n1", BC); L(t, "A", "n1", OP);
                Out(2.8, 110);
                break;
            }
            case Scroll:
            {
                var sx = N("Constant", 0, 200, ("value", "0.15"));
                var sy = N("Constant", 0, 330, ("value", "0"));
                var app = N("Append", 1, 250);
                var pan = N("Panner", 2, 120);
                var t = N("TextureSample", 3, 60, ("texture", tex));
                L(sx, "Value", app, "A"); L(sy, "Value", app, "B"); L(app, "Value", pan, "Speed");
                L(pan, "UV", t, "UV");
                L(t, "RGB", "n1", BC); L(t, "A", "n1", OP);
                Out(4.2, 80);
                break;
            }
            case Blend:
            {
                d.Textures.Add(new SgCustomTexture { Name = "SecondTexture" });
                d.Textures.Add(new SgCustomTexture { Name = "MaskTexture" });
                var a = N("TextureSample", 0, 0, ("texture", tex));
                var s = N("TextureSample", 0, 290, ("texture", "SecondTexture"));
                var m = N("TextureSample", 0, 580, ("texture", "MaskTexture"));
                var lerp = N("Lerp", 1.5, 200);
                L(a, "RGB", lerp, "A"); L(s, "RGB", lerp, "B"); L(m, "R", lerp, "Alpha");
                L(lerp, "Value", "n1", BC); L(a, "A", "n1", OP);
                Out(3, 180);
                break;
            }
            case Pulse:
            {
                d.Params.Add(new SgCustomParam { Name = "GlowColor", Components = 3, IsColor = true, Default = "1 0.4 0.1" });
                var t = N("TextureSample", 0, 0, ("texture", tex));
                var time = N("Time", 0, 330);
                var speed = N("Multiply", 1, 330, ("B", "3"));
                var sine = N("Sine", 2, 330);
                var half = N("Multiply", 3, 330, ("B", "0.5"));
                var up = N("Add", 4, 330, ("B", "0.5"));
                var glow = N("Parameter", 3, 560, ("name", "GlowColor"));
                var gm = N("Multiply", 5, 430);
                var add = N("Add", 6, 120);
                L(time, "Seconds", speed, "A"); L(speed, "Value", sine, "Value"); L(sine, "Value", half, "A"); L(half, "Value", up, "A");
                L(glow, "Value", gm, "A"); L(up, "Value", gm, "B");
                L(t, "RGB", add, "A"); L(gm, "Value", add, "B");
                L(add, "Value", "n1", BC); L(t, "A", "n1", OP);
                Out(7.2, 100);
                break;
            }
        }
        return d;
    }

    /// <summary>Format a value the way node settings are stored (used by the editor when it creates constants).</summary>
    public static string Number(float f) => f.ToString("R", CultureInfo.InvariantCulture);
}
