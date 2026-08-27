using Bifrost.Core.Model;

namespace Bifrost.Drivers.Layout;

/// <summary>A block with the coordinates an absolute-positioning language needs.</summary>
public sealed record PositionedBlock(PrintBlock Block, int X, int Y, int Width, int Height);

/// <summary>
/// Assigns coordinates to blocks for the languages that place elements absolutely on a label
/// canvas — ZPL, CPCL and TSPL.
/// </summary>
/// <remarks>
/// <para>
/// Written once and shared by all three. Only ESC/POS bypasses it, because it streams sequentially
/// down a continuous roll. Without this, most of the driver code would have been triplicated
/// (DES-06 §4.4).
/// </para>
/// <para>
/// Heights are estimates from the printer's base font metrics, not measured glyph boxes. That is
/// sufficient: the purpose is to stack blocks without overlap, and thermal label layout has
/// generous vertical tolerance. Precise metrics would require the printer's font tables, which are
/// not exposed.
/// </para>
/// </remarks>
public sealed class AbsoluteLayoutEngine(int widthDots, int baseCharHeightDots = 24, int baseCharWidthDots = 12)
{
    private const int BlockGapDots = 8;

    public int WidthDots => widthDots;

    public IReadOnlyList<PositionedBlock> Layout(IReadOnlyList<PrintBlock> blocks)
    {
        var positioned = new List<PositionedBlock>(blocks.Count);
        var y = 0;

        foreach (var block in blocks)
        {
            var (w, h) = Measure(block);

            if (block is PrintBlock.Feed feed)
            {
                // Feed contributes vertical space and nothing else.
                y += feed.Dots;
                continue;
            }

            var x = block.Align switch
            {
                Alignment.Center => Math.Max(0, (widthDots - w) / 2),
                Alignment.Right => Math.Max(0, widthDots - w),
                _ => 0,
            };

            positioned.Add(new PositionedBlock(block, x, y, w, h));
            y += h + BlockGapDots;
        }

        return positioned;
    }

    /// <summary>Total height of a laid-out document, which CPCL needs in its header.</summary>
    public int TotalHeight(IReadOnlyList<PrintBlock> blocks)
    {
        var laid = Layout(blocks);
        var contentBottom = laid.Count == 0 ? 0 : laid.Max(p => p.Y + p.Height);

        // Feeds after the last visible block still consume media.
        var trailingFeed = blocks
            .SkipWhile(b => b is not PrintBlock.Feed)
            .OfType<PrintBlock.Feed>()
            .Sum(f => f.Dots);

        return contentBottom + Math.Max(trailingFeed, BlockGapDots);
    }

    private (int Width, int Height) Measure(PrintBlock block) => block switch
    {
        PrintBlock.Text t => (
            t.Value.Length * baseCharWidthDots * t.SizeMultiplier,
            baseCharHeightDots * t.SizeMultiplier),

        // Width is an approximation: CODE128 encodes roughly 11 modules per character plus
        // start, stop and check patterns. Over-estimating is safe — it only affects centring.
        PrintBlock.Barcode b => (
            (b.Value.Length + 4) * 11 * b.ModuleWidth,
            b.HeightDots + (b.ShowText ? baseCharHeightDots : 0)),

        // A QR is square. Version 1 is 21 modules and grows by 4 per version; 33 is a middle
        // estimate that keeps a realistic part-number payload from overlapping what follows.
        // Over-estimating only costs blank paper; under-estimating overprints the next block.
        PrintBlock.QrCode q => (33 * q.Scale, 33 * q.Scale),

        PrintBlock.Image i => (i.Bitmap.Width, i.Bitmap.Height),

        PrintBlock.Feed f => (0, f.Dots),

        PrintBlock.Cut => (0, 0),

        _ => (0, 0),
    };
}
