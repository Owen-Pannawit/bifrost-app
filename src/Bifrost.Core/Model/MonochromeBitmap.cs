namespace Bifrost.Core.Model;

/// <summary>
/// A 1-bit image, ready for a thermal head. One bit per dot, row-major, set means burn.
/// </summary>
/// <remarks>
/// Deliberately not <c>Android.Graphics.Bitmap</c>. The IR must stay free of Android types so that
/// drivers remain testable without a device (IMP-02 §2.1); the app converts at the boundary.
///
/// Rows are padded to whole bytes because both ESC/POS raster and CPCL <c>EG</c> take byte-aligned
/// rows, and doing the padding once here keeps it out of every driver.
/// </remarks>
public sealed class MonochromeBitmap
{
    public MonochromeBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        BytesPerRow = (width + 7) / 8;
        Data = new byte[BytesPerRow * height];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row stride in bytes. Rows are byte-aligned; trailing bits in a row are unset.</summary>
    public int BytesPerRow { get; }

    /// <summary>Row-major, most-significant bit first within each byte.</summary>
    public byte[] Data { get; }

    public void Set(int x, int y, bool black = true)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;

        var index = (y * BytesPerRow) + (x / 8);
        var mask = (byte)(0x80 >> (x % 8));

        if (black) Data[index] |= mask;
        else Data[index] &= (byte)~mask;
    }

    public bool Get(int x, int y) =>
        (uint)x < (uint)Width
        && (uint)y < (uint)Height
        && (Data[(y * BytesPerRow) + (x / 8)] & (0x80 >> (x % 8))) != 0;

    /// <summary>
    /// A nested-squares target, for proving that image printing works at all.
    /// </summary>
    /// <remarks>
    /// Generated rather than shipped as an asset: no file to lose, no decoder to depend on, and
    /// the pattern is obviously right or obviously wrong at a glance. A photograph would prove
    /// less — a smudge in a photo is ambiguous, a broken square is not.
    /// </remarks>
    public static MonochromeBitmap TestPattern(int size = 96)
    {
        var bitmap = new MonochromeBitmap(size, size);

        for (var ring = 0; ring < size / 2; ring += size / 8)
        {
            for (var i = ring; i < size - ring; i++)
            {
                bitmap.Set(i, ring);
                bitmap.Set(i, size - 1 - ring);
                bitmap.Set(ring, i);
                bitmap.Set(size - 1 - ring, i);
            }
        }

        // A diagonal, so a mirrored or rotated image is immediately obvious.
        for (var i = 0; i < size; i++) bitmap.Set(i, i);

        return bitmap;
    }
}
