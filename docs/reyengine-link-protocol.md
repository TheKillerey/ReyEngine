# ReyEngine live link — protocol 4

The live link between **ReyEngine** (the map editor) and **MapgeoAddon** (its Blender add-on). Protocol 3
replaced protocol 2, which the retired single-file "ReyEngine Bridge" add-on (`reyengine_bridge.py`) spoke.
Protocol 4 (ReyEngine M842) adds the grass pivot to the mesh block, keeps every channel and several materials
through a reshape, un-shares instanced buffers instead of refusing them, and adds the navgrid, read-only
(`navgrid`). A protocol 3 add-on is told to update; the two sides refuse a mismatch either way.

This file is kept identical in both repositories:

- ReyEngine: `docs/reyengine-link-protocol.md`; code in `src/ReyEngine.Formats/Interop/BlenderBridgeProtocol.cs`,
  `BlenderLinkMaterials.cs`, `BlenderLinkRequests.cs`, `MapGeoBlenderBridge.cs` and
  `src/ReyEngine.App/Services/BlenderBridgeServer.cs`.
- MapgeoAddon: `docs/reyengine-link-protocol.md`; code in `reyengine_link_protocol.py` (wire, no bpy),
  `reyengine_link.py` (the ReyEngine panel in the LoL Mapgeo tab) and `reyengine_link_navgrid.py`.

## Roles

- **ReyEngine listens, Blender connects.** ReyEngine holds the map; Tools ▸ *Blender link (MapgeoAddon)…*
  starts the server for the open map.
- **127.0.0.1 only**, default port **47800**, never bound to anything else.
- **A session token.** Loopback is not enough on its own: a web page in the user's browser can POST to
  127.0.0.1. Each time the link starts ReyEngine makes a token (`XXXX-XXXX`, 40 bits), shows it in the
  Tools menu status line, copies it to the clipboard and offers *Copy link token for Blender*. Every hello
  must carry it; case, dashes and spaces are ignored. MapgeoAddon's ReyEngine panel has a Token field;
  left empty, *Connect* takes a token-shaped clipboard, so "start the link, press Connect" is the usual flow.
