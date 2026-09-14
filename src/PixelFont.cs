using Godot;

namespace Atomcraft.TestHarness;

/// <summary>
/// A 3x5 bitmap font, and the one-row texture atlas the overlay draws it from.
///
/// Overlay text has to stay legible at one simulation cell per glyph. A cell is 8 world
/// units, so at the game's own maximum zoom of 1.5 it is 12 screen pixels across: any font
/// with real letterforms is illegible at that size, and every scalable font the engine
/// offers blurs, because a glyph outline rasterized at 12px lands on fractional pixel
/// boundaries. A bitmap font drawn at an integer scale, from integer screen positions,
/// through a nearest-neighbor filter, is exact instead: each font pixel is a whole number of
/// screen pixels, every time. That is the whole reason this file exists rather than a call
/// to Godot's font rendering.
///
/// 3x5 is the smallest grid that still separates all of 0-9 and A-Z. With one column and one
/// row of spacing it occupies the 4x6 screen-pixel cell the overlay advertises. Digits and
/// uppercase are the shapes this size is good at. Lowercase has no room for true descenders,
/// so it is drawn as distinct short forms rather than as scaled-down uppercase, and some
/// punctuation ('$', '&amp;', '@') is approximate. If a label has to be read exactly, say it
/// in digits and capitals, or step up a size.
/// </summary>
public static class PixelFont
{
    /// <summary>Lit area of one glyph, in font pixels.</summary>
    public const int GlyphWidth = 3;
    public const int GlyphHeight = 5;

    /// <summary>Pen movement per character: the glyph plus one blank column.</summary>
    public const int Advance = 4;

    /// <summary>Baseline-to-baseline for a multi-line label: the glyph plus one blank row.</summary>
    public const int LineHeight = 6;

    private const char First = ' ';
    private const char Last = '~';

    /// <summary>
    /// One entry per printable ASCII character, from space to tilde, as five rows of three
    /// cells separated by spaces. '#' is lit. Laid out this way so a glyph can be read and
    /// corrected in place; the atlas is built from it at first use.
    /// </summary>
    private static readonly string[] Glyphs =
    {
        "... ... ... ... ...",   // (space)
        ".#. .#. .#. ... .#.",   // !
        "#.# #.# ... ... ...",   // "
        "#.# ### #.# ### #.#",   // #
        ".## ##. .#. ..# ##.",   // $
        "#.# ..# .#. #.. #.#",   // %
        ".#. #.# .#. #.# .##",   // &
        ".#. .#. ... ... ...",   // '
        "..# .#. .#. .#. ..#",   // (
        "#.. .#. .#. .#. #..",   // )
        "... #.# .#. #.# ...",   // *
        "... .#. ### .#. ...",   // +
        "... ... ... .#. #..",   // ,
        "... ... ### ... ...",   // -
        "... ... ... ... .#.",   // .
        "..# ..# .#. #.. #..",   // /
        "### #.# #.# #.# ###",   // 0
        ".#. ##. .#. .#. ###",   // 1
        "### ..# ### #.. ###",   // 2
        "### ..# ### ..# ###",   // 3
        "#.# #.# ### ..# ..#",   // 4
        "### #.. ### ..# ###",   // 5
        "### #.. ### #.# ###",   // 6
        "### ..# ..# ..# ..#",   // 7
        "### #.# ### #.# ###",   // 8
        "### #.# ### ..# ###",   // 9
        "... .#. ... .#. ...",   // :
        "... .#. ... .#. #..",   // ;
        "..# .#. #.. .#. ..#",   // <
        "... ### ... ### ...",   // =
        "#.. .#. ..# .#. #..",   // >
        "##. ..# .#. ... .#.",   // ?
        ".#. #.# ### #.. .##",   // @
        ".#. #.# ### #.# #.#",   // A
        "##. #.# ##. #.# ##.",   // B
        ".## #.. #.. #.. .##",   // C
        "##. #.# #.# #.# ##.",   // D
        "### #.. ##. #.. ###",   // E
        "### #.. ##. #.. #..",   // F
        ".## #.. #.# #.# .##",   // G
        "#.# #.# ### #.# #.#",   // H
        "### .#. .#. .#. ###",   // I
        "..# ..# ..# #.# .#.",   // J
        "#.# #.# ##. #.# #.#",   // K
        "#.. #.. #.. #.. ###",   // L
        "#.# ### ### #.# #.#",   // M
        "#.# ##. ### .## #.#",   // N
        ".#. #.# #.# #.# .#.",   // O
        "##. #.# ##. #.. #..",   // P
        ".#. #.# #.# ### .##",   // Q
        "##. #.# ##. #.# #.#",   // R
        ".## #.. .#. ..# ##.",   // S
        "### .#. .#. .#. .#.",   // T
        "#.# #.# #.# #.# ###",   // U
        "#.# #.# #.# #.# .#.",   // V
        "#.# #.# ### ### #.#",   // W
        "#.# #.# .#. #.# #.#",   // X
        "#.# #.# .#. .#. .#.",   // Y
        "### ..# .#. #.. ###",   // Z
        ".## .#. .#. .#. .##",   // [
        "#.. #.. .#. ..# ..#",   // \
        "##. .#. .#. .#. ##.",   // ]
        ".#. #.# ... ... ...",   // ^
        "... ... ... ... ###",   // _
        "#.. .#. ... ... ...",   // `
        "... ##. .## #.# .##",   // a
        "#.. #.. ##. #.# ##.",   // b
        "... .## #.. #.. .##",   // c
        "..# ..# .## #.# .##",   // d
        "... .#. #.# ##. .##",   // e
        ".## .#. ### .#. .#.",   // f
        "... .## #.# .## ##.",   // g
        "#.. #.. ##. #.# #.#",   // h
        ".#. ... .#. .#. .#.",   // i
        "..# ... ..# #.# .#.",   // j
        "#.. #.# ##. ##. #.#",   // k
        "##. .#. .#. .#. .##",   // l
        "... ### ### #.# #.#",   // m
        "... ##. #.# #.# #.#",   // n
        "... .#. #.# #.# .#.",   // o
        "... ##. #.# ##. #..",   // p
        "... .## #.# .## ..#",   // q
        "... .## #.. #.. #..",   // r
        "... .## .#. ..# ##.",   // s
        ".#. ### .#. .#. .##",   // t
        "... #.# #.# #.# .##",   // u
        "... #.# #.# #.# .#.",   // v
        "... #.# #.# ### ###",   // w
        "... #.# .#. .#. #.#",   // x
        "... #.# #.# .## ##.",   // y
        "... ### .#. #.. ###",   // z
        "..# .#. ##. .#. ..#",   // {
        ".#. .#. .#. .#. .#.",   // |
        "#.. .#. .## .#. #..",   // }
        "... ..# ### #.. ...",   // ~
    };

