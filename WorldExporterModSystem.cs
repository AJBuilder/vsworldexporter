using QuantumConcepts.Formats.StereoLithography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WorldExporter
{
    public class WorldExporterModSystem : ModSystem
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

        public void ExportCuboid(IClientWorldAccessor world, BlockPos center, int x, int y, int z, string format = "stl")
        {
            BlockPos start = new BlockPos((int)Math.Ceiling((float)center.X - (x / 2)),
                                                            (int)Math.Ceiling((float)center.Y - (y / 2)),
                                                            (int)Math.Ceiling((float)center.Z - (z / 2)),
                                                            center.dimension);
            BlockPos end = new BlockPos((int)Math.Ceiling((float)center.X + (x / 2)),
                                                          (int)Math.Ceiling((float)center.Y + (y / 2)),
                                                          (int)Math.Ceiling((float)center.Z + (z / 2)),
                                                          center.dimension);
            ExportCuboid(world, start, end, format);
        }

        public bool ExportCuboid(IClientWorldAccessor world, BlockPos start, BlockPos end, string format = "stl")
        {
            WorldExporterLog($"ExportCuboid called with start=({start.X}, {start.Y}, {start.Z}) end=({end.X}, {end.Y}, {end.Z})");

            // Debug: Show player position and chunk
            var playerPos = capi.World.Player.Entity.Pos;
            int playerChunkX = (int)playerPos.X / 32;
            int playerChunkY = (int)playerPos.Y / 32;
            int playerChunkZ = (int)playerPos.Z / 32;
            WorldExporterLog($"Player is at block ({(int)playerPos.X}, {(int)playerPos.Y}, {(int)playerPos.Z}), chunk ({playerChunkX}, {playerChunkY}, {playerChunkZ})");

            var playerChunk = world.BlockAccessor.GetChunk(playerChunkX, playerChunkY, playerChunkZ);
            WorldExporterLog($"Player chunk loaded: {playerChunk != null}, empty: {playerChunk?.Empty}");

            if (!Path.Exists(config.outputDirectory))
            {
                WorldExporterChat($"Output directory \"{config.outputDirectory}\" does not exist. Run \".wexporter directory PATH\" to set.");
                return false;
            }

            var collector = new ChunkMeshCollector(capi, world);
            BlockPos alignedStart = collector.RoundToChunkBoundary(start);
            BlockPos alignedEnd = collector.RoundUpToChunkBoundary(end);

            WorldExporterLog($"After alignment: start=({alignedStart.X}, {alignedStart.Y}, {alignedStart.Z}) end=({alignedEnd.X}, {alignedEnd.Y}, {alignedEnd.Z})");

            if (alignedStart.X != start.X || alignedStart.Y != start.Y || alignedStart.Z != start.Z ||
                alignedEnd.X != end.X || alignedEnd.Y != end.Y || alignedEnd.Z != end.Z)
            {
                WorldExporterChat($"Region adjusted to chunk boundaries: ({alignedStart.X}, {alignedStart.Y}, {alignedStart.Z}) to ({alignedEnd.X}, {alignedEnd.Y}, {alignedEnd.Z})");
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            bool useObj = format?.ToLower() == "obj";
            string outputPath = useObj
                ? Path.Combine(config.outputDirectory, $"world_export_{timestamp}.obj")
                : Path.Combine(config.outputDirectory, $"world_export_{timestamp}.stl");

            WorldExporterChat($"Starting export on main thread...");

            try
            {
                var exporter = new ChunkExporter(capi, world);
                exporter.ExportRegionToChunks(alignedStart, alignedEnd, format, outputPath, RenderPassConfig.All());
            }
            catch (Exception e)
            {
                var error = $"Failed to export {format.ToUpper()} to \"{config.outputDirectory}\".";
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
                    .BeginSubCommand("here")
                        .WithDescription("Exports the chunk the player is currently standing in. Optional format: stl (default) or obj.")
                        .WithArgs(new ICommandArgumentParser[] {
                            new WordArgParser("format", false)})
                        .RequiresPrivilege(Privilege.chat)
                        .HandleWith((args) =>
                        {
                            var playerPos = api.World.Player.Entity.Pos;
                            int chunkX = (int)playerPos.X / 32;
                            int chunkY = (int)playerPos.Y / 32;
                            int chunkZ = (int)playerPos.Z / 32;

                            BlockPos start = new BlockPos(chunkX * 32, chunkY * 32, chunkZ * 32, api.World.Player.Entity.Pos.Dimension);
                            BlockPos end = new BlockPos((chunkX + 1) * 32, (chunkY + 1) * 32, (chunkZ + 1) * 32, api.World.Player.Entity.Pos.Dimension);

                            string format = args[0] as string ?? "stl";
                            WorldExporterChat($"Exporting chunk at player position: ({chunkX}, {chunkY}, {chunkZ})");
                            ExportCuboid((IClientWorldAccessor)args.Caller.Entity.World, start, end, format);
                            return TextCommandResult.Success();
                        })
                    .EndSubCommand()
                    .BeginSubCommand("cube")
                        .BeginSubCommand("around")
                            .WithDescription("Exports a cube of the specified x/y/z lengths around the given position. Optional format: stl (default) or obj.")
                            .WithArgs(new ICommandArgumentParser[] {
                                new WorldPositionArgParser("center", api, true),
                                new IntArgParser("x", 10, false),
                                new IntArgParser("y", 10, false),
                                new IntArgParser("z", 10, false),
                                new WordArgParser("format", false)})
                            .RequiresPrivilege(Privilege.chat)
                            .HandleWith((args) =>
                            {
                                BlockPos center = new BlockPos(((Vec3d)args[0]).AsVec3i, args.Caller.Entity.Pos.Dimension);
                                int x = (int)args[1];
                                int y = (int)args[2];
                                int z = (int)args[3];
                                string format = args[4] as string ?? "stl";
                                ExportCuboid((IClientWorldAccessor)args.Caller.Entity.World, center, x, y, z, format);
                                return TextCommandResult.Success();
                            })
                        .EndSubCommand()
                        .BeginSubCommand("within")
                            .WithDescription("Exports a cube with the specified start/end coordinates. Optional format: stl (default) or obj.")
                            .WithArgs(new ICommandArgumentParser[] {
                                new WorldPositionArgParser("start", api, true),
                                new WorldPositionArgParser("end", api, true),
                                new WordArgParser("format", false)})
                            .RequiresPrivilege(Privilege.chat)
                            .HandleWith((args) =>
                            {
                                Vec3d start = (Vec3d)args[0];
                                Vec3d end = (Vec3d)args[1];
                                string format = args[2] as string ?? "stl";
                                ExportCuboid((IClientWorldAccessor)args.Caller.Entity.World,
                                             new BlockPos(start.AsVec3i, args.Caller.Entity.Pos.Dimension),
                                             new BlockPos(end.AsVec3i, args.Caller.Entity.Pos.Dimension),
                                             format);
                                return TextCommandResult.Success();
                            })
                        .EndSubCommand()
                    .EndSubCommand()
                .EndSubCommand()
                    .BeginSubCommand("directory")
                        .WithDescription("Show or set the output directory. This can also be changed in the \"worldexporter.json\" located in the installs ModConfig folder.")
                        .RequiresPrivilege(Privilege.chat)
                        .WithArgs(new ICommandArgumentParser[] { new StringArgParser("path", false) })
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
