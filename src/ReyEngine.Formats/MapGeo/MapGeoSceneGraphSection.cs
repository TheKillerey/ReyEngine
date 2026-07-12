using System.Buffers.Binary;
using LeagueToolkit.Core.Environment;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Locates and validates the raw BucketedGeometry section without parsing the preceding variable-length
/// mesh records. The public LeagueToolkit objects provide an exact first-grid signature; the known
/// planar-reflector tail and a full raw-grid parse make the match unambiguous.
/// </summary>
internal sealed class MapGeoSceneGraphSection
{
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required IReadOnlyList<RawGridHeader> Grids { get; init; }

    internal sealed record RawGridHeader(
        uint ControllerHash,
        uint RegionHash,
        float MinX,
        float MinZ,
        float MaxX,
        float MaxZ,
        float MaxStickOutX,
        float MaxStickOutZ,
        float BucketSizeX,
        float BucketSizeZ,
        ushort BucketsPerSide,
        bool IsDisabled,
        byte Flags,
        uint VertexCount,
        uint IndexCount);

    public static bool TryLocate(
        byte[] data,
        EnvironmentAsset environment,
        int version,
        out MapGeoSceneGraphSection? section)
    {
        try
        {
            section = Locate(data, environment, version);
            return true;
        }
        catch
        {
            section = null;
            return false;
        }
    }

    public static MapGeoSceneGraphSection Locate(byte[] data, EnvironmentAsset environment, int version)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(environment);
        int tailOffset = GetPlanarReflectorTailOffset(data, environment, version);
        int expectedCount = environment.SceneGraphs.Count;

