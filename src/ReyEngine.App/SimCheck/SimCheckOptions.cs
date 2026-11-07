using System.Globalization;

namespace ReyEngine.App.SimCheck;

/// <summary>Where the Character window is put while a check runs. Chosen to leave the user's screen alone.</summary>
internal enum SimCheckPlacement { Corner, Offscreen, Minimized }

/// <summary>
/// M871: <c>ReyEngine.App.exe --simcheck &lt;Champion[,Champion...]|all|from:&lt;Champion&gt;|first:&lt;N&gt;&gt; [--out &lt;dir&gt;] [--speed 1]
/// [--force] [--game &lt;Game folder&gt;] [--placement minimized|offscreen|corner]</c>.
/// </summary>
internal sealed record SimCheckOptions(string Spec, string? OutDirectory, double Speed, bool Force, string? GameDirectory, SimCheckPlacement Placement)
{
    public const string Usage = "usage: ReyEngine.App.exe --simcheck <Champion[,Champion...]|all|from:<Champion>|first:<N>> [--out <dir>] [--speed 0.25|0.5|1|1.5|2] [--force] [--game <Game folder>] [--placement minimized|offscreen|corner]";

    public static bool Handles(string[] args) =>
        args.Length > 0 && args[0].Equals("--simcheck", StringComparison.OrdinalIgnoreCase);

    public static SimCheckOptions? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) { error = Usage; return null; }
        string? outDir = null, game = null;
        double speed = 1.0;
        bool force = false;
        var placement = SimCheckPlacement.Minimized;
        for (int i = 2; i < args.Length; i++)
        {
            string a = args[i];
            bool HasValue() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (a.Equals("--force", StringComparison.OrdinalIgnoreCase)) force = true;
            else if (a.Equals("--out", StringComparison.OrdinalIgnoreCase) && HasValue()) outDir = args[++i];
            else if (a.Equals("--game", StringComparison.OrdinalIgnoreCase) && HasValue()) game = args[++i];
            else if (a.Equals("--speed", StringComparison.OrdinalIgnoreCase) && HasValue())
            {
                if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
                    || !ViewModels.MeshPreviewViewModel.SimSpeeds.Any(s => Math.Abs(s - speed) < 1e-6))
                { error = "--speed must be one of " + string.Join(", ", ViewModels.MeshPreviewViewModel.SimSpeeds.Select(s => s.ToString(CultureInfo.InvariantCulture))); return null; }
            }
            else if (a.Equals("--placement", StringComparison.OrdinalIgnoreCase) && HasValue())
            {
                if (!Enum.TryParse(args[++i], ignoreCase: true, out placement)) { error = "--placement must be corner, offscreen or minimized"; return null; }
            }
            else { error = $"unknown or incomplete option '{a}'. " + Usage; return null; }
        }
        return new SimCheckOptions(args[1], outDir, speed, force, game, placement);
    }
}

/// <summary>M871: the run in progress. Set by <c>Program.Main</c> before Avalonia starts; read by <c>App</c> and the windows that must not take focus.</summary>
internal static class SimCheckSession
{
    public static SimCheckOptions? Options { get; set; }
    public static bool Active => Options is not null;
    public static int ExitCode { get; set; }
}
