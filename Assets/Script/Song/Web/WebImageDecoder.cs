using UnityEngine;
using YARG.Core.IO;
using YARG.Core.Logging;

namespace YARG.Song.Web
{
    /// <summary>
    /// Decodes png/jpg covers with Unity's own decoder, since the native STB2CSharp library that
    /// <see cref="YARGImage"/> uses is not part of the web build. Must run on the main thread, which is
    /// the only thread on the web.
    /// </summary>
    internal static class WebImageDecoder
    {
        public static void Install()
        {
            YARGImage.DecoderOverride = Decode;
        }

        private static YARGImage Decode(FixedArray<byte> file)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(file.ReadOnlySpan.ToArray(), false))
                {
                    return null;
                }

                int width = texture.width;
                int height = texture.height;
                var colors = texture.GetPixels32();
                var pixels = FixedArray<byte>.Alloc(width * height * 4);

                // Unity's rows go bottom to top, STB2CSharp's (which YARGImage users expect) top to bottom
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    int source = (height - 1 - y) * width;
                    int target = y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        var color = colors[source + x];
                        int i = target + x * 4;
                        pixels[i] = color.r;
                        pixels[i + 1] = color.g;
                        pixels[i + 2] = color.b;
                        pixels[i + 3] = color.a;
                    }
                }

                return YARGImage.FromPixels(pixels, width, height, ImageFormat.RGBA);
            }
            catch (System.Exception e)
            {
                YargLogger.LogException(e, "Failed to decode an image");
                return null;
            }
            finally
            {
                Object.Destroy(texture);
            }
        }
    }
}
