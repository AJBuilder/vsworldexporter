using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldExporter
{
    public class TerrainMeshPool : ITerrainMeshPool
    {
        public IList<MeshData> meshes = new List<MeshData>();
        ICoreClientAPI client_api;
        ModelTransform transform;

        public TerrainMeshPool(ICoreClientAPI api)
        {
            client_api = api;
            transform = new ModelTransform();
            transform.EnsureDefaultValues();
        }

        public void AddMeshData(MeshData mesh, int lodLevel = 0)
        {
            if (mesh == null) return;
            mesh = mesh.Clone();
            if (transform != null)
            {
                mesh.ModelTransform(transform);
            }
            meshes.Add(mesh);
        }

        public void AddMeshData(MeshData mesh, float[] tfMatrix, int lodLevel = 0)
        {
            if (mesh == null) return;
            mesh = mesh.Clone();
            mesh.MatrixTransform(tfMatrix);
            if (transform != null)
            {
                mesh.ModelTransform(transform);
            }
            meshes.Add(mesh);
        }

        public void AddMeshData(MeshData mesh, ColorMapData colorMapData, int lodLevel = 0)
        {
            this.AddMeshData(mesh, lodLevel);
        }

        public void SetTranslation(Vec3f translation)
        {
            transform.Translation = translation;
        }
    }
}
