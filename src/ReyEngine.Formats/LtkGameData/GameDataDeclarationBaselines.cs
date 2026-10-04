using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M823: <see cref="IDeclarationBaselines"/> over a project's GameData: where a project bin that the imported GameData also targets stands in the install order, found with the apply engine.
///
/// <para><b>The order.</b> An export writes each layer's document as the imported modules, then the edits kept on top of them, then the declarations the planner makes of the project's bins.
/// So when the module of a project bin runs, LTK has applied every module of the layers up to and including the bin's layer that came before it - the imported ones and the edits, but not
/// the planner's (which are of other bins). The pair <see cref="For"/> returns is the game's bin and the project's copy with EXACTLY those modules applied: the module declared between them
/// states what the copy changes beyond what the package and the edits already do, and the layers after the bin's own run on top, as they would over the copy itself.</para>
///
/// <para><b>The bytes.</b> The game's bin is the one the planner was given (the project's reference), not the installed game's, so the pair is made of the bytes the project was built against;
/// the engine is the overlay's own (<see cref="GameDataOverlay"/>), over a game that answers the chunk with those bytes.</para>
/// </summary>
public sealed class GameDataDeclarationBaselines : IDeclarationBaselines
{
    private readonly IReadOnlyList<GameDataLayerInput> _layers;
    private readonly IReadOnlySet<ulong> _targets;
    private readonly IGameDataGame _game;
    private readonly GameDataOverlayOptions? _options;

    /// <param name="layers">The project's layers with the edits composed in (<see cref="GameDataLayerInput.FromProject"/>), in any order.</param>
    /// <param name="targets">The chunks the declarations name (<see cref="GameDataPreview.NamedChunks"/>): a bin that is none of them is declared against the game's, as ever.</param>
    /// <param name="game">The installed game: its object index resolves the references and the entries of the modules.</param>
    /// <param name="options">The overlay's options (the class schema above all); null for the defaults.</param>
    public GameDataDeclarationBaselines(IReadOnlyList<GameDataLayerInput> layers, IReadOnlySet<ulong> targets, IGameDataGame game, GameDataOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(game);
        var sorted = layers.ToList();
        sorted.Sort(GameDataLayerOrder.Compare);
        _layers = sorted;
        _targets = targets;
        _game = game;
        _options = options;
    }

    /// <inheritdoc />
    public DeclarationBaseline? For(DeclarationFile file, ulong chunk, byte[] game, byte[] mod)
    {
        if (!_targets.Contains(chunk)) return null;

        // the layers up to and including the file's own, in apply order; a layer the project does not list is taken to come last
        int through = _layers.ToList().FindIndex(l => l.Name == file.Layer);
        var prefix = through < 0 ? _layers : _layers.Take(through + 1).ToList();

        return new DeclarationBaseline(
            Applied(prefix, new ChunkOverrideGame(_game, chunk, game), null, chunk, game),
            Applied(prefix, _game, new SingleModFile(chunk, mod), chunk, mod));
    }

    /// <summary>The bytes of the chunk after every module of <paramref name="layers"/> has run over <paramref name="start"/>; the bytes as they were when no module changed them.</summary>
    private byte[] Applied(IReadOnlyList<GameDataLayerInput> layers, IGameDataGame game, IGameDataModFiles? modFiles, ulong chunk, byte[] start)
    {
        var result = new GameDataOverlay(layers, game, modFiles, _options).Apply(chunk);
        return result is { Applied: true, Bytes: { } bytes } ? bytes : start;
    }

    /// <summary>The game, but for one chunk, which is answered with the bytes the planner read.</summary>
    private sealed class ChunkOverrideGame : IGameDataGame
    {
        private readonly IGameDataGame _inner;
        private readonly ulong _chunk;
        private readonly byte[] _bytes;

        public ChunkOverrideGame(IGameDataGame inner, ulong chunk, byte[] bytes) { _inner = inner; _chunk = chunk; _bytes = bytes; }

        public GameChunkTable GetTable(CancellationToken cancellationToken) => _inner.GetTable(cancellationToken);

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) => _inner.GetObjects(cancellationToken);

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) =>
            chunk == _chunk ? (byte[])_bytes.Clone() : _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);

        public string? IndexNotSaved => _inner.IndexNotSaved;
    }

    /// <summary>The project's copy of the one bin, as the mod's own file the modules run over.</summary>
    private sealed class SingleModFile : IGameDataModFiles
    {
        private readonly ulong _chunk;
        private readonly byte[] _bytes;

        public SingleModFile(ulong chunk, byte[] bytes) { _chunk = chunk; _bytes = bytes; }

        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) => chunk == _chunk ? (byte[])_bytes.Clone() : null;

        public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
    }
}
