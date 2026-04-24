# WorldExporter Project - Important Paths

## Project Structure

### Main Project
- **Project Root**: `/home/adam/develop/vsworldexporter/`
- **Build Output**: `/home/adam/develop/vsworldexporter/bin/Debug/Mods/WorldExporter/`
- **Export Directory**: `/home/adam/vsworldexports/`

### Source Files
- **OBJ Exporter**: `/home/adam/develop/vsworldexporter/Exporters/OBJExporter.cs`
- **STL Exporter**: `/home/adam/develop/vsworldexporter/Exporters/STLExporter.cs`
- **Chunk Mesh Collector**: `/home/adam/develop/vsworldexporter/ChunkMeshCollector.cs`
- **TopSoil Texture Baker**: `/home/adam/develop/vsworldexporter/TopSoilTextureBaker.cs`
- **TopSoil Mesh Processor**: `/home/adam/develop/vsworldexporter/TopSoilMeshProcessor.cs`
- **Mod System**: `/home/adam/develop/vsworldexporter/WorldExporterModSystem.cs`

### Documentation
- **TopSoil Rendering Doc**: `/home/adam/develop/vsworldexporter/TOPSOIL_RENDERING.md`
- **Implementation Plan**: `/home/adam/.claude/plans/squishy-twirling-comet.md`

## Vintage Story Paths

### Game Installation
- **Game Root**: `/opt/vintagestory/`
- **Game Assets**: `/opt/vintagestory/assets/`
- **Game Shaders**: `/opt/vintagestory/assets/game/shaders/`
- **Survival Assets**: `/opt/vintagestory/assets/survival/`
- **Creative Assets**: `/opt/vintagestory/assets/creative/`

### Source Code
- **Source Root**: `~/vintagestorysource/`
- **VS API**: `~/vintagestorysource/vsapi/`
- **Decompiled Engine**: `~/vintagestorysource/vintagestorylib_decompiled/`
- **Survival Mod**: `~/vintagestorysource/vssurvivalmod/`
- **Creative Mod**: `~/vintagestorysource/vscreativemod/`
- **Essentials Mod**: `~/vintagestorysource/vsessentialsmod/`

### Game Data
- **Config Directory**: `/home/adam/.config/VintagestoryData/`
- **Logs**: `/home/adam/.config/VintagestoryData/Logs/`
  - `client-main.log` - Main client log
  - `client-debug.log` - Debug messages
  - `client-chat.log` - Chat and WorldExporter logs
  - `client-crash.log` - Crash reports
  - `server-main.log` - Server log
  - `server-debug.log` - Server debug

## TopSoil Rendering - Key Files

### Shaders
- **Vertex Shader**: `/opt/vintagestory/assets/game/shaders/chunktopsoil.vsh`
- **Fragment Shader**: `/opt/vintagestory/assets/game/shaders/chunktopsoil.fsh`

### Block Definitions
- **Soil Block**: `/opt/vintagestory/assets/survival/blocktypes/soil/soil.json`
- **Grass Textures**: `/opt/vintagestory/assets/survival/textures/block/plant/grasscoverage/`

### Source Code References
- **TopSoil Tesselator**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/TopsoilTesselator.cs`
- **Chunk Tesselator**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/ChunkTesselator.cs`
- **Chunk Renderer**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/ChunkRenderer.cs`

### API References
- **MeshData**: `~/vintagestorysource/vsapi/Client/Model/Mesh/MeshData.cs`
- **CustomMeshDataPartShort**: `~/vintagestorysource/vsapi/Client/Model/Mesh/CustomMeshDataPartShort.cs`
- **TextureAtlasPosition**: `~/vintagestorysource/vsapi/Client/Texture/TextureAtlasPosition.cs`
- **VertexFlags**: `~/vintagestorysource/vsapi/Common/Collectible/Block/VertexFlags.cs`
- **BlockFacing**: `~/vintagestorysource/vsapi/Math/BlockFacing.cs`
- **ITextureAtlasAPI**: `~/vintagestorysource/vsapi/Client/API/ITextureAtlasAPI.cs`
- **IRenderAPI**: `~/vintagestorysource/vsapi/Client/API/IRenderAPI.cs`
- **EnumChunkRenderPass**: `~/vintagestorysource/vsapi/Client/Render/EnumChunkRenderPass.cs`

### Block Implementation
- **BlockWithGrassOverlay**: `~/vintagestorysource/vssurvivalmod/Block/BlockWithGrassOverlay.cs`
- **BlockSoil**: `~/vintagestorysource/vssurvivalmod/Block/BlockSoil.cs`
- **BlockForestFloor**: `~/vintagestorysource/vssurvivalmod/Block/BlockForestFloor.cs`

### Texture Atlas Management
- **TextureAtlasManager**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/TextureAtlasManager.cs`
- **BlockTextureAtlasManager**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/BlockTextureAtlasManager.cs`
- **ClientSettings**: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/ClientSettings.cs`

## Build Commands

### Build Project
```bash
cd /home/adam/develop/vsworldexporter
dotnet build
```

### View Recent Export
```bash
ls -lah /home/adam/vsworldexports/$(ls -t /home/adam/vsworldexports/ | head -1)/
```

### Check Logs
```bash
tail -100 /home/adam/.config/VintagestoryData/Logs/client-chat.log
grep "WorldExporter" /home/adam/.config/VintagestoryData/Logs/client-chat.log | tail -50
```

## Quick Reference

### Texture ID System
- **TextureSubId** → Index into `capi.BlockTextureAtlas.Positions[]`
- **TextureAtlasPosition** → Contains UV bounds (x1, y1, x2, y2) and atlas number
- **Atlas Texture** → GPU texture accessible via `capi.BlockTextureAtlas.AtlasTextures[atlasNumber]`

### Normal Extraction
```csharp
// Normals are in mesh.Flags, NOT mesh.Normals (which is unused)
float[] normal = new float[3];
VertexFlags.UnpackNormal(mesh.Flags[vertexIndex], normal);
// normal[0] = X, normal[1] = Y, normal[2] = Z
```

### Important Values
- **Texture Size**: `capi.Render.TextureSize` (typically 32 pixels)
- **Atlas Size**: `capi.BlockTextureAtlas.Size` (e.g., 4096x2048)
- **Block Texture Size (UV)**: `texturePixelSize / atlasSize.Width`
- **Subpixel Padding**: `capi.BlockTextureAtlas.SubPixelPaddingX/Y`
