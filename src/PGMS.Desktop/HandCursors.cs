using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PGMS.Desktop;

/// <summary>
/// The open hand (ready to drag) and the closed hand (dragging) used to scroll a surface. WPF only has the pointing
/// hand of links, so both are drawn here and wrapped in a 32×32 .cur with the hot spot in the palm.
/// </summary>
internal static class HandCursors
{
    private static Cursor? open, closed;
    public static Cursor Open => open ??= Create(closedHand: false);
    public static Cursor Closed => closed ??= Create(closedHand: true);

    // Drawn pixel by pixel like the classic Windows cursors (proposal C approved by the user on 08/11/2026,
    // artifacts/acceptance/M1043-icons/propostas.png): '#' black outline, '.' white, blank transparent.
    // Fingers stay apart down to the knuckles and the thumb points out; the closed hand folds the fingers.
    private static readonly string[] OpenArt =
    [
        "        ##",
        "       #..###",
        "     ###..#..#",
        "    #..#..#..#",
        "    #..#..#..###",
        "    #..#..#..#..#",
        "    #..#..#..#..#",
        "    #..#..#..#..#",
        "    #..#..#..#..#",
        " ## #..#..#..#..#",
        "#..##..#..#..#..#",
        "#...#...........#",
        " #..............#",
        "  #.............#",
        "  #............#",
        "   #...........#",
        "   #..........#",
        "    #.........#",
        "    #.........#",
        "    #.........#",
        "    ###########",
    ];
    private static readonly string[] ClosedArt =
    [
        "", "", "", "", "", "",
        "     ## ## ## ##",
        "    #..#..#..#..#",
        "    #..#..#..#..#",
        " ## #..#..#..#..#",
        "#..##..#..#..#..#",
        "#...#...........#",
        " #..............#",
        "  #.............#",
        "  #............#",
        "   #...........#",
        "   #..........#",
        "    #.........#",
        "    #.........#",
        "    #.........#",
        "    ###########",
    ];
    private const int Left = 7, Top = 5;

    internal static BitmapSource Render(bool closedHand)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        var art = closedHand ? ClosedArt : OpenArt;
        for (int row = 0; row < art.Length; row++)
            for (int column = 0; column < art[row].Length; column++)
            {
                byte value = art[row][column] switch { '#' => 0, '.' => 255, _ => 1 };
                if (value == 1) continue;
                int at = ((Top + row) * size + Left + column) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value; pixels[at + 3] = 255;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Cursor Create(bool closedHand) => new(new MemoryStream(CursorFile(Render(closedHand), 15, 15)), scaleWithDpi: true);

    /// <summary>A .cur holding one 32-bit image: the BMP rows bottom-up, then the AND mask (1 = transparent).</summary>
    internal static byte[] CursorFile(BitmapSource image, ushort hotX, ushort hotY)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        image.CopyPixels(pixels, size * 4, 0);
        int maskStride = size / 8, imageBytes = 40 + pixels.Length + maskStride * size;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);
        w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
        w.Write(hotX); w.Write(hotY); w.Write(imageBytes); w.Write(22);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(pixels.Length + maskStride * size);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);
        for (int y = size - 1; y >= 0; y--)
            for (int b = 0; b < maskStride; b++)
            {
                byte bits = 0;
                for (int i = 0; i < 8; i++) if (pixels[(y * size + b * 8 + i) * 4 + 3] == 0) bits |= (byte)(0x80 >> i);
                w.Write(bits);
            }
        return stream.ToArray();
    }
}
