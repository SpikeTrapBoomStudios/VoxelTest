using Godot;
using System;
using Godot.Collections;
using System.Threading;
using Vector2 = Godot.Vector2;
using Vector3 = Godot.Vector3;
using System.Collections.Generic;
using GodotDict = Godot.Collections.Dictionary;
using SysGeneric = System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;

[Tool]
[GlobalClass]
public partial class BlockGen2 : Node
{
    [Export] FastNoiseLite noiseLite = (FastNoiseLite) GD.Load("res://resources/materials/terrain_noise_main.tres");
    [Export] int chunkCount = 1;
    [Export] byte CHUNK_SIZE_X = 32;
    [Export] byte CHUNK_SIZE_Z = 32;
    [Export] ushort CHUNK_SIZE_Y = 250;
    [Export] byte terrainScale = 10;

    private bool _generate = false;
    [Export]
    public bool generate
    {
        get { return false; }
        set
        {
            if (IsInsideTree())
            {
                foreach (MeshInstance3D mesh in meshes)
                {
                    mesh.QueueFree();
                }
                genThread = null;
                meshes.Clear();
                chunkMap = new ConcurrentDictionary<Vector2,ushort[,,]>();
                _generateMesh();
            }
        }
    }
    private bool _reset = false;
    [Export]
    public bool reset
    {
        get { return false; }
        set
        {
            if (IsInsideTree())
            {
                foreach (MeshInstance3D mesh in meshes)
                {
                    mesh.QueueFree();
                }
                genThread = null;
                meshes.Clear();
                chunkMap = new ConcurrentDictionary<Vector2,ushort[,,]>();
            }
        }
    }
    List<MeshInstance3D> meshes = new List<MeshInstance3D>();
    private ConcurrentDictionary<Vector2,ushort[,,]> chunkMap = new ConcurrentDictionary<Vector2,ushort[,,]>();

    Thread genThread;

    const byte AXIS_X = 0;
    const byte AXIS_Y = 1;
    const byte AXIS_Z = 2;
    const sbyte DIR_POS = 1;
    const sbyte DIR_NEG = -1;

    const byte FRONT_FACE = 0;
    const byte BACK_FACE = 1;
    const byte LEFT_FACE = 2;
    const byte RIGHT_FACE = 3;
    const byte TOP_FACE = 4;
    const byte BOTTOM_FACE = 5;

    struct MeshData()
    {
        public int c = 0;
        public List<Vector3> vertices = new(10000);
        public List<int> indices = new(10000);
        public List<Vector2> uvs = new(10000);
        public List<Vector2> uv2s = new(10000);
    }
    struct FaceDef(byte axis, sbyte dir)
    {
        public byte axis = axis;
        public sbyte dir = dir;
    }
    static readonly FaceDef[] FaceDefs = [
        new FaceDef(AXIS_X, DIR_POS),
        new FaceDef(AXIS_X, DIR_NEG),
        new FaceDef(AXIS_Y, DIR_POS),
        new FaceDef(AXIS_Y, DIR_NEG),
        new FaceDef(AXIS_Z, DIR_POS),
        new FaceDef(AXIS_Z, DIR_NEG),
    ];

    Stopwatch stopwatch = new();

    void _generateMesh()
    {
        genThread = new Thread(new ThreadStart(_threadedGenerateChunk));
        genThread.Start();
    }

    public override void _ExitTree()
    {
        if (genThread != null && genThread.IsAlive) { genThread?.Join(); }
        base._ExitTree();
    }

    void _threadedGenerateChunk()
    {
        try
        {
            Parallel.For(0, chunkCount, x =>
                Parallel.For(0, chunkCount, z => _populateBlockMap(new Vector2(x, z)))
            );
            stopwatch.Reset();
            stopwatch.Start();
            Parallel.For(0, chunkCount, x =>
                Parallel.For(0, chunkCount, z => _chunkGeneration(new Vector2(x, z)))
            );
        }
        catch (Exception e) { GD.Print(e); }
    }

