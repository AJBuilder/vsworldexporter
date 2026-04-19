using Vintagestory.API.Client;

namespace WorldExporter;

public class RenderPassConfig
{
    public bool ExportOpaque = true;
    public bool ExportOpaqueNoCull = true;
    public bool ExportBlendNoCull = true;
    public bool ExportTransparent = true;
    public bool ExportLiquid = true;
    public bool ExportTopSoil = true;
    public bool ExportMeta = false;
    public bool ExportOpaqueWaterPlant = true;

    public static RenderPassConfig All()
    {
        return new RenderPassConfig();
    }

    public static RenderPassConfig OpaqueOnly()
    {
        return new RenderPassConfig
        {
            ExportOpaque = true,
            ExportOpaqueNoCull = false,
            ExportBlendNoCull = false,
            ExportTransparent = false,
            ExportLiquid = false,
            ExportTopSoil = false,
            ExportMeta = false,
            ExportOpaqueWaterPlant = false
        };
    }

    public bool ShouldExport(EnumChunkRenderPass pass)
    {
        return pass switch
        {
            EnumChunkRenderPass.Opaque => ExportOpaque,
            EnumChunkRenderPass.OpaqueNoCull => ExportOpaqueNoCull,
            EnumChunkRenderPass.BlendNoCull => ExportBlendNoCull,
            EnumChunkRenderPass.Transparent => ExportTransparent,
            EnumChunkRenderPass.Liquid => ExportLiquid,
            EnumChunkRenderPass.TopSoil => ExportTopSoil,
            EnumChunkRenderPass.Meta => ExportMeta,
            EnumChunkRenderPass.OpaqueWaterPlant => ExportOpaqueWaterPlant,
            _ => false
        };
    }
}
