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

        static void WriteMeshToSTL(IEnumerable<MeshData> meshes, FileStream fs)
        {
            WorldExporterChat($"Processing mesh into facets...");
            STLDocument document = new STLDocument();
            int facets_added = 0;
            foreach (MeshData mesh in meshes)
            {
                if (mesh.mode != EnumDrawMode.Triangles)
                {
                    WorldExporterLog("Draw mode is not triangles");
                    return;
                }

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
                            facets[i / 3] = new Facet(null, new Vertex[] { vertices[mesh.Indices[i]],
                                                                            vertices[mesh.Indices[i + 1]],
                                                                            vertices[mesh.Indices[i + 2]] }, 0);
                            facets_added++;
                        }
                        catch
                        {
                            WorldExporterLog("Failed to create facet.");
                        }
                    }
                    document.AppendFacets(facets);
                }
                catch
                {
                    WorldExporterLog("Failed to add facets to document.");
                }
            }

            WorldExporterChat($"Created {facets_added} facets. Writing to document at {fs.Name}...");
            document.WriteText(fs);
            WorldExporterChat("Done!");
        }

        static void WriteToOBJ(IList<MeshData> meshes, IClientWorldAccessor world, BlockPos start, BlockPos end, string baseOutputPath)
        {
            string objPath = baseOutputPath + ".obj";
            string mtlPath = baseOutputPath + ".mtl";
            string mtlFilename = Path.GetFileName(mtlPath);
            string outputDir = Path.GetDirectoryName(baseOutputPath);
            string texturesDir = Path.Combine(outputDir, "textures");
            Directory.CreateDirectory(texturesDir);

            // Build texture position to asset location mapping
            var texturePosToAsset = new Dictionary<(float, float, float, float), AssetLocation>();

            world.BlockAccessor.WalkBlocks(start, end, (block, x, y, z) =>
            {
                if (block.FirstCodePart() == "air") return;

                if (block.Textures != null)
                {
                    foreach (var kvp in block.Textures)
                    {
                        try
                        {
                            BakedCompositeTexture baked = kvp.Value?.Baked;
                            if (baked == null) continue;

                            var variants = baked.BakedVariants ?? new[] { baked };
                            foreach (BakedCompositeTexture variant in variants)
                            {
                                int subId = variant.TextureSubId;
                                if (subId < 0 || subId >= capi.BlockTextureAtlas.Positions.Length) continue;
                                TextureAtlasPosition pos = capi.BlockTextureAtlas.Positions[subId];
                                if (pos == null) continue;
                                var key = (pos.x1, pos.y1, pos.x2, pos.y2);
                                if (!texturePosToAsset.ContainsKey(key))
                                {
                                    AssetLocation loc = (variant.TextureFilenames?.Length > 0)
                                        ? variant.TextureFilenames[0]
                                        : kvp.Value.Base;
                                    texturePosToAsset[key] = loc;
                                }
                            }
                        }
                        catch { }
                    }
                }
            });

            var usedSubIds = new HashSet<int>();
            var vec4 = new Vec4f();

            using var objWriter = new StreamWriter(objPath);
            objWriter.WriteLine("# Exported by vsworldexporter");
            objWriter.WriteLine($"mtllib {mtlFilename}");

            // OBJ indices are 1-based and global across the file.
            // Since we write exactly VerticesCount vt and vn lines per mesh (matching v),
            // a single running offset covers all three index types.
            int indexBase = 1;

            foreach (MeshData mesh in meshes)
            {
                if (mesh == null) continue;

                bool hasUvs = mesh.Uv != null && mesh.TextureIds != null && mesh.TextureIndices != null;
                bool hasNormals = mesh.Normals != null;

                // --- Vertex positions ---
                for (int i = 0; i < mesh.VerticesCount; i++)
                {
                    objWriter.WriteLine(FormattableString.Invariant(
                        $"v {mesh.xyz[i * 3]:G6} {mesh.xyz[i * 3 + 1]:G6} {mesh.xyz[i * 3 + 2]:G6}"));
                }

                // --- UV coordinates ---
                for (int i = 0; i < mesh.VerticesCount; i++)
                {
                    if (!hasUvs)
                    {
                        objWriter.WriteLine("vt 0 0");
                        continue;
                    }

                    int faceIdx = i / mesh.VerticesPerFace;
                    if (faceIdx < mesh.TextureIndices.Length)
                    {
                        byte texIndice = mesh.TextureIndices[faceIdx];
                        if (texIndice < mesh.TextureIds.Length)
                        {
                            int subId = mesh.TextureIds[texIndice];
                            TextureAtlasPosition texPos = capi.BlockTextureAtlas.Positions[subId];
                            if (texPos != null)
                            {
                                float atlasW = texPos.x2 - texPos.x1;
                                float atlasH = texPos.y2 - texPos.y1;
                                float u = atlasW > 0f ? (mesh.Uv[i * 2] - texPos.x1) / atlasW : 0f;
                                // Flip V: OBJ has V=0 at bottom, VS textures have V=0 at top
                                float v = atlasH > 0f ? 1f - (mesh.Uv[i * 2 + 1] - texPos.y1) / atlasH : 0f;
                                objWriter.WriteLine(FormattableString.Invariant($"vt {u:G6} {v:G6}"));
                                usedSubIds.Add(subId);
                                continue;
                            }
                        }
                    }
                    objWriter.WriteLine("vt 0 0");
                }

                // --- Normals ---
                for (int i = 0; i < mesh.VerticesCount; i++)
                {
                    if (!hasNormals)
                    {
                        objWriter.WriteLine("vn 0 1 0");
                        continue;
                    }
                    NormalUtil.FromPackedNormal(mesh.Normals[i], ref vec4);
                    objWriter.WriteLine(FormattableString.Invariant(
                        $"vn {vec4.X:G6} {vec4.Y:G6} {vec4.Z:G6}"));
                }

                // --- Faces grouped by material to minimise usemtl switches ---
                var materialGroups = new Dictionary<int, List<(int v0, int v1, int v2)>>();

                for (int i = 0; i < mesh.IndicesCount; i += 3)
                {
                    int vi0 = (int)mesh.Indices[i];
                    int vi1 = (int)mesh.Indices[i + 1];
                    int vi2 = (int)mesh.Indices[i + 2];

                    int subId = -1;
                    if (hasUvs)
                    {
                        int faceIdx = vi0 / mesh.VerticesPerFace;
                        if (faceIdx < mesh.TextureIndices.Length)
                        {
                            byte texIndice = mesh.TextureIndices[faceIdx];
                            if (texIndice < mesh.TextureIds.Length)
                                subId = mesh.TextureIds[texIndice];
                        }
                    }

                    if (!materialGroups.TryGetValue(subId, out var list))
                    {
                        list = new List<(int, int, int)>();
                        materialGroups[subId] = list;
                    }
                    list.Add((vi0, vi1, vi2));
                }

                foreach (var (subId, triangles) in materialGroups)
                {
                    if (subId >= 0)
                        objWriter.WriteLine($"usemtl mat_{subId}");

                    foreach (var (vi0, vi1, vi2) in triangles)
                    {
                        int v0 = indexBase + vi0;
                        int v1 = indexBase + vi1;
                        int v2 = indexBase + vi2;
                        objWriter.WriteLine($"f {v0}/{v0}/{v0} {v1}/{v1}/{v1} {v2}/{v2}/{v2}");
                    }
                }

                indexBase += mesh.VerticesCount;
            }

            // --- Debug: show what texturePosToAsset contains vs usedSubIds positions ---
            WorldExporterLog($"[DBG] texturePosToAsset entries: {texturePosToAsset.Count}, usedSubIds: {string.Join(", ", usedSubIds)}");
            foreach (var kv in texturePosToAsset)
                WorldExporterLog($"[DBG] posMap: ({kv.Key.Item1:G4},{kv.Key.Item2:G4},{kv.Key.Item3:G4},{kv.Key.Item4:G4}) -> {kv.Value}");
            foreach (int sid in usedSubIds)
            {
                TextureAtlasPosition p = capi.BlockTextureAtlas.Positions[sid];
                WorldExporterLog($"[DBG] subId {sid} atlas pos: ({p?.x1:G4},{p?.y1:G4},{p?.x2:G4},{p?.y2:G4})");
            }

            // --- Build subId → AssetLocation via the position map from TerrainMeshPool ---
            // texturePosToAsset was built using GetTextureSource(block) per block, so its
            // positions are guaranteed to match what the tessellator stored in mesh.TextureIds.
            var textureSubIdToAsset = new Dictionary<int, AssetLocation>();
            foreach (int subId in usedSubIds)
            {
                TextureAtlasPosition pos = capi.BlockTextureAtlas.Positions[subId];
                if (pos == null) continue;
                if (texturePosToAsset.TryGetValue((pos.x1, pos.y1, pos.x2, pos.y2), out AssetLocation loc))
                    textureSubIdToAsset[subId] = loc;
                else
                    WorldExporterLog($"Could not resolve asset for texture subId {subId}");
            }

            // --- MTL file + texture PNG export ---
            using var mtlWriter = new StreamWriter(mtlPath);
            mtlWriter.WriteLine("# Materials exported by vsworldexporter");

            foreach (int subId in usedSubIds)
            {
                string texFilename = $"tex_{subId}.png";
                if (textureSubIdToAsset.TryGetValue(subId, out AssetLocation loc))
                {
                    string sanitised = (loc.Domain + "_" + loc.Path).Replace('/', '_').Replace('\\', '_') + ".png";
                    texFilename = "textures/" + sanitised;

                    IAsset texAsset = capi.Assets.TryGet(
                        loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
                    if (texAsset != null)
                    {
                        File.WriteAllBytes(Path.Combine(texturesDir, sanitised), texAsset.Data);
                    }
                    else
                    {
                        WorldExporterLog($"Could not load texture asset for {loc}");
                    }
                }

                mtlWriter.WriteLine($"newmtl mat_{subId}");
                mtlWriter.WriteLine($"map_Kd {texFilename}");
                mtlWriter.WriteLine();
            }

            WorldExporterChat($"OBJ export complete. Written to {objPath} with {usedSubIds.Count} unique textures.");
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
            if (!Path.Exists(config.outputDirectory))
            {
                WorldExporterChat($"Output directory \"{config.outputDirectory}\" does not exist. Run \".wexporter directory PATH\" to set.");
                return false;
            }

            TerrainMeshPool mesh_pool;
            try
            {
                mesh_pool = new TerrainMeshPool(capi);

                // Walk blocks and gather meshes (no texture mapping)
                world.BlockAccessor.WalkBlocks(start, end, (block, x, y, z) =>
                {
                    try
                    {
                        // Skip air blocks (1.21.x air has default cube mesh)
                        if (block.FirstCodePart() == "air") return;

                        // Set transform relative to start position
                        mesh_pool.SetTranslation(new Vec3f((new BlockPos(x, y, z) - start).AsVec3i));

                        // Handle block entities
                        var block_entity = world.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z));
                        if (block_entity != null)
                        {
                            // Supposedly, it is not thread safe to use the main thread tesselator?
                            // We'll use it anyway? Perhaps as long as we block the main thread?
                            bool skip_default = block_entity.OnTesselation(mesh_pool, capi.Tesselator);

                            // If we aren't adding the default mesh, continue to next block.
                            if (skip_default) return;
                        }

                        // Get and add the default mesh
                        MeshData mesh = capi.TesselatorManager.GetDefaultBlockMesh(block);
                        if (mesh != null)
                        {
                            mesh_pool.AddMeshData(mesh);
                        }
                    }
                    catch
                    {
                        WorldExporterChat($"Failed to get mesh of {block.Code} at {x},{y},{z}");
                    }
                });
            }
            catch (Exception e)
            {
                var error = "Failed to generate mesh.";
                WorldExporterChat(error + " See log for more details.");
                WorldExporterLog(error + $" Exception: {e}");
                return false;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            bool useObj = format?.ToLower() == "obj";

            if (useObj)
            {
                string baseOutputPath = Path.Combine(config.outputDirectory, $"world_export_{timestamp}");
                try
                {
                    var task = new Task(() =>
                    {
                        WriteToOBJ(mesh_pool.meshes, world, start, end, baseOutputPath);
                    });
                    task.Start();
                    task.Wait(30000);
                }
                catch (Exception e)
                {
                    var error = $"Failed to export OBJ to \"{config.outputDirectory}\".";
                    WorldExporterChat(error + " See log for more details.");
                    WorldExporterLog(error + $" Exception: {e}");
                    return false;
                }
            }
            else
            {
                string output_file = Path.Combine(config.outputDirectory, $"world_export_{timestamp}.stl");
                try
                {
                    var task = new Task(() =>
                    {
                        using (FileStream fs = new FileStream(output_file, FileMode.Create, FileAccess.Write))
                        {
                            WriteMeshToSTL(mesh_pool.meshes, fs);
                        }
                    });
                    task.Start();
                    task.Wait(10000);
                }
                catch (Exception e)
                {
                    var error = $"Failed to export STL to \"{config.outputDirectory}\".";
                    WorldExporterChat(error + " See log for more details.");
                    WorldExporterLog(error + $" Exception: {e}");
                    return false;
                }
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
