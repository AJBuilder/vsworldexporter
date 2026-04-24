using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldExporter;

public struct TopSoilFace
{
    public int[] VertexIndices;
    public int SoilTextureId;
    public int GrassTextureId;
    public bool IsTopFace;
    public bool IsBottomFace;
    public float NormalY;
}

public class TopSoilMeshProcessor
{
    private readonly ICoreClientAPI capi;
    private readonly float blockTextureSizeX;
    private readonly float blockTextureSizeY;
    private readonly float subpixelPaddingX;
    private readonly float subpixelPaddingY;

    public TopSoilMeshProcessor(ICoreClientAPI capi)
    {
        this.capi = capi;

        var atlas = capi.BlockTextureAtlas;
        int texturePixelSize = capi.Render.TextureSize;
        this.blockTextureSizeX = (float)texturePixelSize / atlas.Size.Width;
        this.blockTextureSizeY = (float)texturePixelSize / atlas.Size.Height;
        this.subpixelPaddingX = atlas.SubPixelPaddingX;
        this.subpixelPaddingY = atlas.SubPixelPaddingY;
    }

    public List<TopSoilFace> ProcessMesh(MeshData mesh)
    {
        var faces = new List<TopSoilFace>();

        if (mesh == null || mesh.IndicesCount == 0)
        {
            WorldExporterModSystem.WorldExporterLog("ProcessMesh: empty mesh");
            return faces;
        }

        WorldExporterModSystem.WorldExporterLog($"ProcessMesh: processing {mesh.IndicesCount / 3} triangles, {mesh.VerticesCount} vertices");
        WorldExporterModSystem.WorldExporterLog($"ProcessMesh: TextureIds={mesh.TextureIds?.Length ?? 0}, TextureIndices={mesh.TextureIndicesCount}");

        for (int i = 0; i < mesh.IndicesCount; i += 3)
        {
            int vi0 = (int)mesh.Indices[i];
            int vi1 = (int)mesh.Indices[i + 1];
            int vi2 = (int)mesh.Indices[i + 2];

            float normalY = GetVertexNormalY(mesh, vi0);

            int faceTextureIndex = i / 12;
            if (faceTextureIndex >= mesh.TextureIndicesCount)
            {
                faceTextureIndex = mesh.TextureIndicesCount > 0 ? mesh.TextureIndicesCount - 1 : 0;
            }

            int soilTextureId = 0;
            int grassTextureId = 0;

            if (mesh.TextureIds != null && mesh.TextureIndices != null && faceTextureIndex < mesh.TextureIndicesCount)
            {
                byte textureIndexRef = mesh.TextureIndices[faceTextureIndex];
                if (textureIndexRef < mesh.TextureIds.Length)
                {
                    soilTextureId = mesh.TextureIds[textureIndexRef];
                }
            }

            grassTextureId = soilTextureId;

            var face = new TopSoilFace
            {
                VertexIndices = new[] { vi0, vi1, vi2 },
                SoilTextureId = soilTextureId,
                GrassTextureId = grassTextureId,
                IsTopFace = IsTopFace(normalY),
                IsBottomFace = IsBottomFace(normalY),
                NormalY = normalY
            };

            faces.Add(face);
        }

        WorldExporterModSystem.WorldExporterLog($"ProcessMesh: generated {faces.Count} faces");
        return faces;
    }

    public (float u, float v) DecodeUV2(MeshData mesh, int vertexIndex, float normalY)
    {
        if (mesh.CustomShorts?.Values == null || vertexIndex * 2 + 1 >= mesh.CustomShorts.Values.Length)
        {
            WorldExporterModSystem.WorldExporterLog($"DecodeUV2: no CustomShorts data for vertex {vertexIndex}");
            return (0, 0);
        }

        short packedU = mesh.CustomShorts.Values[vertexIndex * 2];
        short packedV = mesh.CustomShorts.Values[vertexIndex * 2 + 1];

        // Treat as unsigned shorts (0-65535) - convert from signed
        ushort unsignedU = (ushort)packedU;
        ushort unsignedV = (ushort)packedV;

        // Extract LSB flags (matches shader: int(uv2In.x * 0x10000) & 1)
        bool isU2 = (unsignedU & 1) == 1;
        bool isV2 = (unsignedV & 1) == 1;

        // Normalize to 0-1 range (as GPU does)
        float uNormalized = unsignedU / 65536.0f;
        float vNormalized = unsignedV / 65536.0f;

        // Apply shader transformation: uv2In * 2.0 - padding
        float uvEpsilon = 1.0f / 32768.0f;
        float u = uNormalized * 2.0f - (isU2 ? (uvEpsilon + subpixelPaddingX * 2.0f) : 0);
        float v = vNormalized * 2.0f - (isV2 ? (uvEpsilon + subpixelPaddingY * 2.0f) : 0);

        // Apply offset for top vs side faces (matches fragment shader: uv2 + vec2(blockTextureSize.x * normal.y, 0))
        bool isTopFace = normalY > 0.5f;
        if (isTopFace)
        {
            u += blockTextureSizeX;
        }

        return (u, v);
    }

    private float GetVertexNormalY(MeshData mesh, int vertexIndex)
    {
        if (mesh.Flags != null && vertexIndex < mesh.Flags.Length)
        {
            float[] normal = new float[3];
            VertexFlags.UnpackNormal(mesh.Flags[vertexIndex], normal);
            return normal[1]; // Y component
        }

        return 0;
    }

    public Vec3f GetVertexNormal(MeshData mesh, int vertexIndex)
    {
        if (mesh.Flags != null && vertexIndex < mesh.Flags.Length)
        {
            float[] normal = new float[3];
            VertexFlags.UnpackNormal(mesh.Flags[vertexIndex], normal);
            return new Vec3f(normal[0], normal[1], normal[2]);
        }

        return new Vec3f(0, 1, 0); // Default to up
    }

    private bool IsTopFace(float normalY)
    {
        return normalY > 0.5f;
    }

    private bool IsBottomFace(float normalY)
    {
        return normalY < -0.5f;
    }
}
