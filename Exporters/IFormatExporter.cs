using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldExporter.Exporters;

public interface IFormatExporter
{
    void Export(
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> meshData,
        BlockPos exportOrigin,
        string outputPath,
        ICoreClientAPI capi);
}
