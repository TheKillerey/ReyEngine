# ReyEngine ⇄ Blender bridge

Edit a League map's meshes in Blender and push the result back into the editor.

## Install

1. In ReyEngine: **Tools ▸ Start Blender Link…** (a map has to be open — otherwise there is nothing to
   serve). The menu below it shows the port it is listening on.
2. In Blender: **Edit ▸ Preferences ▸ Add-ons ▸ Install…**, pick `reyengine_bridge.py`, tick it.
3. In the 3D view press <kbd>N</kbd> and open the **ReyEngine** tab.

## Use

| Button | What it does |
|---|---|
| **Pull Map From ReyEngine** | Fills a `ReyEngine Map` collection with one object per map mesh |
| **Push All** | Sends every pulled object's position, rotation and scale back |
| **Selected** | The same, for the current Blender selection only |
| **Push Shapes** | Sends the selected objects' edited GEOMETRY back |
| **All Changed** | The same, for every mesh whose shape actually differs |

Pushed placements land in the editor immediately — the viewport updates as they arrive. They are **not**
written to the file until you use **Save Map Content Edits**, exactly like a gizmo move.

## What crosses, and what does not

**Geometry and placement.** Vertices, normals and triangles go out; position, rotation and scale come
back.

**Materials do not cross, on purpose.** They live in the map's companion `.bin`, bound to submesh ranges
and to shader state Blender has no way to represent. A round trip through an `.blend` would lose them.
Assign materials in ReyEngine.

**Shape edits come back too.** Push Shapes sends the mesh's evaluated geometry — modifiers included, so
a subdivision surface you added is part of what arrives. UV0 travels both ways, so a reshaped mesh keeps
its texturing.

Reshapes are **queued, not live**: they rewrite the mesh's vertex and index buffers, which is a change to
the file rather than to the scene in memory. The viewport keeps showing the old shape until you **Save Map
Content Edits** and reload. Placements, by contrast, appear immediately.

## How it lines up

The wire is **League space** (Y up, the numbers the `.mapgeo` holds). The add-on converts to Blender's
Z-up on its side, as a change of basis:

```
League (x, y, z)  ->  Blender (x, z, y)      (swap the ground axes; height stays height)
```

Transforms convert as `B = C @ L @ C⁻¹` rather than by swapping euler components — component swapping is
only correct for axis-aligned rotations and silently wrong for everything else.

Each object's origin sits on the ReyEngine mesh's **pivot**. That is what makes rotation and scale round
trip exactly: ReyEngine rotates a mesh about its pivot and Blender rotates about the object origin, so the
two are the same operation. Pushing back what you just pulled is a no-op, not a small drift.

Placements are **absolute**, never incremental, so pushing the same transform twice does not move
anything twice.

## Limits

- **A mesh with more than one material is refused for reshaping.** Blender does not send materials, so a
  new triangle cannot be attributed to one of several. Measured on shipped maps, 87%–100% of meshes have
  a single material, so this bites rarely — and guessing would be worse.
- **A mesh whose buffers are shared with another mesh is refused.** Resizing one would redefine the other.
- **65,536 vertices per mesh**, because mapgeo index buffers are 16-bit. Split the mesh and send the parts.
- **Reshaping loses the mesh's baked lightmap UVs.** They belong to vertices that may no longer exist, and
  a changed shape invalidates the bake anyway. Re-bake after reshaping. This is why **All Changed** sends
  only what differs — replacing an untouched mesh would re-number its vertices and drop its lightmap UVs
  for nothing. A mesh carrying a modifier always counts as changed, since its result only exists once
  evaluated.
- **Saving a reshape rebuilds the map's bucket grids.** The grid holds its own baked copy of the map and
  the game culls against that copy, so a changed shape with a stale grid makes meshes and decals blink out
  as the camera turns. The rebuild is automatic; it is why a save after reshaping takes a moment.
- **Do not mix a reshape and face edits in one save.** A reshape replaces a mesh's triangles, so pending
  face edits no longer refer to the same ones; the editor refuses the save rather than applying them to
  whatever now holds those indices.
- **A mesh with a batch transform is skipped**, with a note. A multi-select move applies a second matrix
  after the mesh's own, and the bridge has nowhere to put it. Save the map and reload it, and those
  meshes send normally.
- **The link is loopback only** and has no authentication. It hands out a map's whole geometry and
  accepts edits to it, so it is bound to `127.0.0.1` deliberately — do not make it reachable.
- **A full pull of a big map takes a while.** ~1,400 objects and ~900,000 vertices is real work for
  Blender; the progress bar moves while it runs. If you only need part of the map, it is far quicker to
  pull once and keep the .blend than to re-pull.
- **Blender's viewport clips at 1,000 units** by default and a League map is ~15,000 across. Pull raises
  the clip distance on the open 3D views for you; a view opened afterwards may need it set by hand.