    private static ImageTexture? _atlas;

    /// <summary>
    /// The glyph atlas: one row of <see cref="Advance"/>-wide slots in character order, white
    /// where lit and fully transparent elsewhere, so the draw colour comes from the modulate
    /// and one texture serves every colour. Built once, on the first label drawn.
    /// </summary>
    internal static ImageTexture Atlas =>
        _atlas ??= ImageTexture.CreateFromImage(Image.CreateFromData(
            AtlasWidth, GlyphHeight, useMipmaps: false, Image.Format.Rgba8, BuildAtlasData()));

    /// <summary>Width of <see cref="Atlas"/> in pixels: one <see cref="Advance"/>-wide slot per character.</summary>
    public static int AtlasWidth => Glyphs.Length * Advance;

    /// <summary>
    /// Checks every glyph in the table is the shape the atlas builder expects, and throws
    /// naming the offender if one is not.
    ///
    /// The glyph table is hand-written data, and the failure mode of a typo in it is a
    /// silently misaligned atlas: every character after the bad one draws a sliver of its
    /// neighbour. This is pure arithmetic over strings, with no texture and no display
    /// involved, so the ordinary headless suite can catch that rather than leaving it to
    /// whoever next looks closely at a screenshot.
    /// </summary>
    public static void Validate() => BuildAtlasData();

    /// <summary>
    /// Where <paramref name="c"/> lives in <see cref="Atlas"/>. Anything outside printable
    /// ASCII draws as '?', which is more useful on screen than a blank or a crash.
    /// </summary>
    public static Rect2 Region(char c)
    {
        var index = (c < First || c > Last) ? '?' - First : c - First;
        return new Rect2(index * Advance, 0, GlyphWidth, GlyphHeight);
    }

    /// <summary>
    /// The size of a label in font pixels at scale 1, honoring embedded newlines. Trailing
    /// spacing is not counted, so a one-character label measures 3x5 rather than 4x6: the
    /// spacing exists to separate characters from each other, and counting it would push
    /// every centered label half a pixel off.
    /// </summary>
    public static Vector2I Measure(string text)
    {
        var lines = text.Split('\n');
        var widest = 0;
        foreach (var line in lines)
            widest = Math.Max(widest, line.Length);
        if (widest == 0)
            return Vector2I.Zero;
        return new Vector2I(widest * Advance - (Advance - GlyphWidth),
                            lines.Length * LineHeight - (LineHeight - GlyphHeight));
    }

    private static byte[] BuildAtlasData()
    {
        var width = AtlasWidth;
        var data = new byte[width * GlyphHeight * 4];

        for (var index = 0; index < Glyphs.Length; index++)
        {
            var rows = Glyphs[index].Split(' ');
            if (rows.Length != GlyphHeight)
                throw new AssertionException(
                    $"PixelFont glyph {index} ('{(char)(First + index)}') has {rows.Length} rows, " +
                    $"expected {GlyphHeight}");

            for (var y = 0; y < GlyphHeight; y++)
            {
                if (rows[y].Length != GlyphWidth)
                    throw new AssertionException(
                        $"PixelFont glyph {index} ('{(char)(First + index)}') row {y} is " +
                        $"{rows[y].Length} cells wide, expected {GlyphWidth}");

                for (var x = 0; x < GlyphWidth; x++)
                {
                    if (rows[y][x] != '#')
                        continue;
                    var offset = ((y * width) + index * Advance + x) * 4;
                    data[offset] = data[offset + 1] = data[offset + 2] = data[offset + 3] = byte.MaxValue;
                }
            }
        }

        return data;
    }
}
