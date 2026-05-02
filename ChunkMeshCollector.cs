using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace WorldExporter;

public struct MeshDataWithPosition
{
    public MeshData Mesh;
    public Vec3i WorldPosition;
    public int AtlasNumber;
}

public class ChunkMeshCollector
{
    private readonly ICoreClientAPI capi;
    private readonly IClientWorldAccessor world;

    public ChunkMeshCollector(ICoreClientAPI api, IClientWorldAccessor worldAccessor)
    {
        capi = api;
        world = worldAccessor;
    }

    public Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> CollectChunksInRegion(
        BlockPos regionStart,
        BlockPos regionEnd,
        RenderPassConfig passConfig)
    {
        var result = new Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>>();

        Vec3i chunkStart = RoundToChunkCoordinate(regionStart);
        Vec3i chunkEnd = RoundUpToChunkCoordinate(regionEnd);

        WorldExporterModSystem.WorldExporterLog($"Chunk range: ({chunkStart.X}, {chunkStart.Y}, {chunkStart.Z}) to ({chunkEnd.X}, {chunkEnd.Y}, {chunkEnd.Z})");

        var tesselator = GetChunkTesselator();
        if (tesselator == null)
        {
            WorldExporterModSystem.WorldExporterLog("Failed to access ChunkTesselator");
            return result;
        }

        for (int cy = chunkStart.Y; cy <= chunkEnd.Y; cy++)
        {
            for (int cz = chunkStart.Z; cz <= chunkEnd.Z; cz++)
            {
                for (int cx = chunkStart.X; cx <= chunkEnd.X; cx++)
                {
                    ProcessChunk(cx, cy, cz, tesselator, result, passConfig);
                }
            }
        }

        return result;
    }

    private void ProcessChunk(
        int chunkX,
        int chunkY,
        int chunkZ,
        ChunkTesselator tesselator,
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> output,
        RenderPassConfig passConfig)
    {
        WorldExporterModSystem.WorldExporterLog($"ProcessChunk attempting chunk ({chunkX}, {chunkY}, {chunkZ})");

        var chunk = world.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ);
        WorldExporterModSystem.WorldExporterLog($"  GetChunk returned: {chunk != null}, empty: {chunk?.Empty}");

        if (chunk == null || chunk.Empty)
        {
            WorldExporterModSystem.WorldExporterLog($"  Chunk ({chunkX}, {chunkY}, {chunkZ}) not loaded or empty - skipping");
            return;
        }

        // Unpack the chunk data like the game does
        var clientChunk = (ClientChunk)chunk;
        try
        {
            clientChunk.Unpack_ReadOnly();
        }
        catch (Exception e)
        {
            WorldExporterModSystem.WorldExporterLog($"Failed to unpack chunk ({chunkX}, {chunkY}, {chunkZ}): {e.Message}");
            return;
        }

        // Check that all neighboring chunks are loaded (ChunkTesselator needs them for extended chunk data)
        // Note: For Y=0 chunks, Y=-1 neighbors will be EmptyChunk, which is fine

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    // Skip checking Y=-1 neighbors for chunks at world bottom
                    if (chunkY == 0 && dy == -1)
                        continue;

