using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using MacroSupport = ReyEngine.Formats.MapGeo.LegacyMapPorter.MacroSupport;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M504: presets and bulk apply.
///
/// <para>The property that matters most here is that PLAN and APPLY agree. This is the tool that edits
/// twenty materials at once, and the last time material state was copied wholesale — M482, copying a
/// donor's techniques — 75 meshes turned black with nothing saying so until the game was launched. A
/// preview computed by different code than the edit is a preview that can lie, so the tests below pin the
/// two together rather than testing each on its own.</para>
/// </summary>
public sealed class MaterialPresetTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private sealed record Sampler(string Name, string Path, int? AddressW = null);

    /// <summary>Build a material through the real parser, so the bindings under test have the same shape
    /// the app sees. A hand-built MaterialBinding would test a form that never occurs.</summary>
    private static MaterialBinding Material(string name, string shader,
        Sampler[]? samplers = null,
        (string Name, Vector4 Value)[]? parameters = null,
        (string Name, bool On)[]? switches = null,
        (string Name, string Value)[]? macros = null,
        bool blendEnable = false, bool? cullEnable = null, int srcBlend = -1, int dstBlend = -1)
    {
        var pass = new List<BinTreeProperty> { new BinTreeObjectLink(H("shader"), H(shader)) };
        if (blendEnable) pass.Add(new BinTreeBool(H("blendEnable"), true));
        if (cullEnable is { } c) pass.Add(new BinTreeBool(H("cullEnable"), c));
        if (srcBlend >= 0) pass.Add(new BinTreeU8(H("srcColorBlendFactor"), (byte)srcBlend));
        if (dstBlend >= 0) pass.Add(new BinTreeU8(H("dstColorBlendFactor"), (byte)dstBlend));

        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), name),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), pass),
                    }),
                }),
            }),
        };

        samplers ??= new[] { new Sampler("DiffuseTexture", "assets/diffuse.tex") };
        if (samplers.Length > 0)
            props.Add(new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                samplers.Select(s =>
                {
                    var fields = new List<BinTreeProperty>
                    {
                        new BinTreeString(H("TextureName"), s.Name),
                        new BinTreeString(H("texturePath"), s.Path),
                    };
                    if (s.AddressW is { } w) fields.Add(new BinTreeU32(H("addressW"), (uint)w));
                    return (BinTreeProperty)new BinTreeEmbedded(0, 0x0904b150, fields);
                }).ToArray()));

        if (parameters is { Length: > 0 })
            props.Add(new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded,
                parameters.Select(p => (BinTreeProperty)new BinTreeEmbedded(0, 0xde480eef,
                    new BinTreeProperty[]
                    {
                        new BinTreeString(H("name"), p.Name),
                        new BinTreeVector4(H("value"), p.Value),
                    })).ToArray()));

        if (switches is { Length: > 0 })
            props.Add(new BinTreeContainer(H("switches"), BinPropertyType.Embedded,
                switches.Select(s => (BinTreeProperty)new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"),
                    new BinTreeProperty[]
                    {
                        new BinTreeString(H("name"), s.Name),
                        new BinTreeBool(H("on"), s.On),
                    })).ToArray()));

        if (macros is { Length: > 0 })
            props.Add(new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                macros.Select(m => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeString(0, m.Name), new BinTreeString(0, m.Value))).ToList()));

        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H(name), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);

        // The CLASS hash has to resolve or a sampler-less material is dropped by the parser (M503a).
        var names = new Dictionary<uint, string>
        {
            [H("StaticMaterialDef")] = "StaticMaterialDef",
            [H(shader)] = shader,
            [H("Shaders/Flat")] = "Shaders/Flat",
            [H("Shaders/AlphaTest")] = "Shaders/AlphaTest",
            [H("Shaders/FourBlend")] = "Shaders/FourBlend",
        };
        return MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();
    }

    /// <summary>A shader cache stand-in: everything is cooked except the pairs listed.</summary>
    private static MaterialPresetContext Cache(
        (string Shader, string Macro)? uncooked = null,
        (string Shader, string Macro)? undeclared = null,
        Func<string, bool>? textureExists = null)
        => new(textureExists, (shader, _, macro, _) =>
        {
            if (undeclared is { } u && shader == u.Shader && macro == u.Macro) return MacroSupport.NotDeclared;
            if (uncooked is { } n && shader == n.Shader && macro == n.Macro) return MacroSupport.NotCooked;
            return MacroSupport.Cooked;
        });

    // ---- capture ---------------------------------------------------------------------------------

    [Fact]
    public void CaptureSnapshotsEveryFieldTheApplierCanWrite()
    {
        var source = Material("Source", "Shaders/Flat",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/a.tex", AddressW: 1) },
            parameters: new[] { ("TintColor", new Vector4(0.5019608f, 0.5019608f, 0.5019608f, 1f)) },
            switches: new[] { ("USE_MASK", true) },
            macros: new[] { ("NO_BAKED_LIGHTING", "1") },
            blendEnable: true, cullEnable: false, srcBlend: 6, dstBlend: 7);

        var preset = MaterialPreset.Capture(source, "Jade terrain");

        Assert.Equal("Jade terrain", preset.Name);
        Assert.Equal("Shaders/Flat", preset.Shader);
        Assert.Equal("Source", preset.SourceMaterial);

        var sampler = Assert.Single(preset.Samplers);
        Assert.Equal("DiffuseTexture", sampler.Name);
        Assert.Equal("assets/a.tex", sampler.Path);
        // Absent stays absent: U and V were never authored, and writing them as 0 would be a different
        // file from the one we captured (M490/M493).
        Assert.Null(sampler.AddressU);
        Assert.Null(sampler.AddressV);
        Assert.Equal(1, sampler.AddressW);

        Assert.Equal("TintColor", Assert.Single(preset.Parameters).Name);
        Assert.True(preset.Switches["USE_MASK"]);
        Assert.Equal("1", preset.Macros["NO_BAKED_LIGHTING"]);

        var state = Assert.IsType<MaterialPresetRenderState>(preset.RenderState);
        Assert.True(state.BlendEnable);
        Assert.False(state.CullEnable);
        Assert.Equal(6, state.SrcBlendFactor);
        Assert.Equal(7, state.DstBlendFactor);

        Assert.Equal(MaterialPresetParts.All, preset.AvailableParts);
    }

    [Fact]
    public void AvailablePartsOnlyOffersWhatThePresetCanActuallyDeliver()
    {
        // A preset with no parameters must not offer a "Parameters" tick box: an empty part that silently
        // does nothing is indistinguishable from one that failed.
        var preset = MaterialPreset.Capture(Material("Plain", "Shaders/Flat"), "Plain");

        Assert.True(preset.AvailableParts.HasFlag(MaterialPresetParts.Shader));
        Assert.True(preset.AvailableParts.HasFlag(MaterialPresetParts.Samplers));
        Assert.False(preset.AvailableParts.HasFlag(MaterialPresetParts.Parameters));
        Assert.False(preset.AvailableParts.HasFlag(MaterialPresetParts.Macros));
    }

    // ---- plan vs apply ---------------------------------------------------------------------------

    [Fact]
    public void PlanChangesNothingAndReportsExactlyWhatApplyThenDoes()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                samplers: new[] { new Sampler("DiffuseTexture", "assets/new.tex", AddressW: 1) },
                parameters: new[] { ("TintColor", new Vector4(0.5019608f, 0.5019608f, 0.5019608f, 1f)) }),
            "P");

        var target = Material("Target", "Shaders/AlphaTest",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/old.tex") },
            parameters: new[] { ("TintColor", Vector4.One) });

        var planned = MaterialPresetApplier.Plan(preset, target, MaterialPresetParts.All, Cache());

        // Nothing moved.
        Assert.Equal("Shaders/AlphaTest", target.RenderShader);
        Assert.Equal("assets/old.tex", target.Slots.Single().Path);
        Assert.False(target.IsDirty);

        var applied = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.All, Cache());

        Assert.Equal(planned.Changes.Select(c => c.ToString()), applied.Changes.Select(c => c.ToString()));
        Assert.Equal("Shaders/Flat", target.RenderShader);
        Assert.Equal("assets/new.tex", target.Slots.Single().Path);
        Assert.Equal(1, target.Slots.Single().AddressW);
        Assert.True(target.IsDirty);

        // Re-applying the same preset is a no-op, which is what makes the diff trustworthy.
        Assert.Empty(MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.All, Cache()).Changes);
    }

    [Fact]
    public void OnlyTheTickedPartsAreWritten()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                samplers: new[] { new Sampler("DiffuseTexture", "assets/new.tex") },
                macros: new[] { ("NO_BAKED_LIGHTING", "1") }),
            "P");

        var target = Material("Target", "Shaders/AlphaTest",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/old.tex") });

        MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Samplers, Cache());

        Assert.Equal("assets/new.tex", target.Slots.Single().Path);
        Assert.Equal("Shaders/AlphaTest", target.RenderShader);      // shader was not ticked
        Assert.Empty(target.Macros);                                  // nor were macros
    }

    // ---- the M486 guard --------------------------------------------------------------------------

    [Fact]
    public void AShaderSwapIsRefusedWhenTheMaterialKeepsAMacroTheNewShaderNeverCooked()
    {
        // This is M486 in a new costume: the macro is fine where it is, and fatal on the shader the preset
        // is about to move the material to. Nothing in the material alone says so.
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/AlphaTest",
                samplers: new[] { new Sampler("DiffuseTexture", "assets/new.tex") }),
            "P");
        var target = Material("Target", "Shaders/Flat",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/old.tex") },
            macros: new[] { ("NO_BAKED_LIGHTING", "1") });

        var row = MaterialPresetApplier.Apply(preset, target,
            MaterialPresetParts.Shader | MaterialPresetParts.Samplers,
            Cache(uncooked: ("Shaders/AlphaTest", "NO_BAKED_LIGHTING")));

        Assert.Equal("Shaders/Flat", target.RenderShader);            // refused, not written
        Assert.DoesNotContain(row.Changes, c => c.Field == "shader");
        var blocker = Assert.Single(row.Blockers);
        Assert.Contains("NO_BAKED_LIGHTING", blocker);
        Assert.Contains("cooked", blocker);

        // The rest of the preset still applies — one bad field does not cancel the edit.
        Assert.Contains(row.Changes, c => c.Field.StartsWith("sampler:"));
    }

    [Fact]
    public void ThatSameSwapIsAllowedWhenThePresetAlsoReplacesTheMacros()
    {
        // Same two materials as above. With Macros ticked the offending macro is not retained, so there is
        // nothing to crash on — refusing here would be a false alarm.
        var preset = MaterialPreset.Capture(Material("Source", "Shaders/AlphaTest"), "P");
        var target = Material("Target", "Shaders/Flat",
            macros: new[] { ("NO_BAKED_LIGHTING", "1") });

        var row = MaterialPresetApplier.Apply(preset, target,
            MaterialPresetParts.Shader | MaterialPresetParts.Macros,
            Cache(uncooked: ("Shaders/AlphaTest", "NO_BAKED_LIGHTING")));

        Assert.Equal("Shaders/AlphaTest", target.RenderShader);
        Assert.Empty(target.Macros);
        Assert.Contains(row.Changes, c => c.Field == "macro:NO_BAKED_LIGHTING" && c.To == "(removed)");
        Assert.Empty(row.Blockers);
    }

    [Fact]
    public void AnUncookedPresetMacroIsRefusedAndAnInertOneIsSkipped()
    {
        // The two failures are opposites and must not read the same: uncooked makes the client fail to
        // compile the shader (fatal), undeclared makes it ignore the macro (merely a lie).
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                macros: new[] { ("NO_BAKED_LIGHTING", "1"), ("DISABLE_DEPTH_FOG", "1") }),
            "P");

        var fatal = Material("Fatal", "Shaders/Flat");
        var rowA = MaterialPresetApplier.Apply(preset, fatal, MaterialPresetParts.Macros,
            Cache(uncooked: ("Shaders/Flat", "NO_BAKED_LIGHTING")));

        Assert.False(fatal.Macros.ContainsKey("NO_BAKED_LIGHTING"));
        Assert.True(fatal.Macros.ContainsKey("DISABLE_DEPTH_FOG"));    // the other one still lands
        Assert.Contains(rowA.Blockers, b => b.Contains("NO_BAKED_LIGHTING") && b.Contains("M486"));

        var inert = Material("Inert", "Shaders/Flat");
        var rowB = MaterialPresetApplier.Apply(preset, inert, MaterialPresetParts.Macros,
            Cache(undeclared: ("Shaders/Flat", "NO_BAKED_LIGHTING")));

        Assert.False(inert.Macros.ContainsKey("NO_BAKED_LIGHTING"));
        Assert.Empty(rowB.Blockers);
        Assert.Contains(rowB.Notes, n => n.Contains("NO_BAKED_LIGHTING") && n.Contains("ignore"));
    }

    [Fact]
    public void WithNoShaderCacheMacrosAreWrittenUncheckedAndSaidSo()
    {
        // The audit's rule: a check with no way to answer is skipped, not guessed. Skipping it silently
        // would be the guess.
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat", macros: new[] { ("NO_BAKED_LIGHTING", "1") }), "P");
        var target = Material("Target", "Shaders/Flat");

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Macros);

        Assert.Equal("1", target.Macros["NO_BAKED_LIGHTING"]);
        Assert.Contains(row.Notes, n => n.Contains("not checked"));
    }

    [Fact]
    public void TheSwitchSetIsPassedToTheShaderCacheWithTheMacro()
    {
        // The permutation key combines every axis at once, so asking about a macro without the switches
        // that will be on the material answers a different question — the mistake M486 and M502 both made.
        var seen = new List<string>();
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                switches: new[] { ("USE_MASK", true) },
                macros: new[] { ("NO_BAKED_LIGHTING", "1") }),
            "P");
        var target = Material("Target", "Shaders/Flat");

        MaterialPresetApplier.Apply(preset, target,
            MaterialPresetParts.Switches | MaterialPresetParts.Macros,
            new MaterialPresetContext(MacroSupport: (_, switches, macro, _) =>
            {
                seen.Add($"{macro}:{string.Join(",", switches.Select(s => $"{s.Key}={s.Value}"))}");
                return MacroSupport.Cooked;
            }));

        Assert.Contains("NO_BAKED_LIGHTING:USE_MASK=True", seen);
    }

    // ---- additive vs replacing -------------------------------------------------------------------

    [Fact]
    public void SamplersAreAdditiveSoAPresetNeverStripsAMaskOrNormalMap()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                samplers: new[] { new Sampler("DiffuseTexture", "assets/new.tex") }),
            "P");

        var target = Material("Target", "Shaders/Flat", samplers: new[]
        {
            new Sampler("DiffuseTexture", "assets/old.tex"),
            new Sampler("NormalTexture", "assets/n.tex"),
        });

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Samplers, Cache());

        Assert.Equal(2, target.Slots.Count);
        Assert.Equal("assets/n.tex", target.Slots.Single(s => s.SamplerName == "NormalTexture").Path);
        Assert.Contains(row.Notes, n => n.Contains("NormalTexture") && n.Contains("kept"));
    }

    [Fact]
    public void ASamplerThePresetHasAndTheTargetLacksIsAdded()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat", samplers: new[]
            {
                new Sampler("DiffuseTexture", "assets/d.tex"),
                new Sampler("Mask_Texture", "assets/m.tex", AddressW: 1),
            }),
            "P");

        var target = Material("Target", "Shaders/Flat",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/d.tex") });

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Samplers, Cache());

        var added = Assert.Single(target.Slots, s => s.SamplerName == "Mask_Texture");
        Assert.Equal("assets/m.tex", added.Path);
        Assert.Equal(1, added.AddressW);
        Assert.Null(added.AddressU);       // the preset authored none, so neither does the target
        Assert.Contains(row.Changes, c => c.Field == "sampler:Mask_Texture" && c.From == "(absent)");
    }

    [Fact]
    public void MacrosReplaceBecauseClearingABadOneIsThePoint()
    {
        // Removing a macro is safe in a way removing a texture is not: absent means "the shader's cooked
        // default", which is by definition a permutation the game ships. It is also the only way a preset
        // can FIX the material that M486 broke.
        var preset = MaterialPreset.Capture(Material("Source", "Shaders/Flat"), "P");
        var target = Material("Target", "Shaders/Flat", macros: new[] { ("NO_BAKED_LIGHTING", "1") });

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Macros, Cache());

        Assert.Empty(target.Macros);
        Assert.Contains(row.Changes, c => c.Field == "macro:NO_BAKED_LIGHTING" && c.To == "(removed)");
    }

    // ---- switches, parameters, render state ------------------------------------------------------

    [Fact]
    public void AnAbsentSwitchIsAddedWithThePresetsValueRatherThanAssumedOff()
    {
        // Riot treats a switch with no 'on' field as ENABLED, and a switch that is absent entirely as the
        // shader's own default — so "off" cannot be expressed by leaving it out.
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat", switches: new[] { ("USE_MASK", false) }), "P");
        var target = Material("Target", "Shaders/Flat");

        MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Switches, Cache());

        Assert.False(target.Switches["USE_MASK"]);
    }

    [Fact]
    public void RenderStateWritesBlendCullAndBothFactorsIncludingRemoval()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat", blendEnable: true, cullEnable: false, srcBlend: 6, dstBlend: 7),
            "P");
        var target = Material("Target", "Shaders/Flat", blendEnable: false, cullEnable: true,
            srcBlend: 1, dstBlend: 1);

        MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.RenderState, Cache());

        Assert.True(target.BlendEnable);
        Assert.False(target.CullEnable);
        Assert.Equal(6, target.SrcBlendFactor);
        Assert.Equal(7, target.DstBlendFactor);

        // And the other direction: a preset with no factors removes them rather than writing a zero, which
        // is a different value.
        var plain = MaterialPreset.Capture(Material("Plain", "Shaders/Flat"), "Plain");
        MaterialPresetApplier.Apply(plain, target, MaterialPresetParts.RenderState, Cache());
        Assert.Equal(-1, target.SrcBlendFactor);
        Assert.Null(target.CullEnable);
    }

    [Fact]
    public void AParameterThePresetHasAndTheTargetLacksIsAdded()
    {
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                parameters: new[] { ("TintColor", new Vector4(0.5019608f, 0.5019608f, 0.5019608f, 1f)) }),
            "P");
        var target = Material("Target", "Shaders/Flat",
            parameters: new[] { ("Other", Vector4.Zero) });

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Parameters, Cache());

        var added = Assert.Single(target.Parameters, p => p.Name == "TintColor");
        Assert.True(added.TryGetVector4(out var v));
        Assert.Equal(0.5019608f, v.X, 5);
        Assert.Contains(row.Changes, c => c.Field == "param:TintColor" && c.From == "(absent)");
    }

    // ---- refusals --------------------------------------------------------------------------------

    [Fact]
    public void ABindingWithNoBinObjectIsRefusedOnceRatherThanSixTimes()
    {
        // The inline and skin-default bindings have nowhere to write anything. Six identical blockers would
        // bury the one sentence that matters.
        var binding = new MaterialBinding("Inline", "Shaders/Flat", Array.Empty<string>(), false,
            new List<TextureSlot>(), Array.Empty<MaterialParameter>());
        var preset = MaterialPreset.Capture(Material("Source", "Shaders/Flat"), "P");

        var row = MaterialPresetApplier.Plan(preset, binding, MaterialPresetParts.All, Cache());

        Assert.Empty(row.Changes);
        Assert.Single(row.Blockers);
        Assert.Contains("StaticMaterialDef", row.Blockers[0]);
    }

    [Fact]
    public void AnUnresolvedTextureIsNotedNotBlocked()
    {
        // The user may well be about to add the file. Refusing would make the tool wrong more often than
        // the warning is useful.
        var preset = MaterialPreset.Capture(
            Material("Source", "Shaders/Flat",
                samplers: new[] { new Sampler("DiffuseTexture", "assets/gone.tex") }),
            "P");
        var target = Material("Target", "Shaders/Flat",
            samplers: new[] { new Sampler("DiffuseTexture", "assets/here.tex") });

        var row = MaterialPresetApplier.Apply(preset, target, MaterialPresetParts.Samplers,
            Cache(textureExists: p => !p.Contains("gone")));

        Assert.Equal("assets/gone.tex", target.Slots.Single().Path);
        Assert.Empty(row.Blockers);
        Assert.Contains(row.Notes, n => n.Contains("assets/gone.tex") && n.Contains("does not resolve"));
    }

    [Fact]
    public void BulkApplyReportsPerMaterialSoOneRefusalIsVisibleAmongTwenty()
    {
        var preset = MaterialPreset.Capture(Material("Source", "Shaders/AlphaTest"), "P");
        var targets = new[]
        {
            Material("Ok1", "Shaders/Flat"),
            Material("Bad", "Shaders/Flat", macros: new[] { ("NO_BAKED_LIGHTING", "1") }),
            Material("Ok2", "Shaders/Flat"),
        };

        var rows = MaterialPresetApplier.Apply(preset, targets, MaterialPresetParts.Shader,
            Cache(uncooked: ("Shaders/AlphaTest", "NO_BAKED_LIGHTING")));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Shaders/AlphaTest", targets[0].RenderShader);
        Assert.Equal("Shaders/Flat", targets[1].RenderShader);
        Assert.Equal("Shaders/AlphaTest", targets[2].RenderShader);
        Assert.Single(rows, r => r.HasBlockers);
        Assert.Equal("Bad", rows.Single(r => r.HasBlockers).MaterialName);
    }

    // ---- library ---------------------------------------------------------------------------------

    [Fact]
    public void TheLibraryRoundTripsThroughJsonAndReplacesByName()
    {
        string path = Path.Combine(Path.GetTempPath(), $"reyengine-presets-{Guid.NewGuid():N}.json");
        try
        {
            var lib = new MaterialPresetLibrary();
            lib.Put(MaterialPreset.Capture(
                Material("Source", "Shaders/Flat",
                    samplers: new[] { new Sampler("DiffuseTexture", "assets/a.tex", AddressW: 1) },
                    macros: new[] { ("NO_BAKED_LIGHTING", "1") }),
                "Jade terrain"));
            // Saving the same name twice updates rather than leaving two rows that differ invisibly.
            lib.Put(MaterialPreset.Capture(Material("Source2", "Shaders/AlphaTest"), "Jade terrain"));
            Assert.True(lib.Save(path));

            var loaded = MaterialPresetLibrary.Load(path);
            var preset = Assert.Single(loaded.Presets);
            Assert.Equal("Jade terrain", preset.Name);
            Assert.Equal("Shaders/AlphaTest", preset.Shader);
            Assert.Equal("Source2", preset.SourceMaterial);

            Assert.True(loaded.Remove("jade terrain"));   // names are matched case-insensitively
            Assert.Empty(loaded.Presets);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnUnreadableLibraryStartsEmptyRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"reyengine-presets-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Empty(MaterialPresetLibrary.Load(path).Presets);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