    void _chunkGeneration(Vector2 chunkPos)
    {
        MeshData newMeshData = _createVerts(chunkPos);
        Godot.Collections.Array array = [];
        array.Resize((int)Mesh.ArrayType.Max);
        array[(int)Mesh.ArrayType.Vertex] = newMeshData.vertices.ToArray();
        array[(int)Mesh.ArrayType.Index] = newMeshData.indices.ToArray();
        array[(int)Mesh.ArrayType.TexUV] = newMeshData.uvs.ToArray();
        array[(int)Mesh.ArrayType.TexUV2] = newMeshData.uv2s.ToArray();
        ArrayMesh arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, array);
        MeshInstance3D mesh = new MeshInstance3D();
        mesh.Mesh = arrayMesh;
        CallDeferred("_createChunkMeshFromData", mesh, chunkPos);
    }

    void _createChunkMeshFromData(MeshInstance3D mesh, Vector2 chunkPos)
    {
        try
        {
            AddChild(mesh);
            if (Engine.IsEditorHint())
            {
                mesh.Owner = GetTree().EditedSceneRoot;
            }
            mesh.MaterialOverride = (Material)ResourceLoader.Load("res://resources/materials/voxel_material_main.tres");
            mesh.Position = new Vector3(chunkPos.X * CHUNK_SIZE_X, 0, chunkPos.Y * CHUNK_SIZE_Z);
            meshes.Add(mesh);
            if (meshes.Count >= Math.Pow(chunkCount, 2))
            {
                stopwatch.Stop();
                GD.Print("Total Chunk Gen Time: ", stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr("Exception in _createChunkMeshFromData: ", ex.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int _toFlat(short x, short y, short z)
    {
        return x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + y * CHUNK_SIZE_Z + z;
    }

    void _populateBlockMap(Vector2 chunkPos)
    {
        ushort[,,] blockMap = new ushort[CHUNK_SIZE_X, CHUNK_SIZE_Y, CHUNK_SIZE_Z];
        for (byte x = 0; x < CHUNK_SIZE_X; x++)
        {
            int chunkOffsetX = x + (int)chunkPos.X * CHUNK_SIZE_X;
            for (byte z = 0; z < CHUNK_SIZE_Z; z++)
            {
                int chunkOffsetZ = z + (int)chunkPos.Y * CHUNK_SIZE_Z;

                float rawHeight = noiseLite.GetNoise2D(chunkOffsetX, chunkOffsetZ);
                rawHeight = (rawHeight + 1) / 2;

                float surfaceY = rawHeight * terrainScale;
                surfaceY = Mathf.Pow(2, surfaceY);
                surfaceY = Math.Clamp(surfaceY, 0, CHUNK_SIZE_Y - 1);

                surfaceY = Mathf.Round(surfaceY);

                for (ushort y = 0; y <= surfaceY; y++)
                {
                    short correctedY = (short) y;
                    short toleratedRange = (short)Math.Abs(correctedY - surfaceY);
                    ushort blockId;
                    if (toleratedRange < 1)
                    {
                        blockId = 1;
                    }
                    else if (toleratedRange < 5)
                    {
                        blockId = 2;
                    }
                    else if (surfaceY - toleratedRange >= EtcMathLib.FastRangeRandom(chunkOffsetX + chunkOffsetX + chunkOffsetZ, 1, 3))
                    {
                        blockId = 3;
                    }
                    else
                    {
                        blockId = 4;
                    }
                    blockMap[x, y, z] = blockId;
                }
            }
        }
        chunkMap[chunkPos] = blockMap;
    }

    MeshData _createVerts(Vector2 chunkPos)
    {
        MeshData thisMeshData = new MeshData();

        ushort[,,] chunkData = chunkMap.TryGetValue(chunkPos, out chunkData) ? chunkData : null;

        foreach (FaceDef face in FaceDefs)
        {
            _greedyMeshFace(
                ref thisMeshData,
                face.axis,
                face.dir,
                chunkPos,
                chunkData
            );
        }

        return thisMeshData;
    }

    byte[] _getFaceAxes(byte axis)
    {
        switch (axis)
        {
            case AXIS_X:
                //X Axis; return [Y, Z]
                return [AXIS_Y, AXIS_Z];
            case AXIS_Y:
                //Y Axis; return [Z, X]
                return [AXIS_Z, AXIS_X];
            case AXIS_Z:
                //Z Axis; return [X, Y]
                return [AXIS_X, AXIS_Y];
        }
        GD.PushError("_get_face_axes provided an invalid axis");
        return [0, 1];
    }

    void _greedyMeshFace(ref MeshData meshData, byte axis, sbyte dir, Vector2 chunkPos, ushort[,,] chunkData)
    {
        ushort[] size = [CHUNK_SIZE_X, CHUNK_SIZE_Y, CHUNK_SIZE_Z];
        byte[] perpAxis = _getFaceAxes(axis);
        byte perpAxis1 = perpAxis[0];
        byte perpAxis2 = perpAxis[1];

        ushort mainLimit = size[axis];
        ushort perp1Limit = size[perpAxis1];
        ushort perp2Limit = size[perpAxis2];
        
        bool[] visited = new bool[perp1Limit];
        bool[] mask = new bool[perp1Limit];
        short[] originPos = new short[3];
        short[] adjPos = new short[3];
        Vector3[] faceVerts = new Vector3[4];
        Vector2[] faceUvs = new Vector2[4];
        short[] vert = new short[3];

        for (short mainOffest = 0; mainOffest < mainLimit; mainOffest++)
        {
            for (short perp2Offset = 0; perp2Offset < perp2Limit; perp2Offset++)
            {
                System.Array.Clear(visited, 0, perp1Limit);
                for (short perp1Offset = 0; perp1Offset < perp1Limit; perp1Offset++)
                {
                    if (visited[perp1Offset]) continue;
                    originPos[axis] = mainOffest;
                    originPos[perpAxis1] = perp1Offset;
                    originPos[perpAxis2] = perp2Offset;
                    ushort blockId = _getBlockIdAt(originPos[0], originPos[1], originPos[2], chunkData);
                    if (blockId == 0) continue;
                    visited[perp1Offset] = true;

                    adjPos[0] = originPos[0];
                    adjPos[1] = originPos[1];
                    adjPos[2] = originPos[2];
                    adjPos[axis] += dir;
                    ushort adjOriginBlockId = _getBlockIdAt(adjPos[0], adjPos[1], adjPos[2], chunkData);
                    if (adjOriginBlockId != 0) continue;

                    short width = 1;
                    adjPos[axis] = originPos[axis];
                    while (perp1Offset + width < perp1Limit)
                    {
                        adjPos[perpAxis1]++;
                        if (perp1Offset + width >= visited.Length) break;
                        if (visited[perp1Offset + width]) break;
                        ushort adjBlockId = _getBlockIdAt(adjPos[0], adjPos[1], adjPos[2], chunkData);
                        if (adjBlockId == 0 || adjBlockId != blockId) break;
                        visited[perp1Offset + width] = true;
                        width++;
                    }

                    byte c = 0;
                    for (byte i = 0; i < 4; i++)
                    {
                        short u = (short)(i == 0 || i == 3 ? 0 : width);
                        short v = (short)(i == 0 || i == 1 ? 0 : 1);

                        vert[axis] = (short)(mainOffest + (dir == 1 ? 1 : 0));
                        vert[perpAxis1] = (short)(perp1Offset + u);
                        vert[perpAxis2] = (short)(perp2Offset + v);

                        faceVerts[c] = new Vector3(vert[0], vert[1], vert[2]);

                        /*Dont even ask why I need to do this. All that needs to be known is that this
                        flips the uv by 90 degrees. Why would we need this? Because some faces are rotated
                        90 degrees wrongly. Why? Idk.*/
                        if (axis == AXIS_X || axis == AXIS_Y)
                        {
                            (v, u) = (u, v);
                        }
                        byte sideName = _getSideNameFromAxisDir(axis, dir);
                        if (blockId == 1)
                        {
                            if (sideName == TOP_FACE)
                            {
                                meshData.uv2s.Add(new Vector2(0, 0));
                            }
                            else if (sideName == LEFT_FACE || sideName == RIGHT_FACE || sideName == FRONT_FACE || sideName == BACK_FACE)
                            {
                                meshData.uv2s.Add(new Vector2(1, 0));
                            }
                            else
                            {
                                meshData.uv2s.Add(new Vector2(2, 0));
                            }
                        }
                        else if (blockId == 2)
                        {
                            meshData.uv2s.Add(new Vector2(2, 0));
                        }
                        else if (blockId == 3)
                        {
                            meshData.uv2s.Add(new Vector2(3, 0));
                        }
                        else if (blockId == 4)
                        {
                            meshData.uv2s.Add(new Vector2(4, 0));
                        }
                        faceUvs[c] = new Vector2(u, v);
                        c++;
                    }
                    if (dir == DIR_POS)
                    {
                        meshData.vertices.AddRange([faceVerts[0], faceVerts[1], faceVerts[2], faceVerts[3]]);
                        if (axis == AXIS_Z)
                        {
                            meshData.uvs.AddRange([faceUvs[3], faceUvs[2], faceUvs[1], faceUvs[0]]);
                        }
                        else
                        {
                            meshData.uvs.AddRange([faceUvs[2], faceUvs[3], faceUvs[0], faceUvs[1]]);
                        }
                    }
                    else
                    {
                        meshData.vertices.AddRange([faceVerts[0], faceVerts[3], faceVerts[2], faceVerts[1]]);
                        if (axis == AXIS_X)
                        {
                            meshData.uvs.AddRange([faceUvs[1], faceUvs[2], faceUvs[3], faceUvs[0]]);
                        }
                        else if (axis == AXIS_Z)
                        {
                            meshData.uvs.AddRange([faceUvs[2], faceUvs[1], faceUvs[0], faceUvs[3]]);
                        }
                        else
                        {
                            meshData.uvs.AddRange([faceUvs[3], faceUvs[0], faceUvs[1], faceUvs[2]]);
                        }
                    }
                    int ic = meshData.c;
                    meshData.indices.AddRange([
                            ic+2, ic + 1, ic,
                            ic+3, ic + 2, ic
                        ]);
                    meshData.c += 4;
                }
            }
        }
    }

    byte _getSideNameFromAxisDir(byte axis, sbyte dir)
    {
        if (dir == DIR_POS)
        {
            switch (axis)
            {
                case AXIS_X: return RIGHT_FACE;
                case AXIS_Y: return TOP_FACE;
                case AXIS_Z: return FRONT_FACE;
            }
        }
        else
        {
            switch (axis)
            {
                case AXIS_X: return LEFT_FACE;
                case AXIS_Y: return BOTTOM_FACE;
                case AXIS_Z: return BACK_FACE;
            }
        }
        return FRONT_FACE;
    }
    
    int _clamp(int d, int min, int max) {
        int t = d < min ? min : d;
        return t > max ? max : t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort _getBlockIdAt(short x, short y, short z, ushort[,,] chunkData)
    {
        if (y < 0 || y >= CHUNK_SIZE_Y || x < 0 || x >= CHUNK_SIZE_X || z < 0 || z >= CHUNK_SIZE_Z) return 0;
        return chunkData[x, y, z];
    }
}
