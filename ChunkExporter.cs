using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using WorldExporter.Exporters;

namespace WorldExporter;

public class ChunkExporter
{
    private readonly ICoreClientAPI capi;
    private readonly IClientWorldAccessor world;

    public ChunkExporter(ICoreClientAPI api, IClientWorldAccessor worldAccessor)
    {
        capi = api;
        world = worldAccessor;
    }

    public void ExportRegionToChunks(
        BlockPos regionStart,
        BlockPos regionEnd,
        string format,
        string outputPath,
        RenderPassConfig passConfig = null)
    {
        passConfig ??= RenderPassConfig.All();

        WorldExporterModSystem.WorldExporterChat("Collecting chunk mesh data...");

        var collector = new ChunkMeshCollector(capi, world);
        var meshData = collector.CollectChunksInRegion(regionStart, regionEnd, passConfig);

        int totalMeshes = meshData.Values.Sum(list => list.Count);
        WorldExporterModSystem.WorldExporterChat($"Collected {totalMeshes} meshes across {meshData.Count} render passes");

        if (totalMeshes == 0)
        {
            WorldExporterModSystem.WorldExporterChat("No meshes to export!");
            return;
        }

        WorldExporterModSystem.WorldExporterChat($"Exporting to {format.ToUpper()}...");

        IFormatExporter exporter = format.ToLower() switch
        {
            "stl" => new STLExporter(),
            "obj" => new OBJExporter(),
            _ => throw new ArgumentException($"Unknown format: {format}")
        };

        exporter.Export(meshData, regionStart, outputPath, capi);
    }
}
