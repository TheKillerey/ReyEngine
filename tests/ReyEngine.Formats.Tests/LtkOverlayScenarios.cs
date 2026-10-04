using static ReyEngine.Formats.Tests.OverlayKit;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>The scenarios of the overlay: each one is also run through <c>ltk_overlay</c> itself, and <c>Fixtures/LtkGameData/overlay-scenarios.json</c> holds what it produced.</summary>
internal static class Scenarios
{
    public static IEnumerable<Scenario> All()
    {
        string a = "data/t/a.bin", b = "data/t/b.bin";

        // ---------------------------------------------------------------- the same chunk in the base and in a layer
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })));
            var mod = new SyntheticMod()
                .Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")))
                .Layer("x", 1, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}")));
            mod.WadFiles.Add(("base", "A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "m_base" }))));
            mod.WadFiles.Add(("x", "A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "m_x" }))));
            yield return new Scenario("base-and-layer-copy", game, mod);
            yield return new Scenario("base-and-layer-copy-x-inactive", game, mod, new string[0]);
            yield return new Scenario("base-and-layer-copy-x-active", game, mod, new[] { "x" });

            var withRaw = new SyntheticMod()
                .Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")))
                .Layer("x", 1, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}")));
            withRaw.WadFiles.Add(("base", "A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "m_base" }))));
            withRaw.WadFiles.Add(("x", "A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "m_x" }))));
            withRaw.Raw.Add((a, Bin(Obj("Test/Obj/A", new[] { "m_raw" }))));
            yield return new Scenario("raw-wins-over-layers", game, withRaw);
        }

        // ---------------------------------------------------------------- an inactive layer, and two modules on one target
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" }))).Add("A.wad.client", b, Bin(Obj("Test/Obj/B", new[] { "g2" })));
            var mod = new SyntheticMod()
                .Layer("base", 0, Doc(
                    Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"1\"]}}"),
                    Target(a, "{\"Test/Obj/A\":{\"nope\":5}}"),
                    Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"3\"]},\"links\":[\"data/other.bin\"]}")))
                .Layer("y", 2, Doc(Target(b, "{\"Test/Obj/B\":{\"+tags\":[\"y\"]}}")));
            yield return new Scenario("two-modules-one-target", game, mod);
            yield return new Scenario("inactive-layer", game, mod, new string[0]);
        }

        // ---------------------------------------------------------------- entries: fan-out over two chunks, one entry in none
        {
            var game = new SyntheticGame()
                .Add("B.wad.client", "data/t/b1.bin", Bin(Obj("Test/Obj/E", new[] { "e_in_b1" }), Obj("Test/Obj/Other", new[] { "o" })))
                .Add("C.wad.client", "data/t/c1.bin", Bin(Obj("Test/Obj/E", new[] { "e_in_c1" })))
                .Add("C.wad.client", "data/t/c2.bin", Bin(Obj("Test/Obj/G", new[] { "g_in_c2" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(Entries(
                "\"Test/Obj/E\":{\"+tags\":[\"added\"]},\"Test/Obj/F\":{\"+tags\":[\"nowhere\"]},\"Test/Obj/G\":{\"+tags\":[\"g\"],\"links\":[\"data/dep.bin\"]}")));
            yield return new Scenario("entries-fanout", game, mod);
        }

        // ---------------------------------------------------------------- a reference to an entry declared in several chunks: the first holder in BYTE order of the archive name
        {
            // 'Z' (0x5A) sorts before 'a' (0x61): archive 0 is Z.wad.client, though a case-insensitive order would put a.wad.client first
            var game = new SyntheticGame()
                .Add("a.wad.client", "data/t/xa.bin", Bin(Obj("Test/Obj/X", new[] { "from_a" })))
                .Add("Z.wad.client", "data/t/xz.bin", Bin(Obj("Test/Obj/X", new[] { "from_z" })))
                .Add("D.wad.client", "data/t/d.bin", Bin(Obj("Test/Obj/D", new[] { "d" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target("data/t/d.bin", "{\"Test/Obj/D\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));
            yield return new Scenario("ref-first-holder-byte-order", game, mod);

            // within one archive the lowest chunk hash is first
            var inOne = new SyntheticGame()
                .AddHash("R.wad.client", 0x2000, Bin(Obj("Test/Obj/X", new[] { "high_hash" })))
                .AddHash("R.wad.client", 0x1000, Bin(Obj("Test/Obj/X", new[] { "low_hash" })))
                .Add("D.wad.client", "data/t/d.bin", Bin(Obj("Test/Obj/D", new[] { "d" })));
            yield return new Scenario("ref-first-chunk-hash", inOne, mod);
        }

        // ---------------------------------------------------------------- PTCH targets, and an entry declared in a PTCH
        {
            var p1 = Obj("Test/Obj/P1", new[] { "p1" });
            var game = new SyntheticGame()
                .Add("P.wad.client", "data/t/p.bin", Ptch(new[] { p1 }))
                .Add("Q.wad.client", "data/t/q.bin", Bin(Obj("Test/Obj/Q", new[] { "q" }, name: "qname")));
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target("data/t/p.bin", "{\"Test/Obj/P1\":{\"+tags\":[\"owned\"]},\"Test/Obj/Q\":{\"+tags\":[\"rec\"]}}"),
                Entries("\"Test/Obj/P1\":{\"+tags\":[\"viaentries\"]}")));
            yield return new Scenario("ptch-target", game, mod);

            var onlyPtch = new SyntheticMod().Layer("base", 0, Doc(Target("data/t/p.bin", "{\"Test/Obj/P1\":{\"+tags\":[\"owned\"]}}")));
            yield return new Scenario("ptch-target-no-index-needed", game, onlyPtch);
        }

        // ---------------------------------------------------------------- a layer that is refused whole
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })));
            var mod = new SyntheticMod()
                .Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"ok\"]}}")))
                .Layer("bad", 1, Doc(Target(a, "{\"unsupported\":1}")))
                .Layer("worse", 2, "{\"version\":2,\"modules\":[]}");
            yield return new Scenario("rejected-layer", game, mod);
        }

        // ---------------------------------------------------------------- objects created: one that another chunk declares, one that nobody does
        {
            var game = new SyntheticGame()
                .Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })))
                .Add("S.wad.client", "data/t/s.bin", Bin(Obj("Test/Obj/Shadowed", new[] { "s" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target(a,
                "{\"objects\":{\"Test/Obj/Shadowed\":{\"clone\":\"Test/Obj/A\",\"set\":{\"+tags\":[\"c\"]}},\"Test/Obj/Fresh\":{\"clone\":\"Test/Obj/A\"}}}")));
            yield return new Scenario("object-shadows-game", game, mod);
        }

        // ---------------------------------------------------------------- targets the game cannot serve
        {
            string toc = "data/final/champions/aatrox.wad.subchunktoc";
            var game = new SyntheticGame()
                .Add("Champions/Aatrox.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })))
                .Add("Champions/Aatrox.wad.client", toc, new byte[] { 1, 2, 3, 4 });
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target("data/t/missing.bin", "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}"),
                Target(toc, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}"),
                Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}")));
            yield return new Scenario("absent-and-blocked-targets", game, mod);
        }

        // ---------------------------------------------------------------- override files
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" }), Obj("Test/Obj/Gone", new[] { "gone" })));
            var patchObject = Obj("Test/Obj/FromPatch", new[] { "pp" });
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target(a, "{\"overrides\":[\"fix.ptch\",\"nofile.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"after\"]}}")));
            mod.LayerFiles.Add(("base", "fix.ptch", Ptch(new[] { patchObject }, H("Test/Obj/Gone"))));
            yield return new Scenario("override-files", game, mod);
        }

        // ---------------------------------------------------------------- the order of layers: natural, and upper case before lower
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g" })));
            string Add(string tag) => Doc(Target(a, $"{{\"Test/Obj/A\":{{\"+tags\":[\"{tag}\"]}}}}"));
            var mod = new SyntheticMod()
                .Layer("layer10", 0, Add("layer10"))
                .Layer("layer9", 0, Add("layer9"))
                .Layer("Layer2", 0, Add("Layer2"))
                .Layer("late", 5, Add("late"))
                .Layer("layer09", 0, Add("layer09"))
                .Layer("base", 3, Add("base"));
            yield return new Scenario("layer-order", game, mod);
        }

        // ---------------------------------------------------------------- a version 2 base that only a link edit touches (header rewrite), and a no-effect module
        {
            var game = new SyntheticGame().Add("A.wad.client", a, V2(Bin(new[] { "data/one.bin", "DATA/ONE.bin" }, Obj("Test/Obj/A", new[] { "g1" }))));
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target(a, "{\"links\":[\"data/two.bin\"],\"-links\":[\"data/absent.bin\"]}"),
                Target(a, "{\"Test/Obj/A\":{\"nope\":1}}")));
            yield return new Scenario("v2-header-rewrite", game, mod);
        }

        // ---------------------------------------------------------------- created objects, clones and references together
        {
            var game = new SyntheticGame()
                .Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })))
                .Add("X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/Src", new[] { "from_src" }, name: "srcname", count: 7)));
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target(a, "{\"objects\":{\"Test/Obj/New\":{\"clone\":\"Test/Obj/A\",\"set\":{\"+tags\":{\"ref\":\"Test/Obj/Src:tags\"}}}},\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/Src:tags\"},\"count\":{\"ref\":\"Test/Obj/Src:count\"},\"name\":{\"ref\":\"Test/Obj/Src:name\"}}}"),
                Target(a, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/Nope:tags\"},\"count\":{\"ref\":\"Test/Obj/Src:nofield\"}}}")));
            yield return new Scenario("refs-clone-missing-unresolved", game, mod);
        }

        // ---------------------------------------------------------------- a reference whose first holder is a PTCH: unreadable
        {
            var game = new SyntheticGame()
                .Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })))
                .Add("P.wad.client", "data/t/p.bin", Ptch(new[] { Obj("Test/Obj/P1", new[] { "p1" }) }));
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/P1:tags\"}}}")));
            yield return new Scenario("ref-first-holder-is-ptch", game, mod);
        }

        // ---------------------------------------------------------------- one object twice in one chunk: the last wins; one chunk in two archives: the first holder reads it
        {
            byte[] twice = TwoRows(Obj("Test/Obj/X", new[] { "first" }), Obj("Test/Obj/X", new[] { "last" }));
            var shared = Bin(Obj("Test/Obj/A", new[] { "shared" }));
            var game = new SyntheticGame()
                .Add("A.wad.client", a, shared)
                .Add("B.wad.client", a, shared)
                .Add("W.wad.client", "data/t/w.bin", twice)
                .Add("D.wad.client", "data/t/d.bin", Bin(Obj("Test/Obj/D", new[] { "d" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target("data/t/d.bin", "{\"Test/Obj/D\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}"),
                Target("data/t/w.bin", "{\"Test/Obj/X\":{\"+tags\":[\"w\"]}}"),
                Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]},\"Test/Obj/X\":{\"+tags\":[\"ex\"]}")));
            yield return new Scenario("duplicate-object-and-shared-chunk", game, mod);
        }

        // ---------------------------------------------------------------- an archive that does not mount keeps its id and indexes nothing
        {
            var game = new SyntheticGame()
                .Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })))
                .Add("Z.wad.client", "data/t/z.bin", Bin(Obj("Test/Obj/Z", new[] { "z" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(
                Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"),
                Entries("\"Test/Obj/Z\":{\"+tags\":[\"ez\"]}")));
            game.AddBroken("M.wad.client", System.Text.Encoding.ASCII.GetBytes("this is not an archive"));
            yield return new Scenario("corrupt-archive", game, mod);
        }

        // ---------------------------------------------------------------- the mod's copy is byte-identical to the game's
        {
            var bytes = Bin(Obj("Test/Obj/A", new[] { "same" }));
            var game = new SyntheticGame().Add("A.wad.client", a, bytes);
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
            mod.WadFiles.Add(("base", "A.wad.client", a, bytes));
            yield return new Scenario("mod-copy-identical-to-game", game, mod);
        }

        // ---------------------------------------------------------------- a mod whose bin is not a bin
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })));
            var mod = new SyntheticMod().Layer("base", 0, Doc(Target(a, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
            mod.WadFiles.Add(("base", "A.wad.client", a, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            yield return new Scenario("mod-copy-not-a-bin", game, mod);
        }

        // ---------------------------------------------------------------- the same entry spelled two ways in two modules of one chunk, and a target module hitting an entries-lowered chunk
        {
            var game = new SyntheticGame().Add("A.wad.client", a, Bin(Obj("Test/Obj/A", new[] { "g1" })));
            string hash = "0x" + H("Test/Obj/A").ToString("x8");
            var mod = new SyntheticMod()
                .Layer("base", 0, Doc(
                    Entries("\"Test/Obj/A\":{\"+tags\":[\"e1\"]}"),
                    Target(a, $"{{\"{hash}\":{{\"+tags\":[\"t1\"]}}}}"),
                    Entries($"\"{hash}\":{{\"+tags\":[\"e2\"]}}")))
                .Layer("z", 1, Doc(Entries("\"Test/Obj/A\":{\"-tags\":[\"e1\"]}")));
            yield return new Scenario("entries-and-targets-interleaved", game, mod);
        }
    }
}
