namespace ReyEngine.Core.Hashing;

/// <summary>Resolves a 64-bit WAD path hash back to a readable path.</summary>
public interface IHashResolver
{
    bool TryGetPath(ulong hash, out string path);
    string ResolvePath(ulong hash);
}
