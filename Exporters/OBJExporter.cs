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
    private TopSoilMeshProcessor meshProcessor;

    public void Export(
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> meshData,
        BlockPos exportOrigin,
        string outputPath,
        ICoreClientAPI capi)
    {
        // Create a directory for this export
        string baseOutputPath = Path.GetFileNameWithoutExtension(outputPath);
        string parentDir = Path.GetDirectoryName(outputPath);
        string exportDir = Path.Combine(parentDir, baseOutputPath);
        Directory.CreateDirectory(exportDir);

        // Initialize TopSoil processor
        meshProcessor = new TopSoilMeshProcessor(capi);

        // Export texture atlas first (directly in export directory)
        ExportTextureAtlas(exportDir, capi);

        // Combined OBJ export
        string combinedObjPath = Path.Combine(exportDir, "world.obj");
        string mtlFilename = "world.mtl";
        string mtlPath = Path.Combine(exportDir, mtlFilename);

        using var objWriter = new StreamWriter(combinedObjPath);
        objWriter.WriteLine("# Exported by vsworldexporter");
        objWriter.WriteLine($"mtllib {mtlFilename}");
        objWriter.WriteLine();

        var allUsedAtlases = new HashSet<int>();
        int vertexIndexBase = 1;

        foreach (var kvp in meshData)
        {
            EnumChunkRenderPass pass = kvp.Key;
            List<MeshDataWithPosition> meshList = kvp.Value;

            if (meshList.Count == 0) continue;

            string passName = pass.ToString().ToLower();
            WorldExporterModSystem.WorldExporterChat($"Exporting {passName} pass ({meshList.Count} meshes)...");

            if (pass == EnumChunkRenderPass.TopSoil)
            {
                vertexIndexBase = ExportTopSoilToWriter(objWriter, meshList, exportOrigin, passName, vertexIndexBase, allUsedAtlases, capi);
            }
            else
            {
                vertexIndexBase = ExportRenderPassToWriter(objWriter, meshList, exportOrigin, passName, vertexIndexBase, allUsedAtlases, capi);
            }
        }

        WriteMTLFile(mtlPath, allUsedAtlases);
        WorldExporterModSystem.WorldExporterChat($"OBJ export complete: {meshData.Count} render passes in {combinedObjPath}");
    }

    private int ExportRenderPassToWriter(
        StreamWriter objWriter,
        List<MeshDataWithPosition> meshList,
        BlockPos exportOrigin,
        string passName,
        int vertexIndexBase,
        HashSet<int> allUsedAtlases,
        ICoreClientAPI capi)
    {
        // Write object declaration
        objWriter.WriteLine($"o {passName}");
        objWriter.WriteLine();

        int currentAtlas = -1;

        foreach (var meshWithPos in meshList)
        {
            MeshData mesh = meshWithPos.Mesh;
            int atlasNumber = meshWithPos.AtlasNumber;
            allUsedAtlases.Add(atlasNumber);

            // Write material change if atlas changed
            if (atlasNumber != currentAtlas)
            {
                objWriter.WriteLine($"usemtl block_atlas_{atlasNumber}");
                currentAtlas = atlasNumber;
            }

            Vec3i worldPos = meshWithPos.WorldPosition;
            Vec3f offset = new Vec3f(
                (worldPos - exportOrigin.AsVec3i).X,
                (worldPos - exportOrigin.AsVec3i).Y,
                (worldPos - exportOrigin.AsVec3i).Z);

            WriteVertices(objWriter, mesh, offset);
            WriteUVs(objWriter, mesh, false);
            WriteNormals(objWriter, mesh);
            WriteFaces(objWriter, mesh, vertexIndexBase);

            vertexIndexBase += mesh.VerticesCount;
        }

        objWriter.WriteLine();
        return vertexIndexBase;
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

    private void WriteUVs(StreamWriter writer, MeshData mesh, bool isTopSoil)
    {
        // For TopSoil pass, use grass overlay UVs from CustomShorts if available
        if (isTopSoil && mesh.CustomShorts?.Values != null && mesh.CustomShorts.Values.Length >= mesh.VerticesCount * 2)
        {
            // Log first few UV values to debug
            if (mesh.VerticesCount > 0)
            {
                float u0 = mesh.CustomShorts.Values[0] / 32768f;
                float v0 = mesh.CustomShorts.Values[1] / 32768f;
                WorldExporterModSystem.WorldExporterLog($"WriteUVs TopSoil: First UV from CustomShorts: ({u0:F6}, {v0:F6}), raw shorts: ({mesh.CustomShorts.Values[0]}, {mesh.CustomShorts.Values[1]})");
            }

            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                // CustomShorts stores uv2 as short values that need to be converted to floats
                // The shader unpacks them with: uv2 = uv2In * 2.0 - ...
                // For export, we'll just use the packed values directly as they should be in atlas coordinates
                float u = mesh.CustomShorts.Values[i * 2] / 32768f;  // Convert from short to normalized float
                float v = 1.0f - (mesh.CustomShorts.Values[i * 2 + 1] / 32768f);  // Flip V for OBJ (OpenGL uses bottom-left origin, OBJ uses top-left)
                writer.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
            }
        }
        // For other passes or if CustomShorts not available, use standard UVs
        else if (mesh.Uv != null && mesh.Uv.Length >= mesh.VerticesCount * 2)
        {
            // Log first few UV values to debug
            if (mesh.VerticesCount > 0 && !isTopSoil)
            {
                WorldExporterModSystem.WorldExporterLog($"WriteUVs Opaque: First UV from mesh.Uv: ({mesh.Uv[0]:F6}, {mesh.Uv[1]:F6})");
            }

            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float u = mesh.Uv[i * 2];
                float v = 1.0f - mesh.Uv[i * 2 + 1];  // Flip V for OBJ (OpenGL uses bottom-left origin, OBJ uses top-left)
                writer.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
            }
        }
        else
        {
            WorldExporterModSystem.WorldExporterLog($"WriteUVs: No UV data available, using default (0,0)");
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

    private void WriteMTLFile(string mtlPath, HashSet<int> usedAtlases)
    {
        using var mtlWriter = new StreamWriter(mtlPath);
        mtlWriter.WriteLine("# Material exported by vsworldexporter");
        mtlWriter.WriteLine();

        // Standard block atlas materials
        foreach (int atlasNum in usedAtlases.OrderBy(x => x))
        {
            mtlWriter.WriteLine($"newmtl block_atlas_{atlasNum}");
            mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");
            mtlWriter.WriteLine("d 1.0");
            mtlWriter.WriteLine("illum 2");
            mtlWriter.WriteLine($"map_Kd block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine($"map_d block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine();
        }

        // TopSoil materials (use first atlas for texture)
        if (usedAtlases.Count > 0)
        {
            int atlasNum = usedAtlases.OrderBy(x => x).First();

            // Soil material (opaque)
            mtlWriter.WriteLine("newmtl topsoil_soil");
            mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");
            mtlWriter.WriteLine("d 1.0");
            mtlWriter.WriteLine("illum 2");
            mtlWriter.WriteLine($"map_Kd block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine($"map_d block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine();

            // Grass material (transparent via alpha channel)
            mtlWriter.WriteLine("newmtl topsoil_grass");
            mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");
            mtlWriter.WriteLine("d 1.0");
            mtlWriter.WriteLine("illum 2");
            mtlWriter.WriteLine($"map_Kd block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine($"map_d block_atlas_{atlasNum}.png");
            mtlWriter.WriteLine();
        }
    }

    private void ExportTextureAtlas(string exportDir, ICoreClientAPI capi)
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

            string atlasPath = Path.Combine(exportDir, $"block_atlas_{i}.png");

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

    private int ExportTopSoilToWriter(
        StreamWriter objWriter,
        List<MeshDataWithPosition> meshList,
        BlockPos exportOrigin,
        string passName,
        int vertexIndexBase,
        HashSet<int> allUsedAtlases,
        ICoreClientAPI capi)
    {
        WorldExporterModSystem.WorldExporterLog("ExportTopSoilToWriter: Starting TopSoil dual-layer export");

        // Collect all faces
        var allFaces = new List<(TopSoilFace face, MeshDataWithPosition meshWithPos)>();

        foreach (var meshWithPos in meshList)
        {
            var faces = meshProcessor.ProcessMesh(meshWithPos.Mesh);
            foreach (var face in faces)
            {
                allFaces.Add((face, meshWithPos));
            }
            allUsedAtlases.Add(meshWithPos.AtlasNumber);
        }

        int nonBottomFaceCount = allFaces.Count(f => !f.face.IsBottomFace);
        WorldExporterModSystem.WorldExporterLog($"TopSoil: Exporting {allFaces.Count} faces as dual-layer objects");
        WorldExporterModSystem.WorldExporterLog($"  - {passName}_soil: {nonBottomFaceCount} faces");
        WorldExporterModSystem.WorldExporterLog($"  - {passName}_grass: {nonBottomFaceCount} faces");

        if (WorldExporterModSystem.TopSoilGrassLayerOffset < 0.001f)
        {
            WorldExporterModSystem.WorldExporterLog("Warning: TopSoilGrassLayerOffset is very small, z-fighting may occur");
        }

        // OBJECT 1: Soil base layer
        objWriter.WriteLine($"o {passName}_soil");
        objWriter.WriteLine("usemtl topsoil_soil");
        objWriter.WriteLine();

        foreach (var (face, meshWithPos) in allFaces)
        {
            if (face.IsBottomFace)
                continue; // Skip bottom faces (render in standard opaque pass)

            MeshData mesh = meshWithPos.Mesh;

            if (mesh.xyz == null || mesh.VerticesCount == 0 || mesh.Uv == null)
            {
                WorldExporterModSystem.WorldExporterLog($"ExportTopSoilRenderPass: Skipping face due to missing data");
                continue;
            }

            // Validate all bounds before writing anything to avoid orphaned OBJ entries
            bool boundsOk = true;
            foreach (int vertIdx in face.VertexIndices)
            {
                if (vertIdx * 3 + 2 >= mesh.xyz.Length || vertIdx * 2 + 1 >= mesh.Uv.Length)
                {
                    WorldExporterModSystem.WorldExporterLog($"ExportTopSoilRenderPass: index {vertIdx} out of bounds, skipping face");
                    boundsOk = false;
                    break;
                }
            }
            if (!boundsOk)
                continue;

            Vec3i worldPos = meshWithPos.WorldPosition;
            Vec3f chunkOffset = new Vec3f(
                (worldPos - exportOrigin.AsVec3i).X,
                (worldPos - exportOrigin.AsVec3i).Y,
                (worldPos - exportOrigin.AsVec3i).Z
            );

            // Write 3 vertices (NO offset)
            foreach (int vertIdx in face.VertexIndices)
            {
                float x = mesh.xyz[vertIdx * 3] + chunkOffset.X;
                float y = mesh.xyz[vertIdx * 3 + 1] + chunkOffset.Y;
                float z = mesh.xyz[vertIdx * 3 + 2] + chunkOffset.Z;
                objWriter.WriteLine(FormattableString.Invariant($"v {x:F6} {y:F6} {z:F6}"));
            }

            // Write 3 UVs (primary UV from mesh.Uv - soil texture)
            foreach (int vertIdx in face.VertexIndices)
            {
                float u = mesh.Uv[vertIdx * 2];
                float v = 1.0f - mesh.Uv[vertIdx * 2 + 1]; // Flip V for OBJ
                objWriter.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
            }

            // Write 3 normals
            foreach (int vertIdx in face.VertexIndices)
            {
                Vec3f normal = meshProcessor.GetVertexNormal(mesh, vertIdx);
                objWriter.WriteLine(FormattableString.Invariant($"vn {normal.X:F6} {normal.Y:F6} {normal.Z:F6}"));
            }

            // Write face (v/vt/vn format)
            int v0 = vertexIndexBase;
            int v1 = vertexIndexBase + 1;
            int v2 = vertexIndexBase + 2;
            objWriter.WriteLine($"f {v0}/{v0}/{v0} {v1}/{v1}/{v1} {v2}/{v2}/{v2}");

            vertexIndexBase += 3;
        }

        // OBJECT 2: Grass overlay layer
        objWriter.WriteLine();
        objWriter.WriteLine($"o {passName}_grass");
        objWriter.WriteLine("usemtl topsoil_grass");
        objWriter.WriteLine();

        foreach (var (face, meshWithPos) in allFaces)
        {
            if (face.IsBottomFace)
                continue; // Skip bottom faces

            MeshData mesh = meshWithPos.Mesh;

            if (mesh.xyz == null || mesh.VerticesCount == 0 || mesh.Uv == null)
                continue;

            if (mesh.CustomShorts?.Values == null || mesh.CustomShorts.Values.Length == 0)
            {
                WorldExporterModSystem.WorldExporterLog("Warning: No CustomShorts UV2 data, skipping grass layer for this mesh");
                vertexIndexBase += 3; // Still increment to keep indices aligned
                continue;
            }

            // Validate all bounds before writing anything to avoid orphaned OBJ entries
            bool boundsOk = true;
            foreach (int vertIdx in face.VertexIndices)
            {
                if (vertIdx * 3 + 2 >= mesh.xyz.Length || vertIdx * 2 + 1 >= mesh.CustomShorts.Values.Length)
                {
                    boundsOk = false;
                    break;
                }
            }
            if (!boundsOk)
            {
                vertexIndexBase += 3; // Still increment to keep indices aligned
                continue;
            }

            Vec3i worldPos = meshWithPos.WorldPosition;
            Vec3f chunkOffset = new Vec3f(
                (worldPos - exportOrigin.AsVec3i).X,
                (worldPos - exportOrigin.AsVec3i).Y,
                (worldPos - exportOrigin.AsVec3i).Z
            );

            // Get face normal for offset calculation
            Vec3f faceNormal = meshProcessor.GetVertexNormal(mesh, face.VertexIndices[0]);
            Vec3f offset = faceNormal * WorldExporterModSystem.TopSoilGrassLayerOffset;

            // Write 3 vertices (WITH offset to prevent z-fighting)
            foreach (int vertIdx in face.VertexIndices)
            {
                float x = mesh.xyz[vertIdx * 3] + chunkOffset.X + offset.X;
                float y = mesh.xyz[vertIdx * 3 + 1] + chunkOffset.Y + offset.Y;
                float z = mesh.xyz[vertIdx * 3 + 2] + chunkOffset.Z + offset.Z;
                objWriter.WriteLine(FormattableString.Invariant($"v {x:F6} {y:F6} {z:F6}"));
            }

            // Write 3 UVs (decoded UV2 from CustomShorts - grass texture)
            foreach (int vertIdx in face.VertexIndices)
            {
                var (u, v) = meshProcessor.DecodeUV2(mesh, vertIdx, face.NormalY);
                v = 1.0f - v; // Flip V for OBJ
                objWriter.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
            }

            // Write 3 normals (same as soil layer)
            foreach (int vertIdx in face.VertexIndices)
            {
                Vec3f normal = meshProcessor.GetVertexNormal(mesh, vertIdx);
                objWriter.WriteLine(FormattableString.Invariant($"vn {normal.X:F6} {normal.Y:F6} {normal.Z:F6}"));
            }

            // Write face
            int v0 = vertexIndexBase;
            int v1 = vertexIndexBase + 1;
            int v2 = vertexIndexBase + 2;
            objWriter.WriteLine($"f {v0}/{v0}/{v0} {v1}/{v1}/{v1} {v2}/{v2}/{v2}");

            vertexIndexBase += 3;
        }

        objWriter.WriteLine();
        WorldExporterModSystem.WorldExporterLog("ExportTopSoilToWriter: Export complete");
        return vertexIndexBase;
    }

    private float RemapUV(float atlasUV, TextureAtlasPosition texPos, bool isU)
    {
        float min = isU ? texPos.x1 : texPos.y1;
        float max = isU ? texPos.x2 : texPos.y2;
        float range = max - min;
        if (range == 0) return 0;
        return (atlasUV - min) / range;
    }
}
