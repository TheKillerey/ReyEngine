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

Pushed placements land in the editor immediately — the viewport updates as they arrive. They are **not**
written to the file until you use **Save Map Content Edits**, exactly like a gizmo move.

## What crosses, and what does not

**Geometry and placement.** Vertices, normals and triangles go out; position, rotation and scale come
back.

**Materials do not cross, on purpose.** They live in the map's companion `.bin`, bound to submesh ranges
and to shader state Blender has no way to represent. A round trip through an `.blend` would lose them.
Assign materials in ReyEngine.

**Editing a mesh's shape in Blender does not come back yet.** This version syncs placement only. Change
a vertex in Blender and the push will move the object correctly but leave its shape as it was in the
editor. Reshaping is the next piece of work — see *Limits* below.

## How it lines up

The wire is **League space** (Y up, the numbers the `.mapgeo` holds). The add-on converts to Blender's
Z-up on its side, as a change of basis:

```
League (x, y, z)  ->  Blender (x, -z, y)
```

Transforms convert as `B = C @ L @ C⁻¹` rather than by swapping euler components — component swapping is
only correct for axis-aligned rotations and silently wrong for everything else.

Each object's origin sits on the ReyEngine mesh's **pivot**. That is what makes rotation and scale round
trip exactly: ReyEngine rotates a mesh about its pivot and Blender rotates about the object origin, so the
two are the same operation. Pushing back what you just pulled is a no-op, not a small drift.

Placements are **absolute**, never incremental, so pushing the same transform twice does not move
anything twice.

## Limits

- **Shape edits do not return.** Placement only, this version.
- **A mesh with a batch transform is skipped**, with a note. A multi-select move applies a second matrix
  after the mesh's own, and the bridge has nowhere to put it. Save the map and reload it, and those
  meshes send normally.
- **The link is loopback only** and has no authentication. It hands out a map's whole geometry and
  accepts edits to it, so it is bound to `127.0.0.1` deliberately — do not make it reachable.
- **Blender's viewport clips at 1,000 units** by default and a League map is ~15,000 across. Pull raises
  the clip distance on the open 3D views for you; a view opened afterwards may need it set by hand.
