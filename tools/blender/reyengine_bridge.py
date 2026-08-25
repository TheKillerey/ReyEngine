"""ReyEngine bridge — edit a League map's meshes in Blender.

Install: Blender ▸ Edit ▸ Preferences ▸ Add-ons ▸ Install… ▸ pick this file ▸ tick it.
Use:     ReyEngine ▸ Tools ▸ Blender Link ▸ Start, then the ReyEngine tab in the 3D view sidebar (N).

Geometry only. Materials stay in ReyEngine — they live in the map's companion .bin, bound to submesh
ranges and shader state that has no representation here, and a round trip through Blender would lose them.

Coordinates
-----------
The wire is League space: Y up, the same numbers the .mapgeo holds. This file owns the conversion, in
one place, as a change of basis:

    League (x, y, z)  ->  Blender (x, z, y)      (swap the two ground axes; height stays height)

League reads +X to the right and +Z up the minimap - checked against the river emitters Riot ships in
Map453, which run the anti-diagonal from high-X/low-Z to low-X/high-Z. So League +Z has to become Blender
+Y, or the map arrives upside down in the top view.

That swap MIRRORS (its determinant is -1), which is unavoidable when the two conventions disagree about
which way the ground plane runs. Triangle winding is therefore reversed in both directions to compensate:
a mirror flips a face's normal and reversing its winding flips it back, so surfaces still face outward.

Transforms are converted as B = C @ L @ C^-1 rather than by swapping euler components, because swapping
components is only correct for axis-aligned rotations and silently wrong for everything else. The
similarity form cancels the two determinants, so the object transform never picks up a mirror of its own.

Each object's origin sits on the ReyEngine mesh's PIVOT, which is what makes rotation and scale round
trip exactly: ReyEngine rotates a mesh about that pivot, and Blender rotates about the object origin, so
the two are the same operation rather than two operations that have to be reconciled.
"""

bl_info = {
    "name": "ReyEngine Bridge",
    "author": "ReyEngine",
    "version": (1, 0, 0),
    "blender": (3, 6, 0),
    "location": "View3D ▸ Sidebar ▸ ReyEngine",
    "description": "Pull a League map's meshes from ReyEngine, edit them here, push placements back.",
    "category": "Import-Export",
}

import json
import socket
import struct
import zlib

import bpy
from bpy.props import IntProperty, StringProperty, BoolProperty
from mathutils import Euler, Matrix, Vector

PROTOCOL = 2
COLLECTION = "ReyEngine Map"
INDEX_KEY = "rey_index"
FINGERPRINT_KEY = "rey_shape"

# League -> Blender basis: swap the two ground axes and leave height alone. Determinant -1, so it is a
# mirror; winding is reversed alongside it (see _reverse_winding). The matrix is its own inverse, which
# is why the same one serves both directions.
L2B = Matrix(((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0))).to_4x4()
B2L = L2B


