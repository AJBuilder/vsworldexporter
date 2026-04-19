using System;
using System.Collections.Generic;
using System.IO;
using QuantumConcepts.Formats.StereoLithography;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldExporter.Exporters;

public class STLExporter : IFormatExporter
{
    public void Export(
        Dictionary<EnumChunkRenderPass, List<MeshDataWithPosition>> meshData,
        BlockPos exportOrigin,
        string outputPath,
        ICoreClientAPI capi)
    {
        WorldExporterModSystem.WorldExporterChat("Processing mesh into STL facets...");

        STLDocument document = new STLDocument();
        int facetsAdded = 0;

        foreach (var passMeshes in meshData.Values)
        {
            foreach (var meshWithPos in passMeshes)
            {
                MeshData mesh = meshWithPos.Mesh;
                Vec3i worldPos = meshWithPos.WorldPosition;

                Vec3f offset = new Vec3f((worldPos - exportOrigin.AsVec3i).X,
                                         (worldPos - exportOrigin.AsVec3i).Y,
                                         (worldPos - exportOrigin.AsVec3i).Z);

                facetsAdded += AddMeshToSTL(document, mesh, offset);
            }
        }

        WorldExporterModSystem.WorldExporterChat($"Created {facetsAdded} facets. Writing to {outputPath}...");

        using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            document.WriteText(fs);
        }

        WorldExporterModSystem.WorldExporterChat("STL export complete!");
    }

    private int AddMeshToSTL(STLDocument document, MeshData mesh, Vec3f offset)
    {
        if (mesh.mode != EnumDrawMode.Triangles)
        {
            WorldExporterModSystem.WorldExporterLog("Draw mode is not triangles");
            return 0;
        }

        int facetsAdded = 0;

        try
        {
            Vertex[] vertices = new Vertex[mesh.VerticesCount];

            for (var v = 0; v < mesh.VerticesCount; v++)
            {
                vertices[v] = new Vertex(
                    mesh.xyz[v * 3] + offset.X,
                    mesh.xyz[v * 3 + 1] + offset.Y,
                    mesh.xyz[v * 3 + 2] + offset.Z
                );
            }

            var facets = new Facet[(int)Math.Ceiling((float)(mesh.IndicesCount / 3))];
            for (var i = 0; i < mesh.IndicesCount; i += 3)
            {
                try
                {
                    facets[i / 3] = new Facet(null, new Vertex[] {
                        vertices[mesh.Indices[i]],
                        vertices[mesh.Indices[i + 1]],
                        vertices[mesh.Indices[i + 2]]
                    }, 0);
                    facetsAdded++;
                }
                catch
                {
                    WorldExporterModSystem.WorldExporterLog("Failed to create facet.");
                }
            }

            document.AppendFacets(facets);
        }
        catch (Exception e)
        {
            WorldExporterModSystem.WorldExporterLog($"Failed to add facets to document: {e.Message}");
        }

        return facetsAdded;
    }
}
