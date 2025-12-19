using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        void ITerrainMeshPool.AddMeshData(MeshData mesh, int lodLevel = 0)
        {
            if (mesh == null) return;
            mesh = mesh.Clone();
            if (transform != null)
            {
                mesh.ModelTransform(transform);
            }
            meshes.Add(mesh);
        }

        void ITerrainMeshPool.AddMeshData(MeshData mesh, float[] tfMatrix, int lodLevel = 0)
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

        void ITerrainMeshPool.AddMeshData(MeshData mesh, ColorMapData colorMapData, int lodLevel = 0)
        {
            ((ITerrainMeshPool)this).AddMeshData(mesh, lodLevel);
        }

        public void AddCuboid(IClientWorldAccessor world, Cuboidi cuboidi)
        {
            this.AddCuboid(world, cuboidi.Start.AsBlockPos, cuboidi.End.AsBlockPos);
        }

        public void AddCuboid(IClientWorldAccessor world, BlockPos start_pos, BlockPos end_pos)
        {

            // Tesselating entities is done in it's own thread, so we can start them all first, then wait for them to all finish at the end.
            //Entity[] entities = world.GetEntitiesInsideCuboid(start_pos, end_pos);
            //CountdownEvent entity_tesselation_countdown = new CountdownEvent(entities.Length);
            //foreach (Entity entity in entities)
            //{
            //    if (entity.Properties.Client.Renderer is EntityShapeRenderer renderer)
            //    {
            //        renderer.TesselateShape((mesh) =>
            //        {
            //            try
            //            {
            //                ((ITerrainMeshPool)this).AddMeshData(mesh);
            //            }
            //            finally
            //            {
            //                entity_tesselation_countdown.Signal();
            //            }
            //        });
            //    }
            //    else
            //    {
            //        entity_tesselation_countdown.Signal();
            //    }
            //}

            world.BlockAccessor.WalkBlocks(start_pos, end_pos, (block, x, y, z) =>
            {
                try
                {
                    transform.Translation = new Vec3f((new BlockPos(x, y, z) - start_pos).AsVec3i);
                    // If its a block entity, tesselate it and add.
                    var block_entity = world.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z));
                    if (block_entity != null)
                    {
                        // Supposedly, it is not thread safe to use the main thread tesselator?
                        // We'll use it anyway? Perhaps as long as we block the main thread?
                        bool skip_default = block_entity.OnTesselation(this, client_api.Tesselator);

                        // If we aren't adding the default mesh, continue to next block.
                        if (skip_default) return;
                    }

                    // Get the base/cached/default mesh
                    MeshData mesh = client_api.TesselatorManager.GetDefaultBlockMesh(block);
                    if (mesh != null)
                    {
                        ((ITerrainMeshPool)this).AddMeshData(mesh);
                    }
                }
                catch
                {
                    WorldExporterModSystem.WorldExporterChat($"Failed to get mesh of {block.Code} at {x},{y},{z}");
                }
            });

            // Wait for all entites to be finished
            //entity_tesselation_countdown.Wait(5000);
        }

    }

}
