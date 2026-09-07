namespace Witchlight;

/// <summary>
/// Reads pixels back off the graphics card.
///
/// The mod API uploads a texture and renders into one but never hands the result
/// back, so this calls the same OpenGL binding the game does.
///
/// Every OpenGL type stays inside the method body and none appears in a signature,
/// so nothing here resolves until it is called, and only a client calls it. A
/// dedicated server never looks for the library.
/// </summary>
internal static class Readback
{
    internal static byte[] Bgra(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        OpenTK.Graphics.OpenGL.GL.ReadPixels(
            0,
            0,
            width,
            height,
            OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
            OpenTK.Graphics.OpenGL.PixelType.UnsignedByte,
            pixels);
        return pixels;
    }
}
