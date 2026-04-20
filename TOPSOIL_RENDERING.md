# Vintage Story Top Soil Rendering System

## Overview

The TopSoil rendering system allows blocks like grass-covered soil to display a semi-transparent grass overlay on top of an opaque soil base. This creates realistic grass that can be climate-tinted while maintaining the underlying soil texture.

## Block Configuration

From `/opt/vintagestory/assets/survival/blocktypes/soil/soil.json`:

```json
{
  "drawtypeByType": {
    "*-none": "Cube",
    "*": "TopSoil"
  },
  "renderpassByType": {
    "*-none": "Opaque",
    "*": "TopSoil"
  },
  "textures": {
    "all": { "base": "block/soil/fert{fertility}" },
    "snowed": { "base": "block/plant/grasscoverage/snow/normal" },
    "specialSecondTexture": { "base": "block/plant/grasscoverage/{grasscoverage}" }
  }
}
```

Key properties:
- `drawtype: "TopSoil"` - Uses the TopSoil tesselator (index 14)
- `renderpass: "TopSoil"` - Uses dedicated TopSoil render pass (enum value 5)
- Three texture types:
  - `all`: Base soil texture
  - `snowed`: Snow overlay (used when block above has snow)
  - `specialSecondTexture`: Grass overlay texture

## Texture Atlas Layout

The grass texture (`specialSecondTexture`) is stored in the texture atlas with a special layout:
- **Left half**: Side grass texture (used for vertical faces)
- **Right half**: Top grass texture (used for horizontal top face)
- Both halves are placed horizontally adjacent in the atlas
- The spacing is exactly one block texture width (`blockTextureSize.x`)

## Mesh Generation

### TopsoilTesselator (`/home/adam/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/TopsoilTesselator.cs`)

The mesh generation process:

1. **Texture Selection**:
   - Primary texture: Standard block face textures from `fastBlockTextureSubidsByFace[0-5]`
   - Secondary texture: Grass overlay from `fastBlockTextureSubidsByFace[6]` (the specialSecondTexture)
   - If block above has snow, use "snowed" texture instead of grass

2. **Face Iteration**:
   - Only renders visible faces (determined by `drawFaceFlags`)
   - Top face can be rotated 0-3 based on position hash: `MurmurHash3Mod(x, y, z, 4)`

3. **Vertex Data** (per quad):
   - 4 vertices with XYZ positions
   - Primary UV coordinates (`uv`) - point to soil texture in atlas
   - Secondary UV coordinates (`uv2`) - stored in `CustomShorts` as packed values
   - Color map data for climate/seasonal tinting
   - Lighting data
   - Normal encoded in render flags

### UV2 Packing

From `/home/adam/vintagestorysource/vsapi/Client/Model/Mesh/CustomMeshDataPartShort.cs`:

```csharp
public void AddPackedUV(float u, float v, bool isU2, bool isV2)
{
    Add((short)(ushort)((int)(u * 0x8000 + 0.5f) + (isU2 ? 1 : 0)));
    Add((short)(ushort)((int)(v * 0x8000 + 0.5f) + (isV2 ? 1 : 0)));
}
```

Key points:
- UV values are multiplied by 0x8000 (32768) for precision
- Provides enough resolution for 32-pixel boundaries in texture atlas
- The LSB of each packed short stores a boolean flag (`isU2` or `isV2`)
- These flags are used for subpixel padding adjustments

### UV2 Coordinate Calculation

From `TopsoilTesselator.cs` lines 76-106:

```csharp
float x = textureAtlasPosition2.x1;  // Left edge of grass texture
float u = textureAtlasPosition2.x1 + (textureAtlasPosition2.x2 - textureAtlasPosition2.x1) / 2f;  // Middle
float y = textureAtlasPosition2.y1;  // Top edge
float y2 = textureAtlasPosition2.y2; // Bottom edge
```

The UV2 coordinates use these base values:
- `x` (u-coordinate for left half): Points to side grass texture
- `u` (u-coordinate for right half): Points to middle of texture (boundary between side/top)
- `y` and `y2`: Standard top/bottom v-coordinates

The `isU2` flag indicates whether to use left half (false) or right half (true) of the texture.

## Shader Processing

### Vertex Shader (`/opt/vintagestory/assets/game/shaders/chunktopsoil.vsh`)

