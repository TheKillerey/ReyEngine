"""ReyEngine bridge — edit a League map's meshes in Blender.

Install: Blender ▸ Edit ▸ Preferences ▸ Add-ons ▸ Install… ▸ pick this file ▸ tick it.
Use:     ReyEngine ▸ Tools ▸ Blender Link ▸ Start, then the ReyEngine tab in the 3D view sidebar (N).

Geometry only. Materials stay in ReyEngine — they live in the map's companion .bin, bound to submesh
ranges and shader state that has no representation here, and a round trip through Blender would lose them.

Coordinates
-----------
The wire is League space: Y up, the same numbers the .mapgeo holds. This file owns the conversion, in
one place, as a change of basis:

    League (x, y, z)  ->  Blender (x, -z, y)

Transforms are converted as B = C @ L @ C^-1 rather than by swapping euler components, because swapping
components is only correct for axis-aligned rotations and silently wrong for everything else.

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

import bpy
from bpy.props import IntProperty, StringProperty, BoolProperty
from mathutils import Euler, Matrix, Vector

PROTOCOL = 1
COLLECTION = "ReyEngine Map"
INDEX_KEY = "rey_index"

# League -> Blender basis. Orthonormal with determinant +1, so the inverse is the transpose and the
# conversion never introduces a mirror (which would flip winding and read as inside-out geometry).
L2B = Matrix(((1.0, 0.0, 0.0), (0.0, 0.0, -1.0), (0.0, 1.0, 0.0))).to_4x4()
B2L = L2B.inverted()


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


def _request(context, payload, want_body=False):
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
        index_count = u32()
        indices = struct.unpack_from("<%dI" % index_count, body, at)
        at += 4 * index_count
        meshes.append({
            "name": name, "index": index, "pivot": pivot,
            "location": location, "rotation": rotation, "scale": scale,
            "positions": positions, "normals": normals, "indices": indices,
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

        for entry in meshes:
            _build_object(collection, entry)

        _widen_clipping(context)
        self.report({"INFO"}, "ReyEngine: pulled %d mesh(es)" % len(meshes))
        return {"FINISHED"}


def _build_object(collection, entry):
    positions = entry["positions"]
    indices = entry["indices"]

    mesh = bpy.data.meshes.new(entry["name"])
    verts = [(positions[i], positions[i + 1], positions[i + 2]) for i in range(0, len(positions), 3)]
    # Vertices arrive pivot-relative in League axes; the basis change is the object's job below, so
    # rotate them here and leave the object matrix free to carry the placement.
    verts = [tuple(L2B @ Vector(v)) for v in verts]
    faces = [(indices[i], indices[i + 1], indices[i + 2]) for i in range(0, len(indices), 3)]
    mesh.from_pydata(verts, [], faces)
    mesh.validate(verbose=False)
    mesh.update()

    obj = bpy.data.objects.new(entry["name"], mesh)
    obj[INDEX_KEY] = entry["index"]
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

        collection = bpy.data.collections.get(COLLECTION)
        layout.label(text="%d mesh(es) linked" % (len(collection.objects) if collection else 0))
        layout.label(text="Materials stay in ReyEngine.", icon="INFO")


CLASSES = (REYENGINE_PG_settings, REYENGINE_OT_pull, REYENGINE_OT_push, REYENGINE_PT_panel)


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
