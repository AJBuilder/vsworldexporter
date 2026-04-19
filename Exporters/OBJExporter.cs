using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldExporter.Exporters;

public class OBJExporter : IFormatExporter
{
    public void Export(
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> meshData,
        BlockPos exportOrigin,
        string outputPath,
        ICoreClientAPI capi)
    {
        string baseOutputPath = Path.GetFileNameWithoutExtension(outputPath);
        string outputDir = Path.GetDirectoryName(outputPath);
        string texturesDir = Path.Combine(outputDir, "textures");
        Directory.CreateDirectory(texturesDir);

        // Export texture atlas first
        ExportTextureAtlas(texturesDir, capi);

        foreach (var kvp in meshData)
        {
            EnumChunkRenderPass pass = kvp.Key;
            List<MeshDataWithPosition> meshList = kvp.Value;

            if (meshList.Count == 0) continue;

            string passName = pass.ToString().ToLower();
            string passObjPath = Path.Combine(outputDir, $"{baseOutputPath}_{passName}.obj");
            string mtlFilename = $"{baseOutputPath}_{passName}.mtl";
            string mtlPath = Path.Combine(outputDir, mtlFilename);

            WorldExporterModSystem.WorldExporterChat($"Exporting {passName} pass ({meshList.Count} meshes)...");

            ExportRenderPassToOBJ(
                meshList,
                exportOrigin,
                passObjPath,
                mtlFilename,
                mtlPath,
                capi);
        }

        WorldExporterModSystem.WorldExporterChat($"OBJ export complete: {meshData.Count} render passes");
    }

    private void ExportRenderPassToOBJ(
        List<MeshDataWithPosition> meshList,
        BlockPos exportOrigin,
        string objPath,
        string mtlFilename,
        string mtlPath,
        ICoreClientAPI capi)
    {
        using var objWriter = new StreamWriter(objPath);
        objWriter.WriteLine("# Exported by vsworldexporter");
        objWriter.WriteLine($"mtllib {mtlFilename}");
        objWriter.WriteLine();
        objWriter.WriteLine("usemtl block_atlas");
        objWriter.WriteLine();

        int vertexIndexBase = 1;

        foreach (var meshWithPos in meshList)
        {
            MeshData mesh = meshWithPos.Mesh;
            Vec3i worldPos = meshWithPos.WorldPosition;
            Vec3f offset = new Vec3f(
                (worldPos - exportOrigin.AsVec3i).X,
                (worldPos - exportOrigin.AsVec3i).Y,
                (worldPos - exportOrigin.AsVec3i).Z);

            WriteVertices(objWriter, mesh, offset);
            WriteUVs(objWriter, mesh);
            WriteNormals(objWriter, mesh);
            WriteFaces(objWriter, mesh, vertexIndexBase);

            vertexIndexBase += mesh.VerticesCount;
        }

        WriteMTLFile(mtlPath);
    }

    private void WriteVertices(StreamWriter writer, MeshData mesh, Vec3f offset)
    {
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3] + offset.X;
            float y = mesh.xyz[i * 3 + 1] + offset.Y;
            float z = mesh.xyz[i * 3 + 2] + offset.Z;
            writer.WriteLine(FormattableString.Invariant($"v {x:F6} {y:F6} {z:F6}"));
        }
    }

    private void WriteUVs(StreamWriter writer, MeshData mesh)
    {
        // UVs are already in atlas coordinates (0.0-1.0), just write them directly
        if (mesh.Uv != null && mesh.Uv.Length >= mesh.VerticesCount * 2)
        {
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float u = mesh.Uv[i * 2];
                float v = mesh.Uv[i * 2 + 1];
                writer.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
            }
        }
        else
        {
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                writer.WriteLine("vt 0 0");
            }
        }
    }

    private void WriteNormals(StreamWriter writer, MeshData mesh)
    {
        if (mesh.Normals != null && mesh.Normals.Length >= mesh.VerticesCount * 3)
        {
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float nx = mesh.Normals[i * 3];
                float ny = mesh.Normals[i * 3 + 1];
                float nz = mesh.Normals[i * 3 + 2];
                writer.WriteLine(FormattableString.Invariant($"vn {nx:F6} {ny:F6} {nz:F6}"));
            }
        }
        else
        {
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                writer.WriteLine("vn 0 1 0");
            }
        }
    }

    private void WriteFaces(StreamWriter writer, MeshData mesh, int indexBase)
    {
        // All faces use the same material (atlas texture)
        for (int i = 0; i < mesh.IndicesCount; i += 3)
        {
            int vi0 = (int)mesh.Indices[i];
            int vi1 = (int)mesh.Indices[i + 1];
            int vi2 = (int)mesh.Indices[i + 2];

            int v0 = indexBase + vi0;
            int v1 = indexBase + vi1;
            int v2 = indexBase + vi2;

            writer.WriteLine($"f {v0}/{v0}/{v0} {v1}/{v1}/{v1} {v2}/{v2}/{v2}");
        }
    }

    private void WriteMTLFile(string mtlPath)
    {
        using var mtlWriter = new StreamWriter(mtlPath);
        mtlWriter.WriteLine("# Material exported by vsworldexporter");
        mtlWriter.WriteLine();
        mtlWriter.WriteLine("newmtl block_atlas");
        mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");
        mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");
        mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");
        mtlWriter.WriteLine("d 1.0");
        mtlWriter.WriteLine("illum 2");
        mtlWriter.WriteLine("map_Kd textures/block_atlas_0.png");
    }

    private void ExportTextureAtlas(string texturesDir, ICoreClientAPI capi)
    {
        var atlasTextures = capi.BlockTextureAtlas.AtlasTextures;

        if (atlasTextures == null || atlasTextures.Count == 0)
        {
            WorldExporterModSystem.WorldExporterLog("Warning: No atlas textures found");
            return;
        }

        for (int i = 0; i < atlasTextures.Count; i++)
        {
            var loadedTexture = atlasTextures[i];
            if (loadedTexture == null || loadedTexture.TextureId == 0)
            {
                WorldExporterModSystem.WorldExporterLog($"Warning: Atlas texture {i} is null or has invalid TextureId");
                continue;
            }

            string atlasPath = Path.Combine(texturesDir, $"block_atlas_{i}.png");

            try
            {
                DownloadTextureFromGPU(loadedTexture, atlasPath);
                WorldExporterModSystem.WorldExporterChat($"Exported texture atlas {i} ({loadedTexture.Width}x{loadedTexture.Height})");
            }
            catch (Exception e)
            {
                WorldExporterModSystem.WorldExporterLog($"Error exporting atlas {i}: {e.Message}");
            }
        }
    }

    private void DownloadTextureFromGPU(LoadedTexture texture, string outputPath)
    {
        int width = texture.Width;
        int height = texture.Height;

        // Bind the texture
        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);

        // Download pixels from GPU
        byte[] pixels = new byte[width * height * 4]; // RGBA
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // Create SKBitmap and save
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        // Save directly without flipping
        bitmap.Encode(SKEncodedImageFormat.Png, 100).SaveTo(File.OpenWrite(outputPath));

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }
}
