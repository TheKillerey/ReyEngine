namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>A palette line that can be wired to a pin that was dragged into empty space, and the pin of the new node the wire ends at.</summary>
public sealed record SgAutoMatch(SgPaletteEntry Entry, string Pin, int Width, bool Exact);

/// <summary>
/// M835: Unreal's "drag a wire into empty space": which nodes fit the pin the wire came from. The rules are the analyser's:
/// a scalar broadcasts to any width; a vector must meet a pin of its own width, or a pin that takes any width (it adopts the width it is given).
/// <list type="bullet">
/// <item>From an OUTPUT pin of width w: a new node qualifies when one of its inputs takes w (Fixed = w, Fixed = 0, or w = 1). The first exact (Fixed = w), else the first width-agnostic, input is the one wired.</item>
/// <item>From an unwired INPUT pin that takes <c>fixedWidth</c> (0 = any): a new node qualifies when one of its outputs is that wide, or a scalar, or width-agnostic (math nodes). The exact output is preferred.</item>
/// </list>
/// The Shader Output node is never offered (a graph has one).
/// </summary>
public static class SgAutoConnect
{
    /// <summary>The width of a node output as the palette can know it before any wire arrives: >0 fixed, 0 = it follows its inputs (any).</summary>
    public static int[] OutputWidths(SgPaletteEntry entry, ShaderGraphDocument doc, SgBase? b)
    {
        var def = SgCatalog.Get(entry.Type);
        if (def is null || entry.Type == SgCatalog.OutputType) return Array.Empty<int>();
        switch (entry.Type)
        {
            case "TextureSample": return new[] { 4, 3, 1, 1, 1, 1 };
            case "Split": return new[] { 1, 1, 1, 1 };
            case "Dot" or "Length" or "Distance" or "Time": return new[] { 1 };
            case "Cross" or "Desaturation": return new[] { 3 };
            case "Panner" or "Rotator" or "TilingOffset" or "TexCoord": return new[] { 2 };
            case "VertexColor" or "ConstantColor": return new[] { 4 };
            case "ComponentMask": return new[] { SgCatalog.ParseMask(entry.Props.GetValueOrDefault("mask", "rgb"))?.Length ?? 3 };
            case "Constant": return new[] { SgCatalog.TryParseConstant(entry.Props.GetValueOrDefault("value", "1"), out var v) ? v.Length : 1 };
            case "Parameter":
            {
                if (entry.Props.TryGetValue(SgCatalog.NewParamKey, out var kind) && SgCustomParams.ParseKind(kind) is { } pk) return new[] { pk.Components };
                string name = entry.Props.GetValueOrDefault("name", "");
                if (doc.FindParam(name) is { } cp) return new[] { Math.Clamp(cp.Components, 1, 4) };
                return new[] { b?.Parameter(name)?.Components ?? 4 };
            }
            case "Custom": return new[] { SgCustom.OutWidth(entry.Props.GetValueOrDefault("out", SgCustom.DefaultOut)) };
            case "Append": return new[] { 0 };
        }
        // a math node: as wide as what is wired into it
        return def.Outputs.Select(_ => def.Inputs.Any(i => i.Fixed == 0) ? 0 : def.Inputs.Max(i => i.Fixed)).ToArray();
    }

    private static bool Takes(SgPinDef pin, int w) => pin.Fixed == 0 || w <= 1 || pin.Fixed == w;

    /// <summary>The palette lines that accept a wire from an output pin of <paramref name="width"/> floats, best first.</summary>
    public static IReadOnlyList<SgAutoMatch> ForOutput(IReadOnlyList<SgPaletteEntry> palette, ShaderGraphDocument doc, SgBase? b, int width)
    {
        var list = new List<SgAutoMatch>();
        foreach (var e in palette)
        {
            if (e.Type == SgCatalog.OutputType) continue;
            var def = SgCatalog.Get(e.Type);
            if (def is null || def.Inputs.Count == 0) continue;
            SgPinDef? pick = def.Inputs.FirstOrDefault(p => p.Fixed == width && width > 1)
                             ?? def.Inputs.FirstOrDefault(p => p.Fixed == 0 && !SgCatalog.IsUvPin(new SgNode { Type = e.Type }, p) && !SgCatalog.IsTimePin(new SgNode { Type = e.Type }, p))
                             ?? def.Inputs.FirstOrDefault(p => Takes(p, width));
            if (e.Type == SgCustom.Type)
            {
                SgCustom.TryParseInputs(e.Props.GetValueOrDefault("inputs", SgCustom.DefaultInputs), out var ins, out _);
                var cin = ins.FirstOrDefault(i => i.Components == width) ?? ins.FirstOrDefault(i => width <= 1 || i.Components == width);
                if (cin is null) continue;
                list.Add(new SgAutoMatch(e, cin.Name, width, cin.Components == width));
                continue;
            }
            if (pick is null || !Takes(pick, width)) continue;
            list.Add(new SgAutoMatch(e, pick.Name, width, pick.Fixed == width));
        }
        return list.OrderByDescending(m => m.Exact).ToList();
    }

    /// <summary>The palette lines that can feed an unwired input pin that takes <paramref name="fixedWidth"/> floats (0 = any), best first.</summary>
    public static IReadOnlyList<SgAutoMatch> ForInput(IReadOnlyList<SgPaletteEntry> palette, ShaderGraphDocument doc, SgBase? b, int fixedWidth)
    {
        var list = new List<SgAutoMatch>();
        foreach (var e in palette)
        {
            if (e.Type == SgCatalog.OutputType) continue;
            var def = SgCatalog.Get(e.Type);
            if (def is null || def.Outputs.Count == 0) continue;
            var widths = OutputWidths(e, doc, b);
            int pick = -1; bool exact = false;
            for (int i = 0; i < widths.Length; i++)
            {
                if (fixedWidth > 0 && widths[i] == fixedWidth) { pick = i; exact = true; break; }
                if (pick < 0 && (fixedWidth == 0 || widths[i] == 1 || widths[i] == 0)) pick = i;
            }
            if (pick < 0) continue;
            list.Add(new SgAutoMatch(e, def.Outputs[pick], widths[pick], exact));
        }
        return list.OrderByDescending(m => m.Exact).ToList();
    }

    /// <summary>The compatible lines whose title, category or keywords match <paramref name="filter"/>.</summary>
    public static IReadOnlyList<SgAutoMatch> Filter(IReadOnlyList<SgAutoMatch> matches, string? filter)
    {
        var found = SgCatalog.Search(matches.Select(m => m.Entry).ToList(), filter);
        return found.Select(e => matches.First(m => ReferenceEquals(m.Entry, e))).ToList();
    }
}
