using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using QuantumConcepts.Formats.StereoLithography;
using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using System.IO;
using Vintagestory.API.Common.Entities;
using System.Collections.Concurrent;
using System.Threading;
using Vintagestory.GameContent;
using System.Linq;

//[assembly: ModInfo("WorldExporter", "worldexporter",
//                    Authors = new string[] { "Durus" },
//                    Description = "Adds the ability to export the world in 3D file formats.",
//                    Version = "1.0.0")]

namespace WorldExporter
{public class WorldExporterModSystem : ModSystem
    {
        static ICoreClientAPI client_api;

        static string logging_prefix = "[WorldExporter]";

        string output_dir = "C:\\Users\\adama\\Documents\\world_exports";

        public WorldExporterModSystem()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var assemblyName = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
                var modDir = Path.GetDirectoryName(typeof(WorldExporterModSystem).Assembly.Location);
                var libPath = Path.Combine(modDir, "native", assemblyName);

                if (File.Exists(libPath))
                {
                    return System.Reflection.Assembly.LoadFrom(libPath);
                }

                return null;
            };
        }

        public override bool ShouldLoad(EnumAppSide side)
        {
            return side == EnumAppSide.Client;
        }
        private static void WorldExporterLog(string message)
        {
            client_api.Logger.Chat($"{logging_prefix} {message}");
        }
        private static void WorldExporterChat(string message)
        {
            client_api.World.Player.ShowChatNotification($"{logging_prefix} {message}");
        }
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
                        WorldExporterChat($"Failed to get mesh of {block.Code} at {x},{y},{z}");
                    }
                });

                // Wait for all entites to be finished
                //entity_tesselation_countdown.Wait(5000);
            }

        }

        void WriteMeshToSTL(IEnumerable<MeshData> meshes, FileStream fs)
        {
            WorldExporterChat($"Processing {meshes.Count()} meshes into facets...");
            STLDocument document = new STLDocument();
            int facets_added = 0;
            foreach (MeshData mesh in meshes)
            {
                if (mesh.mode != EnumDrawMode.Triangles)
                {
                    WorldExporterLog("Draw mode is not triangles");
                    return;
                }


                // Convert all vertices since we don't want to be duplicating data
                // Potential for optimization: only create one when needed, but cache it for later.
                try
                {
                    Vertex[] vertices = new Vertex[mesh.VerticesCount];
                    try
                    {
                        for (var v = 0; v < mesh.VerticesCount; v++)
                        {
                            vertices[v] = new Vertex(mesh.xyz[v * 3], mesh.xyz[v * 3 + 1], mesh.xyz[v * 3 + 2]);
                        }
                    }
                    catch
                    {
                        WorldExporterLog("Failed to read vertices.");
                    }

                    var facets = new Facet[(int)Math.Ceiling((float)(mesh.IndicesCount / 3))];
                    for (var i = 0; i < mesh.IndicesCount; i += 3)
                    {
                        try
                        {
                            facets[i/3] = new Facet(null, new Vertex[] {vertices[mesh.Indices[i]],
                                                                          vertices[mesh.Indices[i+1]],
                                                                          vertices[mesh.Indices[i+2]] }, 0);
                            facets_added++;
                        }
                        catch
                        {
                            WorldExporterLog("Failed to create facet.");
                        }
                    }
                    document.AppendFacets(facets);
                }
                catch (Exception e)
                {
                    WorldExporterLog("Failed to add facets to document.");
                }
            }

            WorldExporterChat($"Created {facets_added} facets. Writing to document at {fs.Name}...");
            document.WriteText(fs);
            WorldExporterChat("Done!");
        }

        public void ExportCuboid(IClientWorldAccessor world, BlockPos center, int x, int y, int z)
        {
            BlockPos start = new BlockPos((int)Math.Ceiling((float)center.X - (x / 2)),
                                                            (int)Math.Ceiling((float)center.Y - (y / 2)),
                                                            (int)Math.Ceiling((float)center.Z - (z / 2)),
                                                            center.dimension);
            BlockPos end = new BlockPos((int)Math.Ceiling((float)center.X + (x / 2)),
                                                          (int)Math.Ceiling((float)center.Y + (y / 2)),
                                                          (int)Math.Ceiling((float)center.Z + (z / 2)),
                                                          center.dimension);
            ExportCuboid(world, start, end);
        }
        public void ExportCuboid(IClientWorldAccessor world, BlockPos start, BlockPos end)
        {
            Cuboidi volume_to_export = new Cuboidi(start, end);
            TerrainMeshPool mesh_pool = new TerrainMeshPool(client_api);
            mesh_pool.AddCuboid(world, volume_to_export);


            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = $"world_export_{timestamp}.stl";
            var output_file = Path.Combine(output_dir, filename);
            using (FileStream fs = new FileStream(output_file, FileMode.Create, FileAccess.Write))
            {
                WriteMeshToSTL(mesh_pool.meshes, fs);
            }
        }



        public override void StartClientSide(ICoreClientAPI api)
        {
            client_api = api;
            api.ChatCommands.Create("wexporter")
                .BeginSubCommand("export")
                    .BeginSubCommand("cube")
                        .BeginSubCommand("around")
                            .WithDescription("Exports a cube of the specified x/y/z lengths around the given position.")
                            .WithArgs(new ICommandArgumentParser[] { new WorldPositionArgParser("center", api, true),
                                                                     new IntArgParser("x", 10, false),
                                                                     new IntArgParser("y", 10, false),
                                                                     new IntArgParser("z", 10, false),})
                            .RequiresPrivilege(Privilege.chat)
                            .HandleWith((args) =>
                            {
                                BlockPos center = new BlockPos(((Vec3d)args[0]).AsVec3i, args.Caller.Entity.Pos.Dimension);
                                int x = (int)args[1];
                                int y = (int)args[2];
                                int z = (int)args[3];
                                ExportCuboid((IClientWorldAccessor)args.Caller.Entity.World, center, x, y, z);
                                return TextCommandResult.Success();
                            })
                        .EndSubCommand()
                        .BeginSubCommand("within")
                            .WithDescription("Exports a cube of with the specified start/end coordinates.")
                            .WithArgs(new ICommandArgumentParser[] { new WorldPositionArgParser("start", api, true),
                                                                     new WorldPositionArgParser("end", api, true)})
                            .RequiresPrivilege(Privilege.chat)
                            .HandleWith((args) =>
                            {
                                Vec3d start = (Vec3d)args[0];
                                Vec3d end = (Vec3d)args[1];
                                ExportCuboid((IClientWorldAccessor)args.Caller.Entity.World,
                                             new BlockPos(start.AsVec3i, args.Caller.Entity.Pos.Dimension),
                                             new BlockPos(end.AsVec3i, args.Caller.Entity.Pos.Dimension));
                                return TextCommandResult.Success();
                            })
                        .EndSubCommand()
                    .EndSubCommand()
                .EndSubCommand();
            base.StartClientSide(api);
        }
    }
}
