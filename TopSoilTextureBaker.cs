using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace WorldExporter;

public class TopSoilTextureBaker : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly Dictionary<(int soilTexId, int grassTexId, bool isTop), SKBitmap> bakedTextureCache;
    private readonly int texturePixelSize;
    private readonly float blockTextureSizeX;
    private readonly float blockTextureSizeY;
    private readonly float subpixelPaddingX;
    private readonly float subpixelPaddingY;

    public TopSoilTextureBaker(ICoreClientAPI capi)
    {
        this.capi = capi;
        this.bakedTextureCache = new Dictionary<(int, int, bool), SKBitmap>();

        var atlas = capi.BlockTextureAtlas;
        this.texturePixelSize = capi.Render.TextureSize;
        this.blockTextureSizeX = (float)texturePixelSize / atlas.Size.Width;
        this.blockTextureSizeY = (float)texturePixelSize / atlas.Size.Height;
        this.subpixelPaddingX = atlas.SubPixelPaddingX;
        this.subpixelPaddingY = atlas.SubPixelPaddingY;

        WorldExporterModSystem.WorldExporterLog($"TopSoilTextureBaker initialized: textureSize={texturePixelSize}px, blockTexSize=({blockTextureSizeX:F6},{blockTextureSizeY:F6}), padding=({subpixelPaddingX:F6},{subpixelPaddingY:F6})");
    }

    public SKBitmap BakeTexturePair(int atlasNumber, int soilTextureId, int grassTextureId, bool isTopFace)
    {
        var key = (soilTextureId, grassTextureId, isTopFace);
        if (bakedTextureCache.TryGetValue(key, out SKBitmap cached))
        {
            WorldExporterModSystem.WorldExporterLog($"BakeTexturePair: cache hit for soil={soilTextureId}, grass={grassTextureId}, isTop={isTopFace}");
            return cached;
        }

        WorldExporterModSystem.WorldExporterLog($"BakeTexturePair: baking soil={soilTextureId}, grass={grassTextureId}, isTop={isTopFace}, atlas={atlasNumber}");

        var atlasTexture = capi.BlockTextureAtlas.AtlasTextures[atlasNumber];
        SKBitmap atlasBitmap = DownloadAtlasFromGPU(atlasTexture);

        var soilTexPos = capi.BlockTextureAtlas.Positions[soilTextureId];
        var grassTexPos = capi.BlockTextureAtlas.Positions[grassTextureId];

        SKBitmap soilTexture = ExtractTextureRegion(atlasBitmap, soilTexPos, 0, 0);

        float grassUOffset = isTopFace ? blockTextureSizeX : 0;
        SKBitmap grassTexture = ExtractTextureRegion(atlasBitmap, grassTexPos, grassUOffset, 0);

        SKBitmap baked = BlendTextures(soilTexture, grassTexture);

        bakedTextureCache[key] = baked;

        atlasBitmap.Dispose();
        soilTexture.Dispose();
        grassTexture.Dispose();

        WorldExporterModSystem.WorldExporterLog($"BakeTexturePair: completed baking {texturePixelSize}x{texturePixelSize} texture");
        return baked;
    }

    private SKBitmap DownloadAtlasFromGPU(LoadedTexture texture)
    {
        int width = texture.Width;
        int height = texture.Height;

        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);

        byte[] pixels = new byte[width * height * 4];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        GL.BindTexture(TextureTarget.Texture2D, 0);

        return bitmap;
    }

    private SKBitmap ExtractTextureRegion(SKBitmap atlasBitmap, TextureAtlasPosition texPos, float uOffset, float vOffset)
    {
        float u1 = texPos.x1 + uOffset;
        float v1 = texPos.y1 + vOffset;
        float u2 = texPos.x2 + uOffset;
        float v2 = texPos.y2 + vOffset;

        int pixelX1 = (int)(u1 * atlasBitmap.Width);
        int pixelY1 = (int)(v1 * atlasBitmap.Height);
        int pixelX2 = (int)(u2 * atlasBitmap.Width);
        int pixelY2 = (int)(v2 * atlasBitmap.Height);

        int pixelWidth = Math.Max(1, pixelX2 - pixelX1);
        int pixelHeight = Math.Max(1, pixelY2 - pixelY1);

        pixelX1 = Math.Clamp(pixelX1, 0, atlasBitmap.Width - 1);
        pixelY1 = Math.Clamp(pixelY1, 0, atlasBitmap.Height - 1);
        pixelWidth = Math.Min(pixelWidth, atlasBitmap.Width - pixelX1);
        pixelHeight = Math.Min(pixelHeight, atlasBitmap.Height - pixelY1);

        var extracted = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        for (int y = 0; y < pixelHeight; y++)
        {
            for (int x = 0; x < pixelWidth; x++)
            {
                SKColor pixel = atlasBitmap.GetPixel(pixelX1 + x, pixelY1 + y);
                extracted.SetPixel(x, y, pixel);
            }
        }

        return extracted;
    }

    private SKBitmap BlendTextures(SKBitmap soil, SKBitmap grass)
    {
        int width = Math.Max(soil.Width, grass.Width);
        int height = Math.Max(soil.Height, grass.Height);

        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int soilX = (x * soil.Width) / width;
                int soilY = (y * soil.Height) / height;
                SKColor soilPixel = soil.GetPixel(soilX, soilY);

                int grassX = (x * grass.Width) / width;
                int grassY = (y * grass.Height) / height;
                SKColor grassPixel = grass.GetPixel(grassX, grassY);

                float grassAlpha = grassPixel.Alpha / 255.0f;
                byte r = (byte)(soilPixel.Red * (1 - grassAlpha) + grassPixel.Red * grassAlpha);
                byte g = (byte)(soilPixel.Green * (1 - grassAlpha) + grassPixel.Green * grassAlpha);
                byte b = (byte)(soilPixel.Blue * (1 - grassAlpha) + grassPixel.Blue * grassAlpha);

                result.SetPixel(x, y, new SKColor(r, g, b, 255));
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var bitmap in bakedTextureCache.Values)
        {
            bitmap?.Dispose();
        }
        bakedTextureCache.Clear();
    }
}
