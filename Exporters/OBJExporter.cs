using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        var allUsedTextureSubIds = new HashSet<int>();

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

            var passUsedTextureSubIds = ExportRenderPassToOBJ(
                meshList,
                exportOrigin,
                passObjPath,
                mtlPath,
                mtlFilename,
                capi);

            allUsedTextureSubIds.UnionWith(passUsedTextureSubIds);
        }

        ExportTextures(allUsedTextureSubIds, texturesDir, capi);

        WorldExporterModSystem.WorldExporterChat($"OBJ export complete: {meshData.Count} render passes, {allUsedTextureSubIds.Count} textures");
    }

    private HashSet<int> ExportRenderPassToOBJ(
        List<MeshDataWithPosition> meshList,
        BlockPos exportOrigin,
        string objPath,
        string mtlPath,
        string mtlFilename,
        ICoreClientAPI capi)
    {
        var usedTextureSubIds = new HashSet<int>();

        using var objWriter = new StreamWriter(objPath);
        objWriter.WriteLine("# Exported by vsworldexporter");
        objWriter.WriteLine($"mtllib {mtlFilename}");
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
            WriteUVs(objWriter, mesh, usedTextureSubIds, capi);
            WriteNormals(objWriter, mesh);
            WriteFaces(objWriter, mesh, vertexIndexBase, capi);

            vertexIndexBase += mesh.VerticesCount;
        }

        WriteMTLFile(mtlPath, usedTextureSubIds);

        return usedTextureSubIds;
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

    private void WriteUVs(StreamWriter writer, MeshData mesh, HashSet<int> usedTextures, ICoreClientAPI capi)
    {
        bool hasUvs = mesh.Uv != null && mesh.TextureIds != null && mesh.TextureIndices != null;

        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            if (!hasUvs)
            {
                writer.WriteLine("vt 0 0");
                continue;
            }

            int faceIdx = i / mesh.VerticesPerFace;
            if (faceIdx >= mesh.TextureIndices.Length)
            {
                writer.WriteLine("vt 0 0");
                continue;
            }

            byte texIndex = mesh.TextureIndices[faceIdx];
            if (texIndex >= mesh.TextureIds.Length)
            {
                writer.WriteLine("vt 0 0");
                continue;
            }

            int textureSubId = mesh.TextureIds[texIndex];

            float uvX = mesh.Uv[i * 2];
            float uvY = mesh.Uv[i * 2 + 1];

            if (textureSubId < capi.BlockTextureAtlas.Positions.Length)
            {
                var texPos = capi.BlockTextureAtlas.Positions[textureSubId];
                if (texPos != null)
                {
                    float atlasW = texPos.x2 - texPos.x1;
                    float atlasH = texPos.y2 - texPos.y1;
                    float u = atlasW > 0f ? (uvX - texPos.x1) / atlasW : 0f;
                    float v = atlasH > 0f ? 1f - (uvY - texPos.y1) / atlasH : 0f;

                    writer.WriteLine(FormattableString.Invariant($"vt {u:F6} {v:F6}"));
                    usedTextures.Add(textureSubId);
                    continue;
                }
            }

            writer.WriteLine("vt 0 0");
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

    private void WriteFaces(StreamWriter writer, MeshData mesh, int indexBase, ICoreClientAPI capi)
    {
        var materialGroups = new Dictionary<int, List<(int, int, int)>>();

        bool hasTextures = mesh.TextureIds != null && mesh.TextureIndices != null;

        for (int i = 0; i < mesh.IndicesCount; i += 3)
        {
            int vi0 = (int)mesh.Indices[i];
            int vi1 = (int)mesh.Indices[i + 1];
            int vi2 = (int)mesh.Indices[i + 2];

            int textureSubId = -1;
            if (hasTextures)
            {
                int faceIdx = vi0 / mesh.VerticesPerFace;
                if (faceIdx < mesh.TextureIndices.Length)
                {
                    byte texIndex = mesh.TextureIndices[faceIdx];
                    if (texIndex < mesh.TextureIds.Length)
                    {
                        textureSubId = mesh.TextureIds[texIndex];
                    }
                }
            }

            if (!materialGroups.ContainsKey(textureSubId))
                materialGroups[textureSubId] = new List<(int, int, int)>();

            materialGroups[textureSubId].Add((vi0, vi1, vi2));
        }

        foreach (var kvp in materialGroups)
        {
            int textureSubId = kvp.Key;
            if (textureSubId >= 0)
                writer.WriteLine($"usemtl mat_{textureSubId}");

            foreach (var (vi0, vi1, vi2) in kvp.Value)
            {
                int v0 = indexBase + vi0;
                int v1 = indexBase + vi1;
                int v2 = indexBase + vi2;
                writer.WriteLine($"f {v0}/{v0}/{v0} {v1}/{v1}/{v1} {v2}/{v2}/{v2}");
            }
        }
    }

    private void WriteMTLFile(string mtlPath, HashSet<int> textureSubIds)
    {
        using var mtlWriter = new StreamWriter(mtlPath);
        mtlWriter.WriteLine("# Materials exported by vsworldexporter");
        mtlWriter.WriteLine();

        foreach (int subId in textureSubIds)
        {
            mtlWriter.WriteLine($"newmtl mat_{subId}");
            mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");
            mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");
            mtlWriter.WriteLine("d 1.0");
            mtlWriter.WriteLine("illum 2");
            mtlWriter.WriteLine($"map_Kd textures/tex_{subId}.png");
            mtlWriter.WriteLine();
        }
    }

    private void ExportTextures(HashSet<int> textureSubIds, string texturesDir, ICoreClientAPI capi)
    {
        var subIdToAssetLoc = new Dictionary<int, AssetLocation>();
        int texturesExported = 0;

        foreach (int subId in textureSubIds)
        {
            AssetLocation loc = GetAssetLocationForSubId(subId, subIdToAssetLoc, capi);
            string texFilename = $"tex_{subId}.png";

            if (loc != null)
            {
                string sanitised = (loc.Domain + "_" + loc.Path).Replace('/', '_').Replace('\\', '_') + ".png";
                texFilename = sanitised;

                IAsset texAsset = capi.Assets.TryGet(loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
                if (texAsset != null)
                {
                    File.WriteAllBytes(Path.Combine(texturesDir, sanitised), texAsset.Data);
                    texturesExported++;
                }
                else
                {
                    WorldExporterModSystem.WorldExporterLog($"Warning: Could not load texture asset for {loc}");
                }
            }
            else
            {
                WorldExporterModSystem.WorldExporterLog($"Warning: No asset location found for texture subId {subId}");
            }
        }

        WorldExporterModSystem.WorldExporterChat($"Exported {texturesExported} textures");
    }

    private AssetLocation GetAssetLocationForSubId(int subId, Dictionary<int, AssetLocation> cache, ICoreClientAPI capi)
    {
        if (cache.TryGetValue(subId, out var cached))
            return cached;

        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Textures == null) continue;
            foreach (var kvp in block.Textures)
            {
                var baked = kvp.Value?.Baked;
                if (baked?.TextureSubId == subId)
                {
                    cache[subId] = baked.BakedName;
                    return baked.BakedName;
                }

                if (baked?.BakedVariants != null)
                {
                    foreach (var variant in baked.BakedVariants)
                    {
                        if (variant.TextureSubId == subId)
                        {
                            cache[subId] = variant.BakedName;
                            return variant.BakedName;
                        }
                    }
                }

                if (baked?.BakedTiles != null)
                {
                    foreach (var tile in baked.BakedTiles)
                    {
                        if (tile.TextureSubId == subId)
                        {
                            cache[subId] = tile.BakedName;
                            return tile.BakedName;
                        }
                    }
                }
            }
        }
        return null;
    }
}
