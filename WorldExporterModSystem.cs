using QuantumConcepts.Formats.StereoLithography;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using static System.Runtime.InteropServices.JavaScript.JSType;

//[assembly: ModInfo("WorldExporter", "worldexporter",
//                    Authors = new string[] { "Durus" },
//                    Description = "Adds the ability to export the world in 3D file formats.",
//                    Version = "1.0.0")]

namespace WorldExporter
{public class WorldExporterModSystem : ModSystem
    {
        static ICoreClientAPI capi;

        static string logging_prefix = "[WorldExporter]";

        Config config;

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
        internal static void WorldExporterLog(string message)
        {
            capi.Logger.Chat($"{logging_prefix} {message}");
        }
        internal static void WorldExporterChat(string message)
        {
            capi.Event.EnqueueMainThreadTask(() =>
            {
                capi.World.Player.ShowChatNotification($"{logging_prefix} {message}");
            }, $"world exporter chat: {message}");
        }
        static void WriteMeshToSTL(MeshData mesh, FileStream fs)
        {
            WorldExporterChat($"Processing mesh into facets...");
            STLDocument document = new STLDocument();
            int facets_added = 0;
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
        public bool ExportCuboid(IClientWorldAccessor world, BlockPos start, BlockPos end)
        {
            if (!Path.Exists(config.outputDirectory))
            {
                WorldExporterChat($"Output directory \"{config.outputDirectory}\" does not exist. Run \".wexporter director PATH\" to set.");
                return false;
            }

            TerrainMeshPool mesh_pool;
            try
            {
                Cuboidi volume_to_export = new Cuboidi(start, end);
                mesh_pool = new TerrainMeshPool(capi);
                mesh_pool.AddCuboid(world, volume_to_export);
            }
            catch (Exception e)
            {
                var error = "Failed to generate mesh.";
                WorldExporterChat(error + " See log for more details.");
                WorldExporterLog(error + $" Exception: {e}");
                return false;

            }


            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var output_file = Path.Combine(config.outputDirectory, $"world_export_{timestamp}.stl");
            try
            {
                var task = new Task(() =>
                {
                    using (FileStream fs = new FileStream(output_file, FileMode.Create, FileAccess.Write))
                    {
                        WriteMeshToSTL(mesh_pool.meshPool, fs);
                    }
                });
                task.Start();
                task.Wait(10000);
            }
            catch (Exception e)
            {
                var error = $"Failed to export to \"{config.outputDirectory}\".";
                WorldExporterChat(error + " See log for more details.");
                WorldExporterLog(error + $" Exception: {e}");
                return false;
            }
            return true;
        }



        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            capi = api;

            // Load config
            try
            {
                config = api.LoadModConfig<Config>("worldexporter.json");
                if (config == null)
                {
                    config = new Config();
                }
                //Save a copy of the mod config.
                api.StoreModConfig<Config>(config, "worldexporter.json");
            }
            catch (Exception e)
            {
                WorldExporterLog($"Encountered an error when loading config! Loading default settings instead. Exception: {e}");
                config = new Config();
            }


            // Build commands
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
                .EndSubCommand()
                    .BeginSubCommand("directory")
                        .WithDescription("Show or set the output directory. This can also be changed in the \"worldexporter.json\" located in the installs ModConfig folder.")
                        .RequiresPrivilege(Privilege.chat)
                        .WithArgs(new ICommandArgumentParser[] { new StringArgParser("path", false)})
                        .HandleWith((args) =>
                        {
                            var path = args[0];
                            if (path == null)
                            {
                                return TextCommandResult.Success($"Current output directory: \"{config.outputDirectory}\"");
                            }
                            else
                            {
                                if (Directory.Exists((string)path))
                                {
                                    config.outputDirectory = (string)path;
                                    api.StoreModConfig<Config>(config, "worldexporter.json");
                                    return TextCommandResult.Success($"Set output directory to \"{path}\".");
                                }
                                else
                                {
                                    return TextCommandResult.Success($"Directory does not exist.");
                                }
                            }
                        })
                    .EndSubCommand();
        }
    }
}