Line 88-90:
```glsl
uv = uvIn;  // Primary UV for soil texture
uv2 = uv2In * 2.0 - vec2(
    (int(uv2In.x * 0x10000) & 1) * (uvEpsilon + subpixelPaddingX * 2.0),
    (int(uv2In.y * 0x10000) & 1) * (uvEpsilon + subpixelPaddingY * 2.0)
);
```

UV2 unpacking process:
1. Multiply packed value by 2.0
2. Extract LSB flag by: `int(uv2In.x * 0x10000) & 1`
   - 0x10000 = 65536 = 2 * 0x8000 (the packing multiplier)
3. If flag is set, subtract subpixel padding

### Fragment Shader (`/opt/vintagestory/assets/game/shaders/chunktopsoil.fsh`)

Lines 41-50:
```glsl
vec4 brownSoilColor = texture(terrainTex, uv) * rgba;

if (normal.y >= 0) {
    // Top (normal.y == 1) or Sides (normal.y == 0)
    vec4 grassColor = getColorMapped(terrainTexLinear,
        texture(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0))) * rgba;
    outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
} else {
    // Bottom
    outColor = applyFog(brownSoilColor, fogAmount);
}
```

Rendering logic:
1. Sample soil texture using `uv`
2. For top and side faces (normal.y >= 0):
   - Sample grass texture using `uv2 + offset`
   - **Side faces** (normal.y = 0): offset = (0, 0) → left half of texture
   - **Top face** (normal.y = 1): offset = (blockTextureSize.x, 0) → right half of texture
   - Blend grass over soil using grass alpha channel
3. For bottom face (normal.y < 0):
   - Use only soil texture, no grass overlay

## OBJ Export Challenges

### Problem 1: Normal-Based Texture Selection

The shader uses the face normal to decide which texture to use (side vs top grass). This is a runtime decision in the shader, but OBJ files need static UV coordinates per vertex.

**Solution**: Extract normals from `mesh.Flags` array using `VertexFlags.UnpackNormal()` to determine face orientation.

### Problem 2: Packed UV2 Format

The current exporter (`OBJExporter.cs` lines 119-140) incorrectly unpacks UV2:

```csharp
// INCORRECT - doesn't account for shader transformations
float u = mesh.CustomShorts.Values[i * 2] / 32768f;
float v = 1.0f - (mesh.CustomShorts.Values[i * 2 + 1] / 32768f);
```

This misses:
1. Extracting and handling the isU2/isV2 flags from LSB
2. Applying the `* 2.0` scaling from the shader
3. Subtracting the padding based on flags
4. Adding the `blockTextureSize.x` offset for top faces

**Note**: For baked texture approach, UV2 coordinates are only used to determine which grass texture half to bake - final OBJ uses simple 0-1 UVs on the baked texture.

### Problem 3: Shared Vertices

In the mesh data, vertices may be shared between faces with different normals. In OBJ, UV coordinates are per-vertex, so vertices would need to be duplicated to have different UVs for different faces.

**Solution**: Duplicate vertices per-face in the OBJ export so each triangle gets unique vertex entries with correct UVs.

## Normal Extraction from MeshData

**IMPORTANT**: `mesh.Normals` is NOT used by the game engine!

From the API documentation:
```csharp
/// <summary>
/// The normals buffer. This should hold VerticesCount*1 values. Currently unused by the engine.
/// GL_INT_2_10_10_10_REV Format
/// </summary>
public int[] Normals;  // Usually null or empty
```

**Actual normal storage**: Normals are encoded in the `mesh.Flags` array as packed 12-bit values.

To extract normals:
```csharp
if (mesh.Flags != null && vertexIndex < mesh.Flags.Length)
{
    float[] normal = new float[3];
    VertexFlags.UnpackNormal(mesh.Flags[vertexIndex], normal);
    float nx = normal[0];
    float ny = normal[1];  // Use this to detect top/side/bottom faces
    float nz = normal[2];
}
```

Normal values:
- Top face: `(0, 1, 0)`
- Bottom face: `(0, -1, 0)`
- Side faces: `(±1, 0, 0)` or `(0, 0, ±1)`

## Correct UV2 Decoding Algorithm

To properly decode UV2 for OBJ export:

```csharp
// Unpack the short value
short packedU = mesh.CustomShorts.Values[i * 2];
short packedV = mesh.CustomShorts.Values[i * 2 + 1];

// Extract flag from LSB
bool isU2 = (packedU & 1) == 1;
bool isV2 = (packedV & 1) == 1;

// Convert to float (remove flag first)
float u = (packedU - (isU2 ? 1 : 0)) / 32768.0f;
float v = (packedV - (isV2 ? 1 : 0)) / 32768.0f;

// Apply shader transformation (* 2.0 and subtract padding)
// Note: subpixelPaddingX/Y would need to be obtained from the game
float uvEpsilon = 1.0f / 32768.0f;
u = u * 2.0f - (isU2 ? (uvEpsilon + subpixelPaddingX * 2.0f) : 0);
v = v * 2.0f - (isV2 ? (uvEpsilon + subpixelPaddingY * 2.0f) : 0);

// For top faces, add offset to get top grass texture
if (faceNormal.Y == 1.0f) {
    u += blockTextureSize.x;  // Shift from left half to right half
}

// For bottom faces, don't use UV2 at all - use primary UV instead
if (faceNormal.Y < 0) {
    // Use mesh.Uv instead of UV2
}

// Flip V for OBJ coordinate system
v = 1.0f - v;
```

## MeshData Structure - TopSoil Specifics

**Critical Finding**: TopSoil meshes do NOT populate the TextureIds/TextureIndices arrays:
```csharp
mesh.TextureIds.Length = 0       // Empty array
mesh.TextureIndicesCount = 0     // No texture index data
```

This is because TopSoil uses a custom tesselator that directly writes UV coordinates from TextureAtlasPosition, rather than using the texture ID indirection system.

**Texture Information Access**:
- Cannot get texture IDs from `mesh.TextureIds` (it's empty)
- Must determine texture IDs by:
  1. Analyzing the UV coordinates to find which atlas region they point to
  2. Reverse-lookup from TextureAtlasPosition array
  3. OR track texture IDs during mesh collection phase from block properties

## Required Information for Fix

To fix the OBJ exporter, we need:

1. **Block texture size** (`blockTextureSize.x`) - Available via `capi.Render.TextureSize / capi.BlockTextureAtlas.Size.Width`
2. **Subpixel padding values** - Available via `capi.BlockTextureAtlas.SubPixelPaddingX/Y`
3. **Per-face normals** - Extract from `mesh.Flags` using `VertexFlags.UnpackNormal()`
4. **Texture atlas positions** - Access via `capi.BlockTextureAtlas.Positions[textureSubId]`

## Implementation Status

### What Works
- Normal extraction from `mesh.Flags` array
- Detection of top/side/bottom faces based on normal.Y
- Texture baking (blending soil + grass textures)
- Baking separate textures for top vs side grass
- UV coordinate extraction and bounds checking

### Known Issues
1. **Incorrect texture baking**: Baked textures are wrong - not showing correct soil+grass blend
2. **Texture ID resolution**: Cannot determine which soil/grass textures to use (TextureIds array is empty)
3. **UV remapping**: Need to verify UV coordinates map correctly to baked texture regions

### Next Steps
1. Fix texture extraction from atlas (verify correct regions being extracted)
2. Implement texture ID tracking from block data or UV reverse-lookup
3. Verify alpha blending formula matches shader behavior
4. Test with actual grass blocks to validate output

## References

- Shader files: `/opt/vintagestory/assets/game/shaders/chunktopsoil.{vsh,fsh}`
- Block definition: `/opt/vintagestory/assets/survival/blocktypes/soil/soil.json`
- Tesselator: `~/vintagestorysource/vintagestorylib_decompiled/Vintagestory.Client.NoObf/TopsoilTesselator.cs`
- UV packing: `~/vintagestorysource/vsapi/Client/Model/Mesh/CustomMeshDataPartShort.cs`
- Render pass enum: `~/vintagestorysource/vsapi/Client/Render/EnumChunkRenderPass.cs`
- Normal unpacking: `~/vintagestorysource/vsapi/Common/Collectible/Block/VertexFlags.cs`
- MeshData structure: `~/vintagestorysource/vsapi/Client/Model/Mesh/MeshData.cs`