def _fingerprint(mesh):
    """A cheap signature of a mesh's shape: its coordinates and its topology.

    Used to send only what actually changed. Pushing every mesh REPLACES every mesh - each one is
    re-triangulated, its vertices are re-numbered, and its baked lightmap UVs are dropped because they
    belong to vertices that no longer exist. Doing that to meshes the user never touched is destructive,
    so an untouched mesh has to be recognisable rather than assumed.
    """
    co = [0.0] * (len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    loops = [0] * len(mesh.loops)
    mesh.loops.foreach_get("vertex_index", loops)
    digest = zlib.crc32(struct.pack("<%df" % len(co), *co))
    return zlib.crc32(struct.pack("<%dI" % len(loops), *loops), digest)


def _has_changed(obj):
    """Whether this object's shape differs from what ReyEngine sent.

    A modifier is treated as a change without further checking: its result only exists once evaluated,
    and evaluating every mesh to find out would cost more than sending it.
    """
    if FINGERPRINT_KEY not in obj:
        return True
    if len(obj.modifiers) > 0:
        return True
    return _fingerprint(obj.data) != obj[FINGERPRINT_KEY]


def _reverse_winding(indices):
    """Flip every triangle, so a mirrored mesh still faces outward.

    The basis swap negates a face's normal; reversing its winding negates it back. Doing one without the
    other is the classic way to end up with a map that looks right and is inside out.
    """
    flipped = list(indices)
    for at in range(0, len(flipped) - 2, 3):
        flipped[at + 1], flipped[at + 2] = flipped[at + 2], flipped[at + 1]
    return flipped


# ---------------------------------------------------------------- transport

class BridgeError(Exception):
    pass


def _read_header(sock):
    """Read one newline-terminated JSON header, a byte at a time.

    Byte at a time because a buffered read would swallow part of the binary body that follows the
    newline, and the body is not self-describing enough to recover from that.
    """
    line = bytearray()
    while True:
        b = sock.recv(1)
        if not b:
            raise BridgeError("ReyEngine closed the connection")
        if b == b"\n":
            return json.loads(line.decode("utf-8"))
        if b != b"\r":
            line += b
        if len(line) > 16 * 1024 * 1024:
            raise BridgeError("header never ended - wrong port?")


def _read_exactly(sock, count):
    chunks = bytearray()
    while len(chunks) < count:
        chunk = sock.recv(min(1 << 20, count - len(chunks)))
        if not chunk:
            raise BridgeError("ReyEngine closed the connection mid-payload")
        chunks += chunk
    return bytes(chunks)


def _request(context, payload, want_body=False, body=None):
    """One short-lived connection per action.

    A persistent socket would have to be pumped from Blender's single UI thread, and a dropped link
    would then need reconnect logic that fails in the middle of an edit. Reconnecting per action costs
    a millisecond on loopback and cannot get stuck.
    """
    prefs = context.scene.reyengine_bridge
    sock = socket.create_connection((prefs.host, prefs.port), timeout=30.0)
    try:
        sock.sendall((json.dumps({"op": "hello", "protocol": PROTOCOL}) + "\n").encode("utf-8"))
        hello = _read_header(sock)
        if hello.get("op") == "error":
            raise BridgeError(hello.get("message", "refused"))
        if hello.get("protocol") != PROTOCOL:
            raise BridgeError("protocol %s vs %s - update the older half" % (hello.get("protocol"), PROTOCOL))

        sock.sendall((json.dumps(payload) + "\n").encode("utf-8"))
        if body:
            sock.sendall(body)
        header = _read_header(sock)
        if header.get("op") == "error":
            raise BridgeError(header.get("message", "refused"))
        body = _read_exactly(sock, header.get("bytes", 0)) if want_body else b""
        return header, body
    finally:
        try:
            sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------- mesh block

def _decode_meshes(body):
    at = 0

    def u32():
        nonlocal at
        (value,) = struct.unpack_from("<i", body, at)
        at += 4
        return value

    def floats(n):
        nonlocal at
        values = struct.unpack_from("<%df" % n, body, at)
        at += 4 * n
        return values

    count = u32()
    meshes = []
    for _ in range(count):
        name_length = u32()
        name = body[at:at + name_length].decode("utf-8")
        at += name_length
        index = u32()
        pivot = floats(3)
        location = floats(3)
        rotation = floats(3)
        scale = floats(3)
        positions = floats(u32())
        normals = floats(u32())
        uvs = floats(u32())
        index_count = u32()
        indices = struct.unpack_from("<%dI" % index_count, body, at)
        at += 4 * index_count
        meshes.append({
            "name": name, "index": index, "pivot": pivot,
            "location": location, "rotation": rotation, "scale": scale,
            "positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
        })
    return meshes


# ---------------------------------------------------------------- operators

class REYENGINE_OT_pull(bpy.types.Operator):
    bl_idname = "reyengine.pull"
    bl_label = "Pull Map From ReyEngine"
    bl_description = "Replace this scene's ReyEngine collection with the meshes of the open map"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            header, body = _request(context, {"op": "pull"}, want_body=True)
            meshes = _decode_meshes(body)
        except (BridgeError, OSError, ValueError) as ex:
            self.report({"ERROR"}, "ReyEngine: %s" % ex)
            return {"CANCELLED"}

        for note in header.get("notes", []):
            self.report({"WARNING"}, "ReyEngine: %s" % note)

        collection = bpy.data.collections.get(COLLECTION)
        if collection is None:
            collection = bpy.data.collections.new(COLLECTION)
            context.scene.collection.children.link(collection)
        else:
            for obj in list(collection.objects):
                bpy.data.objects.remove(obj, do_unlink=True)

        window = context.window_manager
        window.progress_begin(0, max(1, len(meshes)))
        try:
            for at, entry in enumerate(meshes):
                _build_object(collection, entry)
                if at % 64 == 0:
                    window.progress_update(at)
        finally:
            window.progress_end()

        _widen_clipping(context)
        self.report({"INFO"}, "ReyEngine: pulled %d mesh(es)" % len(meshes))
        return {"FINISHED"}


def _uv_per_loop(uvs, indices):
    """Expand per-VERTEX UVs into the per-LOOP order a UV layer wants.

    from_pydata lays the loops out in exactly the order of `indices`, so this is a straight gather rather
    than a query against the mesh. Pure arithmetic on lists, kept out of _build_object so it can be tested
    without Blender - getting it wrong would not fail, it would smear the texture across the mesh.
    """
    per_loop = [0.0] * (len(indices) * 2)
    for loop_index, vertex_index in enumerate(indices):
        per_loop[loop_index * 2] = uvs[vertex_index * 2]
        per_loop[loop_index * 2 + 1] = uvs[vertex_index * 2 + 1]
    return per_loop


def _build_object(collection, entry):
    """Build one Blender mesh.

    Everything here is bulk. A map is ~900,000 vertices and ~2,700,000 loops across ~1,400 objects, and
    at that size anything done per element in Python stops being slow and starts looking like a hang:
    the first version multiplied every vertex by a Matrix and assigned every loop's UV individually,
    which froze Blender for minutes. The same work through foreach_set and one mesh.transform is a
    handful of C calls per mesh.
    """
    positions = entry["positions"]
    indices = entry["indices"]
    vertex_count = len(positions) // 3
    face_count = len(indices) // 3

    # Reversed once, and used for BOTH the faces and the UV gather below - a UV layer is per loop, and
    # from_pydata lays the loops out in the order it is given.
    wound = _reverse_winding(indices)

    mesh = bpy.data.meshes.new(entry["name"])
    mesh.from_pydata([(0.0, 0.0, 0.0)] * vertex_count, [],
                     [(wound[i], wound[i + 1], wound[i + 2]) for i in range(0, len(wound), 3)])

    # Coordinates go in as a flat buffer, then the basis change happens ONCE for the whole mesh instead
    # of once per vertex. Vertices arrive pivot-relative in League axes; the object matrix below carries
    # the placement.
    mesh.vertices.foreach_set("co", positions)
    mesh.transform(L2B)
    # No mesh.validate() here. It is per-element work on every mesh, and what it would catch is already
    # caught earlier: the decoder rejects an index past the end of the vertex buffer, and the editor
    # filters the degenerate faces its own face-delete leaves behind.

    # UV0 comes across so a reshaped mesh keeps its texturing. The lightmap channel deliberately does
    # not - a changed shape invalidates a bake anyway, and it would only survive as stale data.
    uvs = entry.get("uvs") or ()
    if len(uvs) == vertex_count * 2 and face_count:
        layer = mesh.uv_layers.new(name="UVMap")
        layer.data.foreach_set("uv", _uv_per_loop(uvs, wound))

    mesh.update()

    obj = bpy.data.objects.new(entry["name"], mesh)
    obj[INDEX_KEY] = entry["index"]
    obj[FINGERPRINT_KEY] = _fingerprint(mesh)
    obj.rotation_mode = "XYZ"

    # Rebuild ReyEngine's own transform, then change basis around it. ReyEngine composes scale, then X,
    # then Y, then Z about the pivot, which is exactly Blender's 'XYZ' euler order - so this is a
    # translation of notation, not a re-derivation, and pushing it straight back is a no-op.
    league = (
        Matrix.Translation(Vector(entry["location"]))
        @ Euler([_rad(a) for a in entry["rotation"]], "XYZ").to_matrix().to_4x4()
        @ Matrix.Diagonal(Vector(entry["scale"])).to_4x4()
    )
    obj.matrix_world = L2B @ league @ B2L
    collection.objects.link(obj)
    return obj


def _widen_clipping(context):
    """A League map is ~15,000 units across and Blender's default view clips at 1,000."""
    for area in context.screen.areas:
        if area.type != "VIEW_3D":
            continue
        for space in area.spaces:
            if space.type == "VIEW_3D":
                space.clip_start = max(space.clip_start, 1.0)
                space.clip_end = max(space.clip_end, 200000.0)


class REYENGINE_OT_push(bpy.types.Operator):
    bl_idname = "reyengine.push"
    bl_label = "Push Placements To ReyEngine"
    bl_description = "Send the position, rotation and scale of every pulled mesh back to the editor"
    bl_options = {"REGISTER"}

    selected_only: BoolProperty(name="Selected only", default=False)

    def execute(self, context):
        collection = bpy.data.collections.get(COLLECTION)
        if collection is None:
            self.report({"ERROR"}, "ReyEngine: nothing pulled yet")
            return {"CANCELLED"}

        transforms = []
        for obj in collection.objects:
            if INDEX_KEY not in obj:
                continue
            if self.selected_only and not obj.select_get():
                continue
            league = B2L @ obj.matrix_world @ L2B
            location, rotation, scale = league.decompose()
            euler = rotation.to_euler("XYZ")
            transforms.append({
                "i": int(obj[INDEX_KEY]),
                "loc": [location.x, location.y, location.z],
                "rot": [_deg(euler.x), _deg(euler.y), _deg(euler.z)],
                "scl": [scale.x, scale.y, scale.z],
            })

        if not transforms:
            self.report({"WARNING"}, "ReyEngine: nothing to push")
            return {"CANCELLED"}
        try:
            header, _ = _request(context, {"op": "push", "transforms": transforms})
        except (BridgeError, OSError, ValueError) as ex:
            self.report({"ERROR"}, "ReyEngine: %s" % ex)
            return {"CANCELLED"}

        self.report({"INFO"}, "ReyEngine: pushed %d (%s)" % (len(transforms), header.get("detail", "")))
        return {"FINISHED"}


def _encode_meshes(entries):
    """Pack meshes into the same block a pull sends, so the two directions cannot drift apart."""
    out = bytearray()
    out += struct.pack("<i", len(entries))
    for e in entries:
        name = e["name"].encode("utf-8")
        out += struct.pack("<i", len(name)) + name
        out += struct.pack("<i", e["index"])
        for key in ("pivot", "location", "rotation", "scale"):
            out += struct.pack("<3f", *e[key])
        out += struct.pack("<i", len(e["positions"])) + struct.pack("<%df" % len(e["positions"]), *e["positions"])
        out += struct.pack("<i", len(e["normals"])) + struct.pack("<%df" % len(e["normals"]), *e["normals"])
        out += struct.pack("<i", len(e["uvs"])) + struct.pack("<%df" % len(e["uvs"]), *e["uvs"])
        out += struct.pack("<i", len(e["indices"])) + struct.pack("<%dI" % len(e["indices"]), *e["indices"])
    return bytes(out)


def _to_league_axes(flat):
    """Blender (x, y, z) -> League (x, z, y) over a flat xyz buffer.

    The same swap as L2B, which is its own inverse. Arithmetic on slices rather than a Matrix per vertex:
    at a map's size, per-element work through Blender's RNA layer is what turns seconds into minutes.
    """
    return flat[0::3], flat[2::3], flat[1::3]


def _mesh_to_league(obj):
    """Evaluated, triangulated geometry in League axes, relative to the object origin (== the pivot).

    Evaluated so modifiers count: a subdivision surface the user added is part of the shape they see,
    and sending the base cage instead would silently discard their work.

    Everything is read in bulk with foreach_get. The first version walked loop_triangles in Python and
    did a Matrix multiply per vertex, which made pushing a reshaped mesh take minutes.
    """
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        vertex_count = len(mesh.vertices)
        loop_count = len(mesh.loops)
        tri_count = len(mesh.loop_triangles)
        if not vertex_count or not tri_count:
            return [], [], [], []

        co = [0.0] * (vertex_count * 3)
        mesh.vertices.foreach_get("co", co)
        no = [0.0] * (vertex_count * 3)
        mesh.vertices.foreach_get("normal", no)
        loop_vertex = [0] * loop_count
        mesh.loops.foreach_get("vertex_index", loop_vertex)
        tri_loops = [0] * (tri_count * 3)
        mesh.loop_triangles.foreach_get("loops", tri_loops)

        uv_layer = mesh.uv_layers.active
        loop_uv = [0.0] * (loop_count * 2)
        if uv_layer:
            uv_layer.data.foreach_get("uv", loop_uv)

        cx, cy, cz = _to_league_axes(co)
        nx, ny, nz = _to_league_axes(no)

        # A vertex with two different UVs has to become two vertices - a vertex buffer holds one UV per
        # vertex, and collapsing them would smear the seam across the texture.
        unique = {}
        positions, normals, uvs, indices = [], [], [], []
        for loop_index in _reverse_winding(tri_loops):
            vertex_index = loop_vertex[loop_index]
            u = loop_uv[loop_index * 2]
            v = loop_uv[loop_index * 2 + 1]
            key = (vertex_index, round(u, 6), round(v, 6))
            at = unique.get(key)
            if at is None:
                at = len(positions) // 3
                unique[key] = at
                positions += (cx[vertex_index], cy[vertex_index], cz[vertex_index])
                normals += (nx[vertex_index], ny[vertex_index], nz[vertex_index])
                uvs += (u, v)
            indices.append(at)
        return positions, normals, uvs, indices
    finally:
        evaluated.to_mesh_clear()


class REYENGINE_OT_push_geometry(bpy.types.Operator):
    bl_idname = "reyengine.push_geometry"
    bl_label = "Push Shapes To ReyEngine"
    bl_description = "Send the edited geometry of the selected meshes back to the editor"
    bl_options = {"REGISTER"}

    selected_only: BoolProperty(name="Selected only", default=True)

    def execute(self, context):
        collection = bpy.data.collections.get(COLLECTION)
        if collection is None:
            self.report({"ERROR"}, "ReyEngine: nothing pulled yet")
            return {"CANCELLED"}

        entries = []
        pushed = []
        unchanged = 0
        for obj in collection.objects:
            if INDEX_KEY not in obj or obj.type != "MESH":
                continue
            if self.selected_only and not obj.select_get():
                continue
            # "All" means every mesh you CHANGED, not every mesh. Replacing an untouched mesh would
            # re-number its vertices and drop its baked lightmap UVs for nothing.
            if not _has_changed(obj):
                unchanged += 1
                continue
            positions, normals, uvs, indices = _mesh_to_league(obj)
            if not positions or not indices:
                continue
            entries.append({
                "name": obj.name, "index": int(obj[INDEX_KEY]),
                "pivot": (0.0, 0.0, 0.0), "location": (0.0, 0.0, 0.0),
                "rotation": (0.0, 0.0, 0.0), "scale": (1.0, 1.0, 1.0),
                "positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
            })
            pushed.append(obj)

        if not entries:
            self.report({"INFO"}, "ReyEngine: no shape changes to push (%d unchanged)" % unchanged)
            return {"CANCELLED"}

        body = _encode_meshes(entries)
        try:
            header, _ = _request(context, {"op": "push_geometry", "bytes": len(body)}, body=body)
        except (BridgeError, OSError, ValueError) as ex:
            self.report({"ERROR"}, "ReyEngine: %s" % ex)
            return {"CANCELLED"}

        # Re-baselined only after ReyEngine has taken them, so a failed push stays pending.
        for obj in pushed:
            obj[FINGERPRINT_KEY] = _fingerprint(obj.data)

        self.report({"INFO"}, "ReyEngine: sent %d changed shape(s), skipped %d unchanged (%s)"
                    % (len(entries), unchanged, header.get("detail", "")))
        return {"FINISHED"}


def _deg(radians):
    return radians * 57.29577951308232


def _rad(degrees):
    return degrees * 0.017453292519943295


# ---------------------------------------------------------------- ui

class REYENGINE_PG_settings(bpy.types.PropertyGroup):
    host: StringProperty(name="Host", default="127.0.0.1")
    port: IntProperty(name="Port", default=47800, min=1, max=65535)


class REYENGINE_PT_panel(bpy.types.Panel):
    bl_label = "ReyEngine Bridge"
    bl_idname = "REYENGINE_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "ReyEngine"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.reyengine_bridge

        column = layout.column(align=True)
        column.prop(settings, "host")
        column.prop(settings, "port")

        layout.separator()
        layout.operator("reyengine.pull", icon="IMPORT")

        row = layout.row(align=True)
        row.operator("reyengine.push", icon="EXPORT", text="Push All").selected_only = False
        row.operator("reyengine.push", text="Selected").selected_only = True

        layout.separator()
        row = layout.row(align=True)
        row.operator("reyengine.push_geometry", icon="MESH_DATA", text="Push Shapes").selected_only = True
        row.operator("reyengine.push_geometry", text="All Changed").selected_only = False

        collection = bpy.data.collections.get(COLLECTION)
        layout.label(text="%d mesh(es) linked" % (len(collection.objects) if collection else 0))
        layout.label(text="Materials stay in ReyEngine.", icon="INFO")


CLASSES = (REYENGINE_PG_settings, REYENGINE_OT_pull, REYENGINE_OT_push,
           REYENGINE_OT_push_geometry, REYENGINE_PT_panel)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.reyengine_bridge = bpy.props.PointerProperty(type=REYENGINE_PG_settings)


def unregister():
    del bpy.types.Scene.reyengine_bridge
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
