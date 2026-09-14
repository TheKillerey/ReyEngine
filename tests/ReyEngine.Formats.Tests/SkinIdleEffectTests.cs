using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M726: a skin's <c>idleParticlesEffects</c> read as a LIST - the effects the game plays for as long as the
/// character exists, each riding its own bone.
/// </summary>
public sealed class SkinIdleEffectTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] SkinWith(params (uint Key, string Bone, string Target)[] idles)
    {
        var records = idles.Select(i =>
        {
            var props = new List<BinTreeProperty>
            {
                new BinTreeHash(H("effectKey"), i.Key),
                new BinTreeString(H("boneName"), i.Bone),
            };
            if (i.Target.Length > 0) props.Add(new BinTreeString(H("targetBoneName"), i.Target));
            return (BinTreeProperty)new BinTreeEmbedded(0, H("SkinCharacterDataProperties_CharacterIdleEffect"), props);
        });

        var skin = new BinTreeObject(H("Characters/X/Skins/Skin0"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("idleParticlesEffects"), BinPropertyType.Embedded, records),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { skin }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void TwoRecordsSharingOneEffectKeyBothSurvive()
    {
        // Lux's real shape, measured: both her wand glows are effectKey Lux_I_weapon and differ only by
        // bone (BUFFBONE_CSTM_WEAPON_2 and _4). VfxSystemRoles.FromSkin keys a dictionary by effect key and
        // keeps the first, so it returns ONE of them - which is why idle playback needs its own reader.
        uint key = 0x459b777b;
        byte[] bin = SkinWith((key, "BUFFBONE_CSTM_WEAPON_2", ""), (key, "BUFFBONE_CSTM_WEAPON_4", ""));

        var list = SkinIdleEffects.Read(bin);
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Equal(key, e.EffectKey));
        Assert.Equal(new[] { "BUFFBONE_CSTM_WEAPON_2", "BUFFBONE_CSTM_WEAPON_4" }, list.Select(e => e.BoneName));

        // and the reader this replaces really does collapse them, so the difference is not theoretical
        var roles = VfxSystemRoles.FromSkin(bin);
        Assert.Single(roles);
    }

    [Fact]
    public void ListOrderIsPreservedAndNothingIsDeduped()
    {
        byte[] bin = SkinWith((1u, "A", ""), (2u, "B", ""), (1u, "A", ""));
        var list = SkinIdleEffects.Read(bin);
        Assert.Equal(3, list.Count);
        Assert.Equal(new uint[] { 1, 2, 1 }, list.Select(e => e.EffectKey));
    }

    [Fact]
    public void ATargetBoneIsKeptWhenAuthoredAndEmptyWhenNot()
    {
        // real but uncommon: 192 of Thresh's 1,047 records and 12 of Yasuo's 95 name one
        byte[] bin = SkinWith((7u, "L_Hand", "R_Hand"), (8u, "Root", ""));
        var list = SkinIdleEffects.Read(bin);
        Assert.Equal("R_Hand", list[0].TargetBoneName);
        Assert.Equal("", list[1].TargetBoneName);
        // Position was not observed in 1,855 censused records, so it reads as null rather than zero
        Assert.All(list, e => Assert.Null(e.Position));
    }

    [Fact]
    public void AnUnusableRecordIsDroppedRatherThanMountedOnNothing()
    {
        // effectKey 0 can name no system; the caller would have nothing to play
        byte[] bin = SkinWith((0u, "Root", ""), (5u, "Head", ""));
        var list = SkinIdleEffects.Read(bin);
        Assert.Equal(5u, Assert.Single(list).EffectKey);
    }

    [Fact]
    public void ASkinWithNoIdleEffectsAndAnUnreadableBinBothYieldNothing()
    {
        Assert.Empty(SkinIdleEffects.Read(SkinWith()));
        Assert.Empty(SkinIdleEffects.Read(new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(SkinIdleEffects.Read(Array.Empty<byte>()));
    }

    [Fact]
    public void ReadingCanBeLimitedToOneSkinObject()
    {
        byte[] bin = SkinWith((9u, "Root", ""));
        Assert.Single(SkinIdleEffects.Read(bin, H("Characters/X/Skins/Skin0")));
        // a different skin object in the same bytes contributes nothing - which is the guard against
        // mounting another skin's effects on this skin's bones
        Assert.Empty(SkinIdleEffects.Read(bin, H("Characters/X/Skins/Skin7")));
    }
}
