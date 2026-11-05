using Avalonia;

namespace Sirius.Terminal;

// Os blocos do Unicode (U+2580 a U+259F), desenhados como retângulos. A fonte não os encosta
// um no outro, e o desenho feito com eles, como o boneco do Claude, ficaria listrado.
public static class BlockElements
{
    public const int First = 0x2580;
    public const int Last = 0x259F;

    private static readonly Rect UpperLeft = new(0, 0, 0.5, 0.5);
    private static readonly Rect UpperRight = new(0.5, 0, 0.5, 0.5);
    private static readonly Rect LowerLeft = new(0, 0.5, 0.5, 0.5);
    private static readonly Rect LowerRight = new(0.5, 0.5, 0.5, 0.5);

    public static bool IsBlock(int rune) => rune is >= First and <= Last;

    // Partes da célula a preencher, em frações dela, e a força da cor.
    public static (Rect[] Parts, double Opacity) For(int rune) => rune switch
    {
        0x2580 => ([new Rect(0, 0, 1, 0.5)], 1),
        >= 0x2581 and <= 0x2587 => ([new Rect(0, 1 - (rune - 0x2580) / 8.0, 1, (rune - 0x2580) / 8.0)], 1),
        0x2588 => ([new Rect(0, 0, 1, 1)], 1),
        >= 0x2589 and <= 0x258F => ([new Rect(0, 0, (0x2590 - rune) / 8.0, 1)], 1),
        0x2590 => ([new Rect(0.5, 0, 0.5, 1)], 1),
        0x2591 => ([new Rect(0, 0, 1, 1)], 0.25),
        0x2592 => ([new Rect(0, 0, 1, 1)], 0.5),
        0x2593 => ([new Rect(0, 0, 1, 1)], 0.75),
        0x2594 => ([new Rect(0, 0, 1, 0.125)], 1),
        0x2595 => ([new Rect(0.875, 0, 0.125, 1)], 1),
        0x2596 => ([LowerLeft], 1),
        0x2597 => ([LowerRight], 1),
        0x2598 => ([UpperLeft], 1),
        0x2599 => ([UpperLeft, LowerLeft, LowerRight], 1),
        0x259A => ([UpperLeft, LowerRight], 1),
        0x259B => ([UpperLeft, UpperRight, LowerLeft], 1),
        0x259C => ([UpperLeft, UpperRight, LowerRight], 1),
        0x259D => ([UpperRight], 1),
        0x259E => ([UpperRight, LowerLeft], 1),
        0x259F => ([UpperRight, LowerLeft, LowerRight], 1),
        _ => ([], 1),
    };
}
