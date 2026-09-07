using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Witchlight;

/// <summary>
/// Renders the player's own seraph into a picture.
///
/// The character screen already renders a seraph in flat orthographic mode, so
/// this asks for the same render into a buffer of its own and reads the result
/// back. The picture carries the real skin, hair, clothes and armour the player
/// is wearing at that moment.
///
/// The render must happen inside a frame, so this is a renderer that does nothing
/// until it is asked and then renders once. It registers in the GUI pass, which
/// is the state the character screen renders under.
///
/// Only the player's own client can do this, because no other machine has that
/// seraph loaded. That is why the picture travels rather than a description.
/// </summary>
public sealed class PortraitCapture : IRenderer
{
    /// <summary>
    /// The width and height of the picture to send. The map draws it at forty
    /// pixels, and this is enough for a dense screen.
    /// </summary>
    public const int Size = 256;

    /// <summary>
    /// The width and height of the canvas to draw into.
    ///
    /// Larger than the picture. This cannot know exactly where the seraph will
    /// land, so the canvas gives it room and <see cref="Crop"/> cuts the picture
    /// from wherever it did.
    /// </summary>
    private const int Canvas = 640;

    /// <summary>
    /// Where the seraph stands in the canvas, how tall it is drawn, and how far
    /// back it sits.
    ///
    /// A seraph does not appear at the position given. Measured, it lands well
    /// over a hundred pixels to the right of it. The canvas is therefore far
    /// larger than the picture and the figure is placed near its left, so it has
    /// room to land wherever it lands.
    /// </summary>
    private const double Origin = Canvas / 8.0;

    private const double HeadRoom = 80;

    private const double Depth = 250;

    private const float Stature = 260;

    /// <summary>
    /// Which way the seraph faces, in radians.
    ///
    /// A seraph drawn at zero stands in profile, and a quarter turn back from that
    /// puts it face to the viewer.
    ///
    /// The character screen adds three tenths of a radian to that quarter turn for
    /// its three-quarter pose. This leaves the extra turn off, because the face is
    /// going to be cropped out and wants to be square on.
    /// </summary>
    private const float Facing = -1.5707963f;

    /// <summary>
    /// How much of a seraph, measured from the crown down, is its head.
    ///
    /// Three tenths. Measured by cutting a real seraph at several fractions: a
    /// quarter clips the jaw, and a third gives half the square to shoulder.
    /// </summary>
    private const double HeadShare = 0.30;

    /// <summary>
    /// Where the light comes from, over the viewer's left shoulder.
    ///
    /// This is the value the game leaves `lightPosition` at, so it is both a
    /// sensible light and the right value to restore afterwards.
    /// </summary>
    private static readonly Vec3f Light = new(0.7071068f, -0.7071068f, 0f);

    private readonly ICoreClientAPI _capi;
    private Action<byte[]?, string>? _then;

