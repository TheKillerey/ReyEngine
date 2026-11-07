using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Formats.Characters.Simulation;
using PixelRect = ReyEngine.Formats.Characters.Simulation.PixelRect;

namespace ReyEngine.App.SimCheck;

/// <summary>
/// M871: the contact sheet of a champion: one row per ability, the baseline and the sampled frames across, each labelled with its time,
/// its changed-pixel ratio, its effect ratio (the caster's own box left out) and the live particle count. The geometry is
/// <see cref="SimCheckSheetLayout"/>; this draws it with Avalonia (UI thread) and saves a PNG.
/// </summary>
internal static class SimCheckSheetWriter
{
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#14161C"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D8DCE6"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A90A0"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#5BD38A"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F0B24A"));
    private static readonly IBrush Blocked = new SolidColorBrush(Color.Parse("#F06A6A"));
    private static readonly IBrush Region = new SolidColorBrush(Color.Parse("#E8D44D"));

    public static void Write(string path, string champion, SimCheckChampionReport report,
        IReadOnlyList<(List<(byte[] Bgra, int W, int H)> Cells, PixelRect Region, string[] Captions)> rows)
    {
        int columns = Math.Max(1, rows.Select(r => r.Cells.Count).DefaultIfEmpty(1).Max());
        var first = rows.SelectMany(r => r.Cells).FirstOrDefault();
        double aspect = first.Bgra is null ? 16.0 / 9.0 : (double)first.W / first.H;
        var layout = SimCheckSheetLayout.Compute(report.Abilities.Count, columns, aspect);

        var canvas = new Canvas { Width = layout.Width, Height = layout.Height, Background = Background };
        Place(canvas, Label($"{champion}   {report.Status}   game {report.GameVersion}   checker {report.CheckVersion}   {report.Abilities.Count(a => !a.Wrapper)} abilities   "
                            + $"{report.WallSeconds:0} s   {report.Window}   speed {report.Speed}", 15, StatusBrush(report.Status), layout.Width - 16, FontWeight.SemiBold), 8, 7);

        for (int i = 0; i < report.Abilities.Count; i++)
        {
            var ability = report.Abilities[i];
            var rowRect = layout.RowRect(i);
            var line = new Rectangle { Width = layout.Width, Height = 1, Fill = new SolidColorBrush(Color.Parse("#2A2E3A")) };
            Place(canvas, line, 0, rowRect.Y);

            var label = layout.LabelRect(i);
            string name = ability.Wrapper ? $"{ability.Key}  {ability.Name}  (sub-casts)" : $"{ability.Key}  {ability.Name}";
            Place(canvas, Label(name, 12, Text, label.Width - 6, FontWeight.SemiBold), label.X, label.Y);
            Place(canvas, Label(ability.Grade, 12, GradeBrush(ability.Grade), label.Width - 6, FontWeight.Bold), label.X, label.Y + 18);
            string detail = ability.Reasons.Count > 0 ? string.Join("\n", ability.Reasons.Take(4)) : string.Join("\n", ability.Notes.Take(2));
            Place(canvas, Label(detail, 9, Dim, label.Width - 6, FontWeight.Normal, wrap: true), label.X, label.Y + 36);

            if (i >= rows.Count) continue;
            var (cells, region, captions) = rows[i];
            for (int c = 0; c < cells.Count && c < layout.Columns; c++)
            {
                var cell = layout.CellRect(i, c);
                var (bgra, w, h) = cells[c];
                var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bitmap.Lock())
                    for (int y = 0; y < h; y++)
                        System.Runtime.InteropServices.Marshal.Copy(bgra, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
                Place(canvas, new Image { Source = bitmap, Width = cell.Width, Height = cell.Height, Stretch = Stretch.Fill }, cell.X, cell.Y);

                // the region the cast is judged in, scaled onto the cell (the baseline's frame size is the sheet's)
                if (!region.IsEmpty && ability.Frames.Count > c)
                {
                    var f = ability.Frames[c];
                    double sx = (double)cell.Width / f.Width, sy = (double)cell.Height / f.Height;
                    Place(canvas, new Rectangle
                    {
                        Width = Math.Max(2, region.Width * sx), Height = Math.Max(2, region.Height * sy),
                        Stroke = Region, StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 },
                    }, cell.X + region.X * sx, cell.Y + region.Y * sy);
                }
                var cap = layout.CaptionRect(i, c);
                if (c < captions.Length) Place(canvas, Label(captions[c], 9, Dim, cap.Width, FontWeight.Normal, wrap: true), cap.X, cap.Y + 1);
            }
        }

        canvas.Measure(new Size(layout.Width, layout.Height));
        canvas.Arrange(new Rect(0, 0, layout.Width, layout.Height));
        using var target = new RenderTargetBitmap(new PixelSize(layout.Width, layout.Height), new Vector(96, 96));
        target.Render(canvas);
        using var file = File.Create(path);
        target.Save(file);
    }

    private static IBrush StatusBrush(string status) => status == "COMPLETE" ? Good : status == "BLOCKED" ? Blocked : Bad;
    private static IBrush GradeBrush(string grade) => grade == "COMPLETE" ? Good : grade == "BLOCKED" ? Blocked : Bad;

    private static TextBlock Label(string text, double size, IBrush brush, double width, FontWeight weight, bool wrap = false) =>
        new() { Text = text, FontSize = size, Foreground = brush, Width = width, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

    private static void Place(Canvas canvas, Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        canvas.Children.Add(control);
    }
}