- **ReyEngine is the only writer of the `.mapgeo` and the `.materials.bin`.** Blender sends edits; ReyEngine
  applies each one through its own editors as a pending, undoable edit, and writes them with its normal
  Save (*Save Map Content Edits* for the mapgeo, the Materials tab's *Save* for the bin). Blender never
  writes a file into the ReyEngine project.

## Framing

Every message is one UTF-8 JSON object on one line, terminated by `\n`, optionally followed by exactly
`header.bytes` bytes of binary body. A reader reads the header byte by byte (a buffered reader would
swallow the start of the body). All binary numbers are little-endian; `i32` is a signed 32-bit integer,
`u32` unsigned, `f32` an IEEE float.

A request that fails is answered with `{"op":"error","message":"..."}` — never by dropping the connection,
unless the stream itself is out of step (a body shorter than declared). A body sent with an op that takes
none is read and discarded, so the stream stays in step.

A connection may carry any number of requests. MapgeoAddon opens one connection per user action.

Connection rules (they are what keeps a browser's request out):

- The **first line must be a hello** (a JSON object with `"op":"hello"`, at most 16 KB). A first line that is
  an HTTP request line (`GET `, `POST `, `OPTIONS `, …), not JSON, or not an object is answered with nothing:
  the connection is closed. A JSON object with another op gets one error and is closed. A refused hello
  (protocol, token) gets one error and is closed.
- A connection is closed when it sends nothing for 10 s before its hello, or for 2 minutes after it.
- At most 4 connections are served at once, each on its own thread, so a silent client cannot block Blender.
- A body (`bytes`) larger than 512 MB, or negative, is refused and the connection closed (it cannot be skipped).
  A body sent with an op that takes none is drained in chunks.
- A request the editor does not answer within 30 s gets an error, and the queued work is abandoned - it never
  runs late, so a client that retries cannot get it applied twice.

## Coordinates

The wire is **League space**: Y up, the numbers the `.mapgeo` holds. MapgeoAddon converts:

    League (x, y, z)  ->  Blender (x, z, y)

That swap mirrors (determinant −1), so triangle winding is reversed in both directions. Object transforms
convert as `B = C · L · C⁻¹` (C is the swap, its own inverse) — never by swapping euler components.

Each mesh is sent **relative to its pivot** (the centre of its bounding box, ReyEngine's rotate/scale
origin) with its placement separately: location (pivot + offset), XYZ euler rotation in degrees, scale.
The Blender object's origin sits on the pivot, so rotation and scale round trip exactly and pushing back
what was pulled is a no-op.

UV0 is sent as the file holds it; MapgeoAddon flips V (`v' = 1 − v`) on pull and back on push, its own
importer's convention.

## Operations

### hello

Must be the first request of every connection; anything else before it is refused.

    → {"op":"hello","protocol":4,"client":"MapgeoAddon 0.5.0","token":"K7Q2-9XMB"}
    ← {"op":"hello","protocol":4,"app":"ReyEngine","app_version":"0.5.1","map":"base_srx.mapgeo",
       "project":"My Rift","project_root":"D:\\...","map_path":"data/maps/mapgeometry/map11/base_srx.mapgeo",
       "materials_bin":"data/maps/mapgeometry/map11/base_srx.materials.bin","mesh_count":586,
       "map_read_only_reason":null,"materials_read_only_reason":null}

A missing or wrong token is refused (with a message saying where the token is). A client speaking protocol 2
(or none) is refused with a message saying the ReyEngine Bridge add-on was
replaced by MapgeoAddon. A newer protocol is refused with "update ReyEngine". A `*_read_only_reason` is
non-null when edits to that file will be refused (a read-only Riot asset that was not copied to the
project, a chunk the project's LTK GameData changes, …).

### pull

    → {"op":"pull"}
    ← {"op":"meshes","protocol":4,"count":586,"bytes":N,"notes":[...],
       "meshes":[ per-mesh info, in block order ],
       "map":"base_srx.mapgeo","map_path":"...","layers":[{"name":"Base","mask":1}, ...],
       "lightmap_color_scale":1.0}
      + N bytes: the mesh block

Per-mesh info (JSON):

| key | meaning |
|---|---|
| `i` | mesh index in the mapgeo (the id every push uses) |
| `name` | mesh name |
| `visibility` | dragon-layer bitmask (pending edits included) |
| `controller` | visibility controller path hash (the importer's "baron hash"), 0 = none |
| `render_region` | v18 render-region hash, 0 = none |
| `render_flags`, `disable_backface_culling` | as the file holds them |
| `file_materials` | each submesh's material as the FILE names it |
| `lightmap` | `{texture, scale:[u,v], bias:[u,v]}` — BakedLight channel, when the mesh has one |
| `baked_paint` | `{texture, scale, bias}` — BAKED_PAINT channel, when present |
| `stationary_light` | stationary light texture, when present |
| `texture_overrides` | `{samplerName: texturePath}` (v17+) |
| `has_lightmap_uv`, `has_vertex_color` | which channels the block carries for it |

`layers` are the map's own visibility layer names and masks (empty when the map declares none; MapgeoAddon
then uses Summoner's Rift's eight dragon layers).

Meshes carrying a session-only batch transform, or with no vertices, are left out with a note.

#### Mesh block

    i32 meshCount
    per mesh:
      i32 nameLength, u8[nameLength] name (UTF-8)
      i32 index
      f32[3] pivot, f32[3] location, f32[3] rotationDegrees, f32[3] scale
      i32 n, f32[n] positions      (xyz, pivot-relative, League axes)
      i32 n, f32[n] normals        (xyz, or n = 0)
      i32 n, f32[n] uvs            (UV0, 2 per vertex, or n = 0)
      i32 n, f32[n] lightmapUvs    (raw TEXCOORD7, 2 per vertex, before the BakedLight scale/bias, or n = 0)
      i32 n, f32[n] colors         (RGBA 0..1, 4 per vertex, or n = 0)
      i32 n, f32[n] grassPivots    (TEXCOORD5, the VertexDeform bush clump pivot: xyz, pivot-relative like
                                    positions, or n = 0)                                      [protocol 4]
      i32 n, u32[n] indices        (triangle list, into THIS mesh's vertices)
      i32 submeshCount
      per submesh:
        i32 len, u8[len] material  (the material it is drawn with, pending edits included)
        i32 startIndex, i32 indexCount   (a range of this mesh's index list)

The decoder refuses a truncated block, an index past the vertex count, a channel whose length does not match
the vertex count, a submesh range outside the index list and bytes left over after the last mesh. Degenerate
triangles (ReyEngine's deleted faces) are not sent.

MapgeoAddon builds `LightmapUV` as its importer does — `(u·su + bu, 1 − (v·sv + bv))` with the mesh's
lightmap scale/bias — and stores the raw channel in the `raw_lightmap_uv` point attribute. Vertex colour goes
into the `Color` corner attribute through `color_srgb` (the file's bytes, exactly), the grass pivot into the
`TEXCOORD5` point attribute (Blender axes, the object's own space).

### materials

    → {"op":"materials"}
    ← {"op":"materials","protocol":4,"bin":"data/.../base_srx.materials.bin","live":true,"count":191,
       "used":186,"editable":true,"read_only_reason":null,"lightmap_color_scale":1.0,
       "materials":{ "<name>": material, ... }}

Every StaticMaterialDef of the map's materials.bin (a repeated name keeps the first), read from ReyEngine's
LIVE document — unsaved edits made in ReyEngine are what Blender sees. `live` is false when ReyEngine's
Materials tab holds unsaved edits to another bin; the file is described then, and edits are refused.

A material is the dict MapgeoAddon's `project_manager.convert_bin_entry_to_material_dict` produces, so
`material_loader.create_blender_material` consumes it unchanged:

    {"__type":"StaticMaterialDef","name":"...","type":0,"shader":"Shaders/StaticMesh/DefaultEnv_Flat",
     "samplerValues":[{"textureName":"DiffuseTexture","TextureName":"DiffuseTexture",
                       "texturePath":"ASSETS/Maps/.../x.tex","texturePathType":18,
                       "link":"0x0123456789abcdef","addressU":1,"addressV":1,"addressW":1}],
     "paramValues":[{"name":"TintColor","type":"Vector4","value":[1,1,1,1]}],
     "switchValues":[{"name":"...","on":true}],"switches":{"...":true},
     "shaderMacros":{"NO_BAKED_LIGHTING":"1","REY_GRAPH":"8"},
     "techniques":[{"name":"normal","passes":[{"shader":"...","blendEnable":false,"cullEnable":true,
                    "srcColorBlendFactor":1,"dstColorBlendFactor":0,"srcAlphaBlendFactor":1,
                    "dstAlphaBlendFactor":0,"writeMask":31,"depthEnable":true}]}],
     "childTechniques":[],"blendEnable":false,"cullEnable":true,
     "rey":{"used":true,"linked":false,"linked_from":null,"editable":true,"read_only_reason":null,
            "graph":"8","alpha_cutout":false,"alpha_cutoff":null,"depth_write":true,"dirty":false}}

- `texturePath` is the readable path when ReyEngine's hash tables name the chunk, else the
  `0x` + 16 hex digit form of the link; `link` is always the link; `texturePathType` is the field's bin wire
  type (18 = WadChunkLink, 16 = string).
- An address mode absent from the bin is absent here (Riot's default, Wrap).
- `paramValues[].value` is four floats for every numeric type (scalars and vectors zero-extended); a
  non-numeric parameter has `text` instead.
- `rey.graph` is the REY_GRAPH value (the ReyEngine Shader Graph number), or null.
- `rey.editable` is false for a material defined in a linked bin (`rey.linked_from` names it), for a
  non-StaticMaterialDef, and for every material when the bin itself is read-only.

### texture

    → {"op":"texture","path":"ASSETS/Maps/.../x.tex"}      (or "0x0123456789abcdef", or "link": ...)
    ← {"op":"texture","found":true,"request":"...","path":"assets/maps/.../x.tex",
       "link":"0x...","ext":".tex","source":"game","bytes":N}  + N bytes: the file

The bytes come from ReyEngine's mounts — the project first, then the game — exactly as the editor reads them
(`.tex` or `.dds`). A `.tex` asked for and only a `.dds` present (or the reverse) is answered with the
sibling. Not found (or not a texture) is `{"found":false,"bytes":0,"message":...}`, not an error, so a batch goes on.
`source` is `project`, `game` or `gamedata`. Only `.tex` and `.dds` are served.

MapgeoAddon writes each texture into a temporary cache (`<temp>/reyengine_link/<map>/assets/<path without
assets/>`, or `raw/<hash>.<ext>` for an unnamed link) and points `material_loader` at it, so it needs no WAD
cache of its own. `<map>` is the map name plus a hash of the project root and map path, so two projects never
share a cache. A path with a `..`, a drive or an absolute component, or one that would land outside the cache,
is refused.

### push

    → {"op":"push","transforms":[{"i":12,"loc":[x,y,z],"rot":[rx,ry,rz],"scl":[sx,sy,sz],"anc":[px,py,pz]}]}
    ← {"op":"ok","applied":586,"detail":"1 applied, 0 refused"}

Absolute placements (pushing the same one twice moves nothing twice). `anc` echoes the pivot the object was
given, so editing faces (which moves the bbox centre) never makes an untouched object jump. Unchanged
placements are skipped; non-finite or zero-scale transforms are refused. Applied immediately to the editor's
scene; written by *Save Map Content Edits*.

### push_geometry

    → {"op":"push_geometry","bytes":N}  + N bytes: a mesh block (placement fields ignored)
    ← {"op":"ok","applied":1,"refused":[{"index":1,"what":"MapGeo_Instance_7","reason":"..."}],
       "detail":"1 queued, 1 refused"}

Reshaped meshes, pivot-relative world space, League axes. `refused[].index` is the mesh's position in the
block; MapgeoAddon re-baselines only the queued ones and lists the refused ones in its panel. ReyEngine
converts positions, normals and grass pivots to mesh-local space and queues the reshape; Save Map Content
Edits writes it:

- **Channels.** Position, normal, UV0, raw TEXCOORD7, PRIMARY_COLOR and TEXCOORD5 are written from what was
  sent, in the mesh's own vertex declaration (float formats and the packed 8888 colours). A channel the push
  did not carry (or a packed format the writer does not produce, e.g. tangents) is zeroed and logged.
- **Several materials.** The submesh table (one range per material, tiling the index list) becomes one
  submesh per material; a submesh of a material the mesh already had keeps its hash. Every material must be
  one of the map's. A mesh with several materials and NO table is refused.
- **Shared buffers (Riot instancing).** The reshaped mesh gets its OWN new vertex and/or index buffers; every
  other mesh that shared them keeps the original buffers byte for byte; nothing is left unreferenced (LTK's
  loader rule). The declaration (stride, layout) stays the mesh's own.
- At most 65,536 vertices (16-bit index buffers). Saving a reshape rebuilds the map's bucket grids, which
  grows the file (measured on base_srx: scene graphs 6.6 MB -> 33.3 MB).

MapgeoAddon sends a mesh when its shape changed - coordinates, topology OR which face uses which material
slot - or it carries a modifier. Splitting per corner follows UV0 and the corner colour. A face on a slot whose
material is not a ReyEngine material is refused in Blender, with the reason.

### assign_material

    → {"op":"assign_material","assignments":[{"mesh":300,"submesh":0,"material":"Maps/.../Rock_MAT"},
                                             {"mesh":12,"material":"..."}]}
    ← {"op":"ok","applied":1,"refused":[{"index":1,"what":"MapGeo_Instance_12","reason":"..."}],
       "unreadable":0,"detail":"1 applied, 1 refused"}

Draw submesh `submesh` of mesh `mesh` with an EXISTING material of the map's materials.bin. `submesh`
absent or −1 = every submesh. Applied as one pending, undoable edit per mesh: the whole-mesh material edit
(the inspector's material picker) when all submeshes end up on one material, otherwise per-submesh edits.
Written into the mapgeo by *Save Map Content Edits*. Refused: an unknown mesh, a submesh out of range, a
material the bin does not define, a read-only map.

MapgeoAddon detects an assignment as a material slot whose material is now another pulled ReyEngine
material (`league_material_name`).

### edit_material

    → {"op":"edit_material","edits":[
         {"material":"M","kind":"param","name":"TintColor","value":[1,0.5,1,1]},
         {"material":"M","kind":"sampler","name":"DiffuseTexture","path":"ASSETS/.../y.tex"},
         {"material":"M","kind":"address","name":"DiffuseTexture","u":1,"v":1,"w":null},
         {"material":"M","kind":"switch","name":"...","on":false},
         {"material":"M","kind":"macro","name":"NO_BAKED_LIGHTING","value":"1"},
         {"material":"M","kind":"macro","name":"REY_GRAPH","value":"8"},
         {"material":"M","kind":"macro","name":"DISABLE_DEPTH_FOG","value":null}]}
    ← {"op":"ok","applied":6,"refused":[],"unreadable":0,"detail":"6 applied, 1 unchanged, 0 refused"}

Each edit goes through ReyEngine's Materials tab commands — one undo step each, the material turns dirty,
the tab's Save writes the bin. An edit that sets the value already there is "unchanged": neither applied nor
refused. A texture path neither the project nor the game holds is refused before it is written; a sampler
keeps its wire type (a WadChunkLink stays one). A row with a value typed in ReyEngine and not applied is
refused rather than overwritten, and the Materials tab is not switched to the map's bin while it has one.

| kind | effect |
|---|---|
| `param` | set an existing parameter; `value` is 1–4 numbers (only as many as the parameter has are used) or a text value |
| `sampler` | set an existing sampler's texture path (a path or a `0x` link) |
| `address` | set address modes; `u`/`v`/`w` each 0 Wrap, 1 Clamp, 2 Mirror, null = not set |
| `switch` | turn an existing switch on or off |
| `macro` | set a shaderMacros define; `value: null` removes it (absent is not "0"). `REY_GRAPH` takes a Shader Graph number and goes through ReyEngine's graph assignment (which also adds/removes the graph's custom parameters and textures). A value the game ships no cooked shader permutation for is refused — it would make the map fail to load. |

Refused with the reason: a material that is linked from another bin or not a StaticMaterialDef, a read-only
bin, a parameter/sampler/switch the material does not have (adding and removing fields is not part of
protocol 3 — do it in ReyEngine), an unparsable value.

MapgeoAddon derives these edits by diffing the JSON its material editor keeps on each material
(`parameters`, `samplers`, `switches`, `shader_macros`) against a snapshot taken at pull. Only a material whose
edits were all taken is re-baselined; one with a refused edit sends it again on the next push.

### navgrid (protocol 4, read-only)

    → {"op":"navgrid"}
    ← {"op":"navgrid","found":true,"path":"assets/maps/navgrid/map11/aipath.aimesh_ngrid","source":"project",
       "version":"7.x","min":[x,y,z],"max":[x,y,z],"cell_size":50,"count_x":N,"count_z":M,"has_heights":true,
       "lift":12,
       "layers":[{"mask":2,"label":"not walkable   (bit 1)","color":[r,g,b,a],"cells":34079,
                  "visible":false,"default_visible":true}, ...],
       "bytes":N*M*6}
      + body: f32 height per cell, then u16 flags per cell (row-major, x fastest)

The navgrid as ReyEngine reads it, and ReyEngine's navgrid overlay layers: one per single-bit flag present,
commonest first, with ReyEngine's label (`NavGrid.LabelFor`; an unidentified bit says so), its colour and
alpha, its cell count, whether ReyEngine's overlay shows it right now (`visible`) and what ReyEngine shows when
its overlay is switched on with no layer chosen (`default_visible`: the commonest layer). `lift` is how far
above a cell's ground height ReyEngine draws it. `found:false` with a `message` when the map has none (or it
is not a version 7 grid).

The link only SHOWS the navgrid; there is no op that changes it. MapgeoAddon builds one object per layer in a
`<map>_NavGrid` collection - each cell as two triangles at its height + `lift`, drawn as a thin wireframe in
the layer colour and alpha, in front of the map (ReyEngine draws the overlay without depth testing), not
selectable. Its panel has "Show NavGrid" (ReyEngine's overlay switch: on with no layer chosen shows the
commonest) and one switch per layer. A map pull mirrors what ReyEngine's overlay shows at that moment; Pull
NavGrid shows it.

## Installing MapgeoAddon from ReyEngine

Settings ▸ Blender installs from a folder, a `.zip` or an https URL (empty = the latest release of
github.com/TheKillerey/MapgeoAddon; only a `.zip` asset of that repository's release is taken). Downloads are
https only, redirects included, at most 200 MB; a zip at most 5,000 entries and 400 MB unpacked, nothing
outside its folder. The copy goes to `scripts\addons\MapgeoAddon`; an older copy is renamed aside and restored
if the new one cannot be moved in. **A MapgeoAddon folder that is a git working copy or a junction/symbolic
link is left alone** (a developer's checkout), and the row says so.

When the matching blender.exe is known, the installer runs it in the background
(`blender -b --python-expr ...`), which **disables `reyengine_bridge`, enables `MapgeoAddon` and saves
Blender's user preferences**; then it deletes `reyengine_bridge.py` and its compiled cache.

## Known limits

- A Shader Graph (REY_GRAPH) material arrives as its base shader (e.g. DefaultEnv_Flat): MapgeoAddon's
  node builders know Riot's shaders, not ReyEngine's graphs. `rey.graph` says which graph it is.
- Render state (blend, cull, write mask) is described but not editable over the link.
- Adding or removing parameters, samplers or switches, creating materials and changing a material's shader
  are not part of protocols 3 and 4.
- Navgrid: version 7 only, shown read-only (flags and heights; the region layers and the rest of each cell
  record are not sent). Editing it is not part of the link.
- A reshape cannot write tangents (TEXCOORD6) or packed-format UVs; they are zeroed and logged.
- Transforms pushed from Blender are applied without their own undo step (as in protocol 2); assignments and
  material edits are undoable.