    public PortraitCapture(ICoreClientAPI capi)
    {
        _capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "witchlight-portrait");
    }

    public double RenderOrder => 1.01;

    public int RenderRange => 0;

    /// <summary>Requests a picture. The callback runs on the next frame.</summary>
    public void Take(Action<byte[]?, string> then) => _then = then;

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        var then = _then;
        if (then is null)
        {
            return;
        }
        _then = null;

        try
        {
            var png = Draw(dt, out var said);
            then(png, said);
        }
        catch (Exception error)
        {
            then(null, error.Message);
        }
    }

    /// <summary>
    /// Renders the seraph once, into an offscreen buffer.
    ///
    /// Reproduces the character screen's setup, which is the one place in the game
    /// that draws a seraph flat. The GUI shader has to be the one in use, the
    /// model view matrix has to be pushed and tilted, and `lightPosition` has to
    /// point somewhere or the entity is lit by nothing. Without all three the
    /// render produces an empty picture and no error.
    /// </summary>
    private byte[]? Draw(float dt, out string said)
    {
        var entity = _capi.World?.Player?.Entity;
        if (entity is null)
        {
            said = "there is no player to draw";
            return null;
        }

        var render = _capi.Render;
        var previous = render.CurrentFrameBuffer;
        FrameBufferRef? buffer = null;

        try
        {
            buffer = render.CreateFrameBuffer(Attributes());
            render.CurrentFrameBuffer = buffer;
            render.GlViewport(0, 0, Canvas, Canvas);

            // Clear to transparent, so the card behind shows through around the
            // seraph. Clear depth with it, since the entity is solid.
            render.ClearFrameBuffer(buffer, new[] { 0f, 0f, 0f, 0f }, true, true);

            // Pass true, not false. The flag chooses between two orthographic
            // depth ranges, and the shallow one is for flat GUI work at the very
            // front. A seraph stands four hundred units back and the shallow range
            // clips it away entirely, with no error. The game passes true wherever
            // it draws something solid off screen.
            //
            // This also pushes both matrix stacks, so its partner is
            // PerspectiveMode rather than a second call to itself. Pairing it with
            // itself leaks two stack entries per picture.
            render.OrthoMode(Canvas, Canvas, true);

            // Set nothing else. The character screen sets no depth, cull or blend
            // state of its own, so `RenderEntityToGui` manages what it needs.
            // Every piece of state set here has to be restored exactly for the
            // rest of the frame to survive.

            // Leave whatever is drawing the rest of the frame in place. The
            // character screen never activates a shader and writes to the one
            // already in use. Stopping one here left the game's aim renderer
            // setting a uniform on an inactive shader, which crashes the client.
            // Activate a shader only when there is none, and restore what was
            // found either way.
            var before = render.CurrentActiveShader;
            var gui = render.GetEngineShader(EnumShaderProgram.Gui);
            var borrowed = before != gui;
            var wearing = borrowed ? "no gui shader was active" : "the gui shader was already active";

            if (borrowed)
            {
                gui.Use();
            }

            try
            {
                render.GlPushMatrix();
                render.GlTranslate(0, 0, 150);

                // The character screen tilts the view fourteen degrees down
                // before drawing. Leave the tilt off, for the same reason the pose
                // is square on: this picture is a face to be cropped out.
                gui.Uniform("lightPosition", Light);

                // The character screen's own numbers, centred. These are known to
                // put a seraph on screen.
                render.RenderEntityToGui(
                    dt, entity, Origin, HeadRoom, Depth, Facing, Stature, ColorUtil.WhiteArgb);
            }
            finally
            {
                // Restore the game's own light first, while the shader it belongs
                // to is still the one in use.
                gui.Uniform("lightPosition", Light);
                render.GlPopMatrix();

                if (borrowed)
                {
                    gui.Stop();
                    before?.Use();
                }
            }

            var picture = Frame(Readback.Bgra(Canvas, Canvas), out said);
            said = $"{said}, {wearing}";
            return picture;
        }
        finally
        {
            render.CurrentFrameBuffer = previous;
            if (buffer is not null)
            {
                render.DestroyFrameBuffer(buffer);
            }

            // Restore what the rest of the frame expects. PerspectiveMode pops
            // the two matrix stacks OrthoMode pushed, which puts the projection
            // back to whatever this interrupted.
            render.PerspectiveMode();
            render.GlViewport(0, 0, render.FrameWidth, render.FrameHeight);
        }
    }

    /// <summary>
    /// The box around everything drawn in a band of rows.
    ///
    /// <see cref="Crop"/> scans twice with this: once over the whole canvas to
    /// find the figure, and once over the top of the figure to find the head.
    /// </summary>
    private readonly struct Drawn
    {
        private Drawn(int left, int right, int top, int bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        public int Left { get; }
        public int Right { get; }
        public int Top { get; }
        public int Bottom { get; }

        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
        public int MiddleX => (Left + Right) / 2;

        /// <summary>Returns the box around the opaque pixels between two rows, or
        ///  an empty box when nothing was drawn there.</summary>
        public static Drawn? In(byte[] bgra, int fromRow, int uptoRow)
        {
            int left = Canvas, right = -1, top = Canvas, bottom = -1;

            for (var y = Math.Max(0, fromRow); y < Math.Min(Canvas, uptoRow); y++)
            {
                for (var x = 0; x < Canvas; x++)
                {
                    if (bgra[(y * Canvas + x) * 4 + 3] == 0)
                    {
                        continue;
                    }

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            return right < 0 ? null : new Drawn(left, right, top, bottom);
        }

        /// <summary>
        /// Returns which sides of the canvas the figure ran off, if any.
        ///
        /// A figure touching an edge was cut off by it, so what is cropped from it
        /// is a piece of a seraph rather than a whole small one. The picture does
        /// not show the difference, and this names where to look.
        /// </summary>
        public string Edges()
        {
            var against = new List<string>();
            if (Left == 0) against.Add("left");
            if (Right == Canvas - 1) against.Add("right");
            if (Top == 0) against.Add("top");
            if (Bottom == Canvas - 1) against.Add("bottom");
            return against.Count == 0 ? "" : $", CUT OFF at the {string.Join(" and ", against)}";
        }
    }

    /// <summary>
    /// Finds the seraph in the buffer and crops to its head and shoulders.
    ///
    /// Where the figure lands depends on the model's height, the GUI scale and
    /// whatever a mod has changed, so this scans for what was actually drawn
    /// rather than trusting a position. Refuses a picture with nothing in it,
    /// since an empty portrait looks like a working one until somebody opens the
    /// map and finds a hole in the card.
    /// </summary>
    private static byte[]? Frame(byte[] bgra, out string said)
    {
        if (Drawn.In(bgra, 0, Canvas) is not { } figure)
        {
            said = $"the seraph drew nothing into a {Canvas}x{Canvas} canvas";
            return null;
        }

        // The head is the top of the figure. The first row drawn is the crown,
        // because the entity renderer turns the model half a turn about X and this
        // reads the rows back in stored order.
        var side = Math.Max(1, (int)(figure.Height * HeadShare));
        var head = Drawn.In(bgra, figure.Top, figure.Top + side);

        // Centre on the head's own columns rather than the figure's. A seraph in
        // a coat is wider at the shoulder than at the ear, and centring on the
        // whole figure walks the face off to one side.
        var x0 = (head?.MiddleX ?? Canvas / 2) - side / 2;
        var y0 = figure.Top - side / 12;

        said = $"{figure.Width}x{figure.Height} found at {figure.Left},{figure.Top}, "
            + $"head {side}x{side} at {x0},{y0}{figure.Edges()}";
        return Encode(bgra, x0, y0, side);
    }

    private static FramebufferAttrs Attributes() => new("witchlight-portrait", Canvas, Canvas)
    {
        Attachments = new[]
        {
            new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                Texture = new RawTexture
                {
                    Width = Canvas,
                    Height = Canvas,
                    PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                    PixelFormat = EnumTexturePixelFormat.Rgba,
                    MinFilter = EnumTextureFilter.Linear,
                    MagFilter = EnumTextureFilter.Linear,
                    WrapS = EnumTextureWrap.ClampToEdge,
                    WrapT = EnumTextureWrap.ClampToEdge,
                },
            },
            new FramebufferAttrsAttachment
            {
                AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                Texture = new RawTexture
                {
                    Width = Canvas,
                    Height = Canvas,
                    PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                    PixelFormat = EnumTexturePixelFormat.DepthComponent,
                    MinFilter = EnumTextureFilter.Nearest,
                    MagFilter = EnumTextureFilter.Nearest,
                    WrapS = EnumTextureWrap.ClampToEdge,
                    WrapT = EnumTextureWrap.ClampToEdge,
                },
            },
        },
    };

    /// <summary>
    /// Encodes one square of the canvas as a PNG at <see cref="Size"/>.
    ///
    /// Keeps the buffer's row order. A frame buffer's first row is conventionally
    /// its bottom one, but reversing the rows here delivers every seraph standing
    /// on its head, so this buffer does not follow that convention.
    /// </summary>
    private static byte[] Encode(byte[] bgra, int x0, int y0, int side)
    {
        var info = new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var cut = new SKBitmap(info);
        var rows = new byte[side * 4];
        for (var y = 0; y < side; y++)
        {
            var line = y0 + y;
            if (line < 0 || line >= Canvas)
            {
                continue;
            }

            // Copy only the part of the row inside the canvas. The rest of the
            // bitmap stays transparent.
            var from = Math.Max(0, x0);
            var upto = Math.Min(Canvas, x0 + side);
            if (upto <= from)
            {
                continue;
            }

            Array.Clear(rows);
            Buffer.BlockCopy(bgra, (line * Canvas + from) * 4, rows, (from - x0) * 4, (upto - from) * 4);
            Marshal.Copy(rows, 0, cut.GetPixels() + y * side * 4, side * 4);
        }

        using var scaled = cut.Resize(
            new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(scaled ?? cut);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void Dispose()
    {
    }
}