        if (expectedCount == 0)
        {
            if (version < 15)
                throw new InvalidDataException("Mapgeo versions before 15 require one implicit scene graph.");
            int offset = tailOffset - sizeof(uint);
            if (offset < 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) != 0)
                throw new InvalidDataException("Could not locate the empty mapgeo scene-graph section.");
            return new MapGeoSceneGraphSection { Offset = offset, Length = 4, Grids = Array.Empty<RawGridHeader>() };
        }

        byte[] firstHeaderSignature = BuildFirstHeaderSignature(environment.SceneGraphs[0]);
        int prefixSize = version switch
        {
            >= 18 => 12, // count + render-region hash + controller hash
            >= 15 => 8,  // count + controller hash
            _ => 0,
        };
        var matches = new List<MapGeoSceneGraphSection>();
        int searchOffset = 0;
        while (searchOffset <= tailOffset - firstHeaderSignature.Length)
        {
            int relativeOffset = data.AsSpan(searchOffset, tailOffset - searchOffset).IndexOf(firstHeaderSignature);
            if (relativeOffset < 0)
                break;
            int signatureOffset = searchOffset + relativeOffset;
            searchOffset = signatureOffset + 1;
            int sectionOffset = signatureOffset - prefixSize;
            if (sectionOffset < 0)
                continue;
            if (TryParseAt(data, sectionOffset, tailOffset, version, expectedCount, out var candidate)
                && MatchesLeagueToolkit(candidate, environment, version))
                matches.Add(candidate);
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException("Could not locate the mapgeo scene-graph section."),
            _ => throw new InvalidDataException("The mapgeo scene-graph section signature is ambiguous."),
        };
    }

    private static int GetPlanarReflectorTailOffset(byte[] data, EnvironmentAsset environment, int version)
    {
        if (version < 13)
            return data.Length;
        int tailLength = checked(4 + environment.PlanarReflectors.Count * 100);
        int offset = data.Length - tailLength;
        if (offset < 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) != environment.PlanarReflectors.Count)
            throw new InvalidDataException("The mapgeo planar-reflector tail is not byte-aligned as expected.");
        return offset;
    }

    private static byte[] BuildFirstHeaderSignature(LeagueToolkit.Core.SceneGraph.BucketedGeometry grid)
    {
        float[] values =
        [
            grid.MinX, grid.MinZ, grid.MaxX, grid.MaxZ,
            grid.MaxStickOutX, grid.MaxStickOutZ, grid.BucketSizeX, grid.BucketSizeZ,
        ];
        var result = new byte[values.Length * sizeof(float)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(i * 4, 4), values[i]);
        return result;
    }

    private static bool TryParseAt(
        byte[] data,
        int offset,
        int expectedEnd,
        int version,
        int expectedCount,
        out MapGeoSceneGraphSection section)
    {
        section = null!;
        try
        {
            var reader = new SpanReader(data, offset, expectedEnd);
            int count = version >= 15 ? checked((int)reader.ReadUInt32()) : 1;
            if (count != expectedCount)
                return false;

            var grids = new RawGridHeader[count];
            for (int i = 0; i < count; i++)
            {
                uint firstHash = version >= 15 ? reader.ReadUInt32() : 0;
                // Verified on a real v18 mapgeo: the original slot changes to render-region identity,
                // while the added v18 slot carries the visibility-controller/baron identity. In v15/17,
                // the only slot still carries the controller hash.
                uint regionHash = version >= 18 ? firstHash : 0;
                uint controllerHash = version >= 18 ? reader.ReadUInt32() : firstHash;
                float minX = reader.ReadSingle();
                float minZ = reader.ReadSingle();
                float maxX = reader.ReadSingle();
                float maxZ = reader.ReadSingle();
                float maxStickOutX = reader.ReadSingle();
                float maxStickOutZ = reader.ReadSingle();
                float bucketSizeX = reader.ReadSingle();
                float bucketSizeZ = reader.ReadSingle();
                ushort side = reader.ReadUInt16();
                bool disabled = reader.ReadByte() != 0;
                byte flags = reader.ReadByte();
                uint vertexCount = reader.ReadUInt32();
                uint indexCount = reader.ReadUInt32();
                grids[i] = new(
                    controllerHash, regionHash,
                    minX, minZ, maxX, maxZ,
                    maxStickOutX, maxStickOutZ, bucketSizeX, bucketSizeZ,
                    side, disabled, flags, vertexCount, indexCount);

                if (disabled)
                    continue;
                reader.Skip(checked((int)vertexCount * 12));
                reader.Skip(checked((int)indexCount * 2));
                reader.Skip(checked(side * side * 20));
                if ((flags & 1) != 0)
                    reader.Skip(checked((int)indexCount / 3));
            }

            if (reader.Offset != expectedEnd)
                return false;
            section = new MapGeoSceneGraphSection
            {
                Offset = offset,
                Length = expectedEnd - offset,
                Grids = grids,
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or EndOfStreamException or OverflowException)
        {
            return false;
        }
    }

    private static bool MatchesLeagueToolkit(MapGeoSceneGraphSection section, EnvironmentAsset environment, int version)
    {
        if (section.Grids.Count != environment.SceneGraphs.Count)
            return false;
        for (int i = 0; i < section.Grids.Count; i++)
        {
            RawGridHeader raw = section.Grids[i];
            var parsed = environment.SceneGraphs[i];
            uint toolkitFirstHash = version >= 18 ? raw.RegionHash : raw.ControllerHash;
            if (toolkitFirstHash != parsed.VisibilityControllerPathHash
                || !SameBits(raw.MinX, parsed.MinX) || !SameBits(raw.MinZ, parsed.MinZ)
                || !SameBits(raw.MaxX, parsed.MaxX) || !SameBits(raw.MaxZ, parsed.MaxZ)
                || !SameBits(raw.MaxStickOutX, parsed.MaxStickOutX)
                || !SameBits(raw.MaxStickOutZ, parsed.MaxStickOutZ)
                || !SameBits(raw.BucketSizeX, parsed.BucketSizeX)
                || !SameBits(raw.BucketSizeZ, parsed.BucketSizeZ)
                || raw.IsDisabled != parsed.IsDisabled
                || raw.BucketsPerSide != parsed.Buckets.Width)
                return false;
            if (!raw.IsDisabled
                && (raw.VertexCount != parsed.Vertices.Count || raw.IndexCount != parsed.Indices.Count))
                return false;
        }
        return true;
    }

    private static bool SameBits(float left, float right)
        => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _end;
        public int Offset { get; private set; }

        public SpanReader(byte[] data, int offset, int end)
        {
            _data = data;
            Offset = offset;
            _end = end;
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[Offset++];
        }

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Offset, 2));
            Offset += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Offset, 4));
            Offset += 4;
            return value;
        }

        public float ReadSingle()
        {
            Require(4);
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(Offset, 4));
            Offset += 4;
            return value;
        }

        public void Skip(int count)
        {
            Require(count);
            Offset += count;
        }

        private void Require(int count)
        {
            if (count < 0 || Offset < 0 || Offset > _end - count)
                throw new EndOfStreamException();
        }
    }
}