                    var neighborChunk = world.BlockAccessor.GetChunk(chunkX + dx, chunkY + dy, chunkZ + dz);
                    if (neighborChunk == null)
                    {
                        WorldExporterModSystem.WorldExporterLog($"Chunk ({chunkX}, {chunkY}, {chunkZ}) neighbor ({chunkX + dx}, {chunkY + dy}, {chunkZ + dz}) not loaded - skipping");
                        return;
                    }
                }
            }
        }

        var tessChunk = Activator.CreateInstance(typeof(TesselatedChunk));

        var tessChunkType = typeof(TesselatedChunk);
        tessChunkType.GetField("chunk", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tessChunk, (ClientChunk)chunk);
        tessChunkType.GetField("CullVisible", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tessChunk, ((ClientChunk)chunk).CullVisible);
        tessChunkType.GetField("positionX", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tessChunk, chunkX * 32);
        tessChunkType.GetField("positionYAndDimension", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tessChunk, chunkY * 32);
        tessChunkType.GetField("positionZ", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tessChunk, chunkZ * 32);

        try
        {
            lock (tesselator.ReloadLock)
            {
                // Validate ChunkTesselator state
                var tesselatorType = tesselator.GetType();

                var startedField = tesselatorType.GetField("started", BindingFlags.Instance | BindingFlags.NonPublic);
                bool started = (bool)(startedField?.GetValue(tesselator) ?? false);
                if (!started)
                {
                    WorldExporterModSystem.WorldExporterLog($"ChunkTesselator not started - skipping chunk ({chunkX}, {chunkY}, {chunkZ})");
                    return;
                }

                var blocksFastField = tesselatorType.GetField("blocksFast", BindingFlags.Instance | BindingFlags.NonPublic);
                var blocksFast = blocksFastField?.GetValue(tesselator);
                if (blocksFast == null)
                {
                    WorldExporterModSystem.WorldExporterLog($"ChunkTesselator.blocksFast is null - skipping chunk ({chunkX}, {chunkY}, {chunkZ})");
                    return;
                }

                var lightConverterField = tesselatorType.GetField("lightConverter", BindingFlags.Instance | BindingFlags.NonPublic);
                var lightConverter = lightConverterField?.GetValue(tesselator);
                if (lightConverter == null)
                {
                    WorldExporterModSystem.WorldExporterLog($"ChunkTesselator.lightConverter is null - skipping chunk ({chunkX}, {chunkY}, {chunkZ})");
                    return;
                }

                // Set vars fields that ChunkTesselator expects
                var vars = tesselatorType.GetField("vars", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tesselator);
                if (vars != null)
                {
                    var varsType = vars.GetType();

                    // Set blockEntitiesOfChunk
                    var blockEntitiesField = varsType.GetField("blockEntitiesOfChunk", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (blockEntitiesField != null)
                    {
                        blockEntitiesField.SetValue(vars, ((ClientChunk)chunk).BlockEntities);
                    }

                    // Set rainHeightMap
                    var rainHeightMapField = varsType.GetField("rainHeightMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (rainHeightMapField != null)
                    {
                        var mapChunk = ((ClientChunk)chunk).MapChunk;
                        var rainHeightMap = mapChunk?.RainHeightMap ?? CreateDummyHeightMap();
                        rainHeightMapField.SetValue(vars, rainHeightMap);
                    }
                }

                // Debug: Check chunksNearby array before calling NowProcessChunk
                var chunksNearbyField = tesselatorType.GetField("chunksNearby", BindingFlags.Instance | BindingFlags.NonPublic);
                var chunksNearby = chunksNearbyField?.GetValue(tesselator) as Array;
                if (chunksNearby != null)
                {
                    int nullCount = 0;
                    for (int i = 0; i < chunksNearby.Length; i++)
                    {
                        if (chunksNearby.GetValue(i) == null)
                            nullCount++;
                    }
                    if (nullCount > 0)
                    {
                        WorldExporterModSystem.WorldExporterLog($"WARNING: chunksNearby has {nullCount} null entries before NowProcessChunk for chunk ({chunkX}, {chunkY}, {chunkZ})");
                    }
                }

                WorldExporterModSystem.WorldExporterLog($"Calling NowProcessChunk for chunk ({chunkX}, {chunkY}, {chunkZ})");

                int vertexCount = tesselator.NowProcessChunk(chunkX, chunkY, chunkZ, (TesselatedChunk)tessChunk, skipChunkCenter: false);
                WorldExporterModSystem.WorldExporterLog($"NowProcessChunk completed for chunk ({chunkX}, {chunkY}, {chunkZ}), returned {vertexCount} vertices");

                // Access tessChunk.centerParts and tessChunk.edgeParts which should be populated by NowProcessChunk
                Vec3i worldPos = new Vec3i(chunkX * 32, chunkY * 32, chunkZ * 32);

                var centerPartsField = tessChunkType.GetField("centerParts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var edgePartsField = tessChunkType.GetField("edgeParts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                var centerParts = centerPartsField?.GetValue(tessChunk) as Array;
                var edgeParts = edgePartsField?.GetValue(tessChunk) as Array;

                WorldExporterModSystem.WorldExporterLog($"  centerParts: {centerParts?.Length ?? 0} parts, edgeParts: {edgeParts?.Length ?? 0} parts");

                if (centerParts != null)
                {
                    WorldExporterModSystem.WorldExporterLog($"    ProcessChunkParts: processing {centerParts.Length} center parts");
                    ProcessChunkParts(centerParts, worldPos, output, passConfig);
                }

                if (edgeParts != null)
                {
                    WorldExporterModSystem.WorldExporterLog($"    ProcessChunkParts: processing {edgeParts.Length} edge parts");
                    ProcessChunkParts(edgeParts, worldPos, output, passConfig);
                }
            }
        }
        catch (Exception e)
        {
            WorldExporterModSystem.WorldExporterLog($"Error processing chunk ({chunkX}, {chunkY}, {chunkZ}): {e.Message}");
        }
    }

    private void ProcessChunkParts(
        Array parts,
        Vec3i worldPos,
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> output,
        RenderPassConfig passConfig)
    {
        if (parts == null)
        {
            WorldExporterModSystem.WorldExporterLog("    ProcessChunkParts: parts is null");
            return;
        }

        WorldExporterModSystem.WorldExporterLog($"    ProcessChunkParts: processing {parts.Length} parts");

        var partType = typeof(TesselatedChunkPart);
        var passField = partType.GetField("pass", BindingFlags.Instance | BindingFlags.NonPublic);
        var meshLod0Field = partType.GetField("modelDataLod0", BindingFlags.Instance | BindingFlags.NonPublic);
        var meshLod1Field = partType.GetField("modelDataLod1", BindingFlags.Instance | BindingFlags.NonPublic);
        var meshLod2NearField = partType.GetField("modelDataNotLod2Far", BindingFlags.Instance | BindingFlags.NonPublic);
        var meshLod2FarField = partType.GetField("modelDataLod2Far", BindingFlags.Instance | BindingFlags.NonPublic);
        var atlasField = partType.GetField("atlasNumber", BindingFlags.Instance | BindingFlags.NonPublic);

        if (passField == null) WorldExporterModSystem.WorldExporterLog("    ERROR: passField is null!");
        if (meshLod0Field == null) WorldExporterModSystem.WorldExporterLog("    ERROR: meshLod0Field is null!");
        if (atlasField == null) WorldExporterModSystem.WorldExporterLog("    ERROR: atlasField is null!");

        int partIndex = 0;
        foreach (var part in parts)
        {
            if (part == null)
            {
                partIndex++;
                continue;
            }

            WorldExporterModSystem.WorldExporterLog($"      Part {partIndex}: part type = {part.GetType().Name}");

            var pass = (EnumChunkRenderPass)passField.GetValue(part);
            WorldExporterModSystem.WorldExporterLog($"      Part {partIndex}: pass={pass}");

            if (!passConfig.ShouldExport(pass))
            {
                WorldExporterModSystem.WorldExporterLog($"      Part {partIndex}: skipping (pass not configured for export)");
                partIndex++;
                continue;
            }

            // Check all LOD levels to see which one has mesh data
            var meshLod0 = meshLod0Field.GetValue(part) as MeshData;
            var meshLod1 = meshLod1Field.GetValue(part) as MeshData;
            var meshLod2Near = meshLod2NearField.GetValue(part) as MeshData;
            var meshLod2Far = meshLod2FarField.GetValue(part) as MeshData;

            WorldExporterModSystem.WorldExporterLog($"      Part {partIndex}: LOD0 vertices={meshLod0?.VerticesCount ?? 0}, LOD1 vertices={meshLod1?.VerticesCount ?? 0}, LOD2Near vertices={meshLod2Near?.VerticesCount ?? 0}, LOD2Far vertices={meshLod2Far?.VerticesCount ?? 0}");

            // LOD0 (close-up detail) and LOD1 (everywhere blocks) contain different blocks,
            // so add both as separate list entries — no allocation, no copying, no data loss.
            // Skip LOD2/3 as they are simplified distant versions.
            bool hasAnyMesh = (meshLod0 != null && meshLod0.VerticesCount > 0) || (meshLod1 != null && meshLod1.VerticesCount > 0);
            if (!hasAnyMesh)
            {
                WorldExporterModSystem.WorldExporterLog($"      Part {partIndex}: No mesh data at any LOD level");
                partIndex++;
                continue;
            }

            if (!output.ContainsKey(pass))
                output[pass] = new List<MeshDataWithPosition>();

            int atlasNumber = (int)atlasField.GetValue(part);

            if (meshLod0 != null && meshLod0.VerticesCount > 0)
                output[pass].Add(new MeshDataWithPosition { Mesh = meshLod0.Clone(), WorldPosition = worldPos, AtlasNumber = atlasNumber });
            if (meshLod1 != null && meshLod1.VerticesCount > 0)
                output[pass].Add(new MeshDataWithPosition { Mesh = meshLod1.Clone(), WorldPosition = worldPos, AtlasNumber = atlasNumber });

            partIndex++;
        }
    }

    private ChunkTesselator GetChunkTesselator()
    {
        try
        {
            var clientMain = capi.World as ClientMain;
            if (clientMain?.TerrainChunkTesselator == null)
            {
                WorldExporterModSystem.WorldExporterLog("Cannot access ChunkTesselator via ClientMain");
                return null;
            }

            return clientMain.TerrainChunkTesselator;
        }
        catch (Exception e)
        {
            WorldExporterModSystem.WorldExporterLog($"Error accessing ChunkTesselator: {e.Message}");
            return null;
        }
    }

    private Vec3i RoundToChunkCoordinate(BlockPos pos)
    {
        return new Vec3i(pos.X / 32, pos.Y / 32, pos.Z / 32);
    }

    private Vec3i RoundUpToChunkCoordinate(BlockPos pos)
    {
        return new Vec3i((pos.X + 31) / 32, (pos.Y + 31) / 32, (pos.Z + 31) / 32);
    }

    public BlockPos RoundToChunkBoundary(BlockPos pos)
    {
        int cx = (pos.X / 32) * 32;
        int cy = (pos.Y / 32) * 32;
        int cz = (pos.Z / 32) * 32;
        return new BlockPos(cx, cy, cz, pos.dimension);
    }

    public BlockPos RoundUpToChunkBoundary(BlockPos pos)
    {
        int cx = ((pos.X + 31) / 32) * 32;
        int cy = ((pos.Y + 31) / 32) * 32;
        int cz = ((pos.Z + 31) / 32) * 32;
        return new BlockPos(cx, cy, cz, pos.dimension);
    }

    private ushort[] CreateDummyHeightMap()
    {
        int mapChunkSize = world.BlockAccessor.MapSizeX / world.BlockAccessor.RegionSize;
        ushort[] heightMap = new ushort[mapChunkSize * mapChunkSize];
        ushort maxHeight = (ushort)(world.BlockAccessor.MapSizeY - 1);
        for (int i = 0; i < heightMap.Length; i++)
        {
            heightMap[i] = maxHeight;
        }
        return heightMap;
    }
}
