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

        static void WriteToSTL(IClientWorldAccessor world, BlockPos start, BlockPos end, string outputPath)
        {
            // Create local mesh pool
            TerrainMeshPool mesh_pool = new TerrainMeshPool(capi);

            // Walk blocks and gather meshes
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

            // Convert meshes to STL facets
            WorldExporterChat($"Processing mesh into facets...");
            STLDocument document = new STLDocument();
            int facets_added = 0;
            foreach (MeshData mesh in mesh_pool.meshes)
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

            // Write STL file
            WorldExporterChat($"Created {facets_added} facets. Writing to document at {outputPath}...");
            using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                document.WriteText(fs);
            }
            WorldExporterChat("Done!");
        }

        static void WriteToOBJ(IClientWorldAccessor world, BlockPos start, BlockPos end, string baseOutputPath)
        {
            string objPath = baseOutputPath + ".obj";
            string mtlPath = baseOutputPath + ".mtl";
            string mtlFilename = Path.GetFileName(mtlPath);
            string outputDir = Path.GetDirectoryName(baseOutputPath);
            string texturesDir = Path.Combine(outputDir, "textures");
            Directory.CreateDirectory(texturesDir);

            // Create local mesh pool
            TerrainMeshPool mesh_pool = new TerrainMeshPool(capi);

            // Helper function: Find texture position for given UV coordinates
            (float x1, float y1, float x2, float y2, int subId) FindTexturePosition(int atlasTextureId, float uvX, float uvY)
            {
                for (int subId = 0; subId < capi.BlockTextureAtlas.Positions.Length; subId++)
                {
                    var pos = capi.BlockTextureAtlas.Positions[subId];
                    if (pos == null || pos.atlasTextureId != atlasTextureId)
                        continue;

                    // Check if UV falls within this texture's rectangle
                    if (uvX >= pos.x1 && uvX <= pos.x2 &&
                        uvY >= pos.y1 && uvY <= pos.y2)
                    {
                        return (pos.x1, pos.y1, pos.x2, pos.y2, subId);
                    }
                }
                return (-1, -1, -1, -1, -1);  // Not found
            }

            int blocksProcessed = 0;

            // Single pass: gather meshes AND build texture mapping
            world.BlockAccessor.WalkBlocks(start, end, (block, x, y, z) =>
            {
                if (block.FirstCodePart() == "air") return;
                blocksProcessed++;

                // Mesh gathering
                bool should_add_default_mesh = true;
                try
                {
                    mesh_pool.SetTranslation(new Vec3f((new BlockPos(x, y, z) - start).AsVec3i));

                    var block_entity = world.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z));
                    if (block_entity != null)
                    {
                        // Supposedly, it is not thread safe to use the main thread tesselator?
                        // We'll use it anyway? Perhaps as long as we block the main thread?
                        should_add_default_mesh = !block_entity.OnTesselation(mesh_pool, capi.Tesselator);
                    }

                    if (should_add_default_mesh)
                    {
                        MeshData mesh = capi.TesselatorManager.GetDefaultBlockMesh(block);
                        if (mesh != null)
                        {
                            mesh_pool.AddMeshData(mesh);
                        }
                    }
                }
                catch
                {
                    WorldExporterChat($"Failed to get mesh of {block.Code} at {x},{y},{z}");
                }
            });

            WorldExporterLog($"Blocks processed: {blocksProcessed}");

            // Track used textures by position coordinates (x1,y1,x2,y2,subId)
            var usedTextures = new HashSet<(float, float, float, float, int)>();
            var vec4 = new Vec4f();

            using var objWriter = new StreamWriter(objPath);
            objWriter.WriteLine("# Exported by vsworldexporter");
            objWriter.WriteLine($"mtllib {mtlFilename}");

            // OBJ indices are 1-based and global across the file.
            // Since we write exactly VerticesCount vt and vn lines per mesh (matching v),
            // a single running offset covers all three index types.
            int indexBase = 1;

            foreach (MeshData mesh in mesh_pool.meshes)
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
                            int atlasTextureId = mesh.TextureIds[texIndice];
                            float uvX = mesh.Uv[i * 2];
                            float uvY = mesh.Uv[i * 2 + 1];

                            // Find texture position containing this UV
                            var (x1, y1, x2, y2, subId) = FindTexturePosition(atlasTextureId, uvX, uvY);

                            if (subId >= 0)
                            {
                                // Calculate local UV (within this texture)
                                float atlasW = x2 - x1;
                                float atlasH = y2 - y1;
                                float u = atlasW > 0f ? (uvX - x1) / atlasW : 0f;
                                float v = atlasH > 0f ? 1f - (uvY - y1) / atlasH : 0f;  // flip for OBJ

                                objWriter.WriteLine(FormattableString.Invariant($"vt {u:G6} {v:G6}"));
                                usedTextures.Add((x1, y1, x2, y2, subId));
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

                    int textureSubId = -1;
                    if (hasUvs)
                    {
                        int faceIdx = vi0 / mesh.VerticesPerFace;
                        if (faceIdx < mesh.TextureIndices.Length)
                        {
                            byte texIndice = mesh.TextureIndices[faceIdx];
                            if (texIndice < mesh.TextureIds.Length)
                            {
                                int atlasTextureId = mesh.TextureIds[texIndice];
                                float uvX = mesh.Uv[vi0 * 2];
                                float uvY = mesh.Uv[vi0 * 2 + 1];

                                var (_, _, _, _, subId) = FindTexturePosition(atlasTextureId, uvX, uvY);
                                textureSubId = subId;  // Only used for material naming (mat_XXX)
                            }
                        }
                    }

                    if (!materialGroups.TryGetValue(textureSubId, out var list))
                    {
                        list = new List<(int, int, int)>();
                        materialGroups[textureSubId] = list;
                    }
                    list.Add((vi0, vi1, vi2));
                }

                foreach (var (textureSubId, triangles) in materialGroups)
                {
                    if (textureSubId >= 0)
                        objWriter.WriteLine($"usemtl mat_{textureSubId}");

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

            // Helper function: Get AssetLocation for a given subId (lazy, cached)
            AssetLocation GetAssetLocationForSubId(int subId, Dictionary<int, AssetLocation> cache)
            {
                if (cache.TryGetValue(subId, out var cached))
                    return cached;

                // Lazy search: only runs for unique subIds actually used
                foreach (Block block in capi.World.Blocks)
                {
                    if (block?.Textures == null) continue;
                    foreach (var kvp in block.Textures)
                    {
                        var baked = kvp.Value?.Baked;
                        if (baked?.TextureSubId == subId)
                        {
                            cache[subId] = baked.BakedName;
                            return baked.BakedName;
                        }

                        if (baked?.BakedVariants != null)
                        {
                            foreach (var variant in baked.BakedVariants)
                            {
                                if (variant.TextureSubId == subId)
                                {
                                    cache[subId] = variant.BakedName;
                                    return variant.BakedName;
                                }
                            }
                        }

                        if (baked?.BakedTiles != null)
                        {
                            foreach (var tile in baked.BakedTiles)
                            {
                                if (tile.TextureSubId == subId)
                                {
                                    cache[subId] = tile.BakedName;
                                    return tile.BakedName;
                                }
                            }
                        }
                    }
                }
                return null;
            }

            // Extract unique subIds from usedTextures
            var uniqueSubIds = new HashSet<int>();
            foreach (var (x1, y1, x2, y2, subId) in usedTextures)
            {
                if (subId >= 0)
                    uniqueSubIds.Add(subId);
            }

            // --- MTL file + texture PNG export ---
            using var mtlWriter = new StreamWriter(mtlPath);
            mtlWriter.WriteLine("# Materials exported by vsworldexporter");

            var subIdToAssetLoc = new Dictionary<int, AssetLocation>();
            int texturesExported = 0;

            foreach (int subId in uniqueSubIds)
            {
                AssetLocation loc = GetAssetLocationForSubId(subId, subIdToAssetLoc);
                string texFilename = $"tex_{subId}.png";

                if (loc != null)
                {
                    string sanitised = (loc.Domain + "_" + loc.Path).Replace('/', '_').Replace('\\', '_') + ".png";
                    texFilename = "textures/" + sanitised;

                    IAsset texAsset = capi.Assets.TryGet(loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
                    if (texAsset != null)
                    {
                        File.WriteAllBytes(Path.Combine(texturesDir, sanitised), texAsset.Data);
                        texturesExported++;
                    }
                    else
                    {
                        WorldExporterLog($"Warning: Could not load texture asset for {loc}");
                    }
                }
                else
                {
                    WorldExporterLog($"Warning: No asset location found for texture subId {subId}");
                }

                mtlWriter.WriteLine($"newmtl mat_{subId}");
                mtlWriter.WriteLine("Ka 1.000000 1.000000 1.000000");  // Ambient color
                mtlWriter.WriteLine("Kd 1.000000 1.000000 1.000000");  // Diffuse color (white so texture shows through)
                mtlWriter.WriteLine("Ks 0.000000 0.000000 0.000000");  // Specular color (no specular)
                mtlWriter.WriteLine("d 1.0");                           // Dissolve (opacity)
                mtlWriter.WriteLine("illum 2");                         // Illumination model (2 = highlight on)
                mtlWriter.WriteLine($"map_Kd {texFilename}");
                mtlWriter.WriteLine();
            }

            WorldExporterChat($"OBJ export complete. Written to {objPath} with {texturesExported} textures exported.");
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

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            bool useObj = format?.ToLower() == "obj";

            try
            {
                var task = new Task(() =>
                {
                    if (useObj)
                    {
                        string baseOutputPath = Path.Combine(config.outputDirectory, $"world_export_{timestamp}");
                        WriteToOBJ(world, start, end, baseOutputPath);
                    }
                    else
                    {
                        string outputPath = Path.Combine(config.outputDirectory, $"world_export_{timestamp}.stl");
                        WriteToSTL(world, start, end, outputPath);
                    }
                });
                task.Start();
                task.Wait(useObj ? 30000 : 10000);
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
