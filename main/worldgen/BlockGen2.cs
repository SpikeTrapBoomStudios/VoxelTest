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
    [Export] byte blockSizeMultiplier = 1;

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
                chunkMap = new SysGeneric.Dictionary<Vector2,ushort[]>();
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
                chunkMap = new SysGeneric.Dictionary<Vector2,ushort[]>();
            }
        }
    }
    List<MeshInstance3D> meshes = new List<MeshInstance3D>();
    private SysGeneric.Dictionary<Vector2,ushort[]> chunkMap = new SysGeneric.Dictionary<Vector2,ushort[]>();

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

    struct ShortVector2(short x = 0, short y = 0)
    {
        public short x = x;
        public short y = y;
    }
    struct ShortVector3(short x = 0, short y = 0, short z = 0)
    {
        public short x = x;
        public short y = y;
        public short z = z;
    }
    struct MeshData()
    {
        public int c = 0;
        public List<ShortVector3> vertices = new();
        public List<int> indices = new();
        public List<ShortVector2> uvs = new();
        public List<ShortVector2> uv2s = new();
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

        GodotDict godotCompatMeshData = new GodotDict();

        Vector3[] vertices = new Vector3[newMeshData.vertices.Count];
        for (int i = 0; i < newMeshData.vertices.Count; i++)
        {
            ShortVector3 shortVector3 = newMeshData.vertices[i];
            vertices[i] = new Vector3(shortVector3.x, shortVector3.y, shortVector3.z);
        }
        godotCompatMeshData.Add("vertices", vertices);
        int[] indices = newMeshData.indices.ToArray();
        godotCompatMeshData.Add("indices", indices);
        Vector2[] uvs = new Vector2[newMeshData.uvs.Count];
        for (int i = 0; i < newMeshData.uvs.Count; i++)
        {
            ShortVector2 shortVector2 = newMeshData.uvs[i];
            uvs[i] = new Vector2(shortVector2.x, shortVector2.y);
        }
        godotCompatMeshData.Add("uvs", uvs);
        Vector2[] uv2s = new Vector2[newMeshData.uv2s.Count];
        for (int i = 0; i < newMeshData.uv2s.Count; i++)
        {
            ShortVector2 shortVector2 = newMeshData.uv2s[i];
            uv2s[i] = new Vector2(shortVector2.x, shortVector2.y);
        }
        godotCompatMeshData.Add("uv2s", uv2s);

        CallDeferred("_createChunkMeshFromData", godotCompatMeshData, chunkPos);
    }

    void _createChunkMeshFromData(Dictionary data, Vector2 chunkPos)
    {
        try
        {
            Godot.Collections.Array array = [];
            array.Resize((int)Mesh.ArrayType.Max);
            array[(int)Mesh.ArrayType.Vertex] = data["vertices"];
            array[(int)Mesh.ArrayType.Index] = data["indices"];
            array[(int)Mesh.ArrayType.TexUV] = data["uvs"];
            array[(int)Mesh.ArrayType.TexUV2] = data["uv2s"];
            ArrayMesh arrayMesh = new ArrayMesh();
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, array);
            MeshInstance3D mesh = new MeshInstance3D();
            mesh.Mesh = arrayMesh;
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

    void _populateBlockMap(Vector2 chunkPos)
    {
        ushort[] blockMap = new ushort[(CHUNK_SIZE_X / blockSizeMultiplier) * (CHUNK_SIZE_Y / blockSizeMultiplier) * (CHUNK_SIZE_Z / blockSizeMultiplier)];
        for (byte x = 0; x < CHUNK_SIZE_X / blockSizeMultiplier; x++)
        {
            int chunkOffsetX = x * blockSizeMultiplier + (int)chunkPos.X * CHUNK_SIZE_X;
            for (byte z = 0; z < CHUNK_SIZE_Z / blockSizeMultiplier; z++)
            {
                int chunkOffsetZ = z * blockSizeMultiplier + (int)chunkPos.Y * CHUNK_SIZE_Z;

                float rawHeight = noiseLite.GetNoise2D(chunkOffsetX, chunkOffsetZ);
                rawHeight = (rawHeight + 1) / 2;

                float surfaceY = rawHeight * terrainScale;
                surfaceY = Mathf.Pow(2, surfaceY);
                surfaceY = Math.Clamp(surfaceY, 0, CHUNK_SIZE_Y - 1);

                surfaceY = Mathf.Round(surfaceY);

                for (ushort y = 0; y <= surfaceY / blockSizeMultiplier; y++)
                {
                    short correctedY = (short)(y * blockSizeMultiplier);
                    short toleratedRange = (short)Math.Abs(correctedY - surfaceY);
                    ushort blockId;
                    if (toleratedRange < blockSizeMultiplier)
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
                    blockMap[_toFlat(x, y, z)] = blockId;
                }
            }
        }
        chunkMap[chunkPos] = blockMap;
    }

    int _toFlat(ushort x, ushort y, ushort z, bool padded = false)
    {
        ushort localChunkSizeZ = (ushort) (padded ? CHUNK_SIZE_Z + 2 : CHUNK_SIZE_Z);
        localChunkSizeZ = (ushort)Mathf.RoundToInt(localChunkSizeZ / blockSizeMultiplier);
        return x * (ushort)Mathf.RoundToInt(CHUNK_SIZE_Y / blockSizeMultiplier) * localChunkSizeZ + y * localChunkSizeZ + z; 
    }

    MeshData _createVerts(Vector2 chunkPos)
    {
        MeshData thisMeshData = new MeshData();

        ushort PADDED_CHUNK_SIZE_X = (ushort) (CHUNK_SIZE_X + 2);
        ushort PADDED_CHUNK_SIZE_Z = (ushort) (CHUNK_SIZE_Z + 2);
        ushort[] paddedChunkData = new ushort[PADDED_CHUNK_SIZE_X / blockSizeMultiplier * (CHUNK_SIZE_Y / blockSizeMultiplier) * (PADDED_CHUNK_SIZE_Z / blockSizeMultiplier)];
        ushort[] thisChunk = chunkMap[chunkPos];
        ushort[] xPosChunk = chunkMap.TryGetValue(chunkPos + Vector2.Right, out xPosChunk) ? xPosChunk : null;
        ushort[] xNegChunk = chunkMap.TryGetValue(chunkPos + Vector2.Left, out xNegChunk) ? xNegChunk : null;
        ushort[] zPosChunk = chunkMap.TryGetValue(chunkPos + Vector2.Down, out zPosChunk) ? zPosChunk : null;
        ushort[] zNegChunk = chunkMap.TryGetValue(chunkPos + Vector2.Up, out zNegChunk) ? zNegChunk : null;

        for (ushort x = 0; x < PADDED_CHUNK_SIZE_X / blockSizeMultiplier; x++)
        {
            for (ushort z = 0; z < PADDED_CHUNK_SIZE_Z / blockSizeMultiplier; z++)
            {
                for (ushort y = 0; y < CHUNK_SIZE_Y / blockSizeMultiplier; y++)
                {
                    if (x == 0 || x == (PADDED_CHUNK_SIZE_X - 1) / blockSizeMultiplier || z == 0 || z == (PADDED_CHUNK_SIZE_Z - 1) / blockSizeMultiplier)
                    {
                        if (x == 0)
                        {
                            if (xNegChunk != null) paddedChunkData[_toFlat(x, y, z, true)] = xNegChunk[_toFlat((ushort)((CHUNK_SIZE_X - 1) / blockSizeMultiplier), y, (ushort)Math.Clamp(z - 1, 0, ((CHUNK_SIZE_Z - 1) / blockSizeMultiplier)))];
                            else paddedChunkData[_toFlat(x, y, z, true)] = 0;
                        }
                        if (x == (PADDED_CHUNK_SIZE_X - 1) / blockSizeMultiplier)
                        {
                            if (xPosChunk != null) paddedChunkData[_toFlat(x, y, z, true)] = xPosChunk[_toFlat(0, y, (ushort)Math.Clamp(z - 1, 0, ((CHUNK_SIZE_Z - 1) / blockSizeMultiplier)))];
                            else paddedChunkData[_toFlat(x, y, z, true)] = 0;
                        }
                        if (z == 0)
                        {
                            if (zNegChunk != null) paddedChunkData[_toFlat(x, y, z, true)] = zNegChunk[_toFlat((ushort)Math.Clamp(x - 1, 0, ((CHUNK_SIZE_X - 1) / blockSizeMultiplier)), y, (ushort)((CHUNK_SIZE_Z - 1) / blockSizeMultiplier))];
                            else paddedChunkData[_toFlat(x, y, z, true)] = 0;
                        }
                        if (z == (PADDED_CHUNK_SIZE_Z - 1) / blockSizeMultiplier)
                        {
                            if (zPosChunk != null) paddedChunkData[_toFlat(x, y, z, true)] = zPosChunk[_toFlat((ushort)Math.Clamp(x - 1, 0, ((CHUNK_SIZE_X - 1) / blockSizeMultiplier)), y, 0)];
                            else paddedChunkData[_toFlat(x, y, z, true)] = 0;
                        }   
                    }
                    else
                    {
                        paddedChunkData[_toFlat(x, y, z, true)] = thisChunk[_toFlat((ushort)(x - 1), y, (ushort)(z - 1))];
                    }
                }
            }
        }

        foreach (FaceDef face in FaceDefs)
            {
                _greedyMeshFace(
                    ref thisMeshData,
                    face.axis,
                    face.dir,
                    chunkPos,
                    ref paddedChunkData
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

    void _greedyMeshFace(ref MeshData meshData, byte axis, sbyte dir, Vector2 chunkPos, ref ushort[] givenChunks)
    {
        ushort[] size = [(ushort)(CHUNK_SIZE_X / blockSizeMultiplier), (ushort)(CHUNK_SIZE_Y / blockSizeMultiplier), (ushort)(CHUNK_SIZE_Z / blockSizeMultiplier)];
        byte[] perpAxis = _getFaceAxes(axis);
        byte perpAxis1 = perpAxis[0];
        byte perpAxis2 = perpAxis[1];

        ushort mainLimit = size[axis];
        ushort perp1Limit = size[perpAxis1];
        ushort perp2Limit = size[perpAxis2];
        
        bool[] visited = new bool[perp1Limit];
        short[] originPos = new short[3];
        short[] adjPos = new short[3];
        ShortVector3[] faceVerts = new ShortVector3[4];
        ShortVector2[] faceUvs = new ShortVector2[4];
        short[] vert = new short[3];

        for (short mainOffest = 0; mainOffest < mainLimit; mainOffest++)
        {

            for (short perp2Offset = 0; perp2Offset < perp2Limit; perp2Offset++)
            {

                System.Array.Clear(visited, 0, perp1Limit);
                for (short perp1Offset = 0; perp1Offset < perp1Limit; perp1Offset++)
                {
                    originPos[axis] = (short)(mainOffest * blockSizeMultiplier);
                    originPos[perpAxis1] = (short)(perp1Offset * blockSizeMultiplier);
                    originPos[perpAxis2] = (short)(perp2Offset * blockSizeMultiplier);
                    if (visited[perp1Offset]) continue;
                    ushort blockId = _getBlockIdAt(new ShortVector3((short)(originPos[0]+1), originPos[1], (short)(originPos[2]+1)), chunkPos, ref givenChunks);
                    if (blockId == 0) continue;
                    visited[perp1Offset] = true;

                    adjPos[0] = originPos[0];
                    adjPos[1] = originPos[1];
                    adjPos[2] = originPos[2];
                    adjPos[axis] += (short)(dir * blockSizeMultiplier);
                    ushort adjOriginBlockId = _getBlockIdAt(new ShortVector3((short)(adjPos[0]+1), adjPos[1], (short)(adjPos[2]+1)), chunkPos, ref givenChunks);
                    if (adjOriginBlockId != 0) continue;

                    short width = 1;
                    adjPos[axis] = originPos[axis];
                    while (true)
                    {
                        adjPos[perpAxis1]+=blockSizeMultiplier;
                        if (perp1Offset + width >= visited.Length) break;
                        if (visited[perp1Offset + width]) break;
                        ushort adjBlockId = _getBlockIdAt(new ShortVector3((short)(adjPos[0]+1), adjPos[1], (short)(adjPos[2]+1)), chunkPos, ref givenChunks);
                        if (adjBlockId == 0 || adjBlockId != blockId) break;
                        visited[perp1Offset + width] = true;
                        width++;
                    }

                    byte c = 0;
                    for (byte i=0; i<4; i++)
                    {
                        short u = 0;
                        short v = 0;
                        if (i==0) { u = 0; v = 0; }
                        else if (i==1) { u = width; v = 0; }
                        else if (i==2) { u = width; v = 1; }
                        else if (i==3) { u = 0; v = 1; }

                        vert[axis] = (short)((mainOffest + (dir == 1 ? 1 : 0)) * blockSizeMultiplier);
                        vert[perpAxis1] = (short)((perp1Offset + u) * blockSizeMultiplier);
                        vert[perpAxis2] = (short)((perp2Offset + v) * blockSizeMultiplier);

                        faceVerts[c] = new ShortVector3(vert[0], vert[1], vert[2]);

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
                                meshData.uv2s.Add(new ShortVector2(0, 0));
                            }
                            else if (sideName == LEFT_FACE || sideName == RIGHT_FACE || sideName == FRONT_FACE || sideName == BACK_FACE)
                            {
                                meshData.uv2s.Add(new ShortVector2(1, 0));
                            }
                            else
                            {
                                meshData.uv2s.Add(new ShortVector2(2, 0));
                            }
                        }
                        else if (blockId == 2)
                        {
                            meshData.uv2s.Add(new ShortVector2(2, 0));
                        }
                        else if (blockId == 3)
                        {
                            meshData.uv2s.Add(new ShortVector2(3, 0));
                        }
                        else if (blockId == 4)
                        {
                            meshData.uv2s.Add(new ShortVector2(4, 0));
                        }
                        faceUvs[c] = new ShortVector2(u, v);
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
    
    bool _doesBlockExistAt(ShortVector3 blockPos, Vector2 chunkPos, ref ushort[] givenChunks)
    {
        return _getBlockIdAt(blockPos, chunkPos, ref givenChunks) != 0;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort _getBlockIdAt(ShortVector3 blockPos, Vector2 chunkPos, ref ushort[] givenChunks)
    {
        if (blockPos.y < 0 || blockPos.y >= CHUNK_SIZE_Y) return 0;

        return givenChunks[_toFlat((ushort)Mathf.Clamp(blockPos.x / blockSizeMultiplier, 0, (CHUNK_SIZE_X/blockSizeMultiplier)-1), (ushort)Mathf.Clamp(blockPos.y / blockSizeMultiplier, 0, (CHUNK_SIZE_Y/blockSizeMultiplier)-1), (ushort)Mathf.Clamp(blockPos.z / blockSizeMultiplier, 0, (CHUNK_SIZE_Z/blockSizeMultiplier)-1), true)];
        /*bool exceedsChunkDims = false;
        bool posXadj = false;
        bool posZadj = false;
        bool negXadj = false;
        bool negZadj = false;
        if (blockPos.x < 0 || blockPos.x >= CHUNK_SIZE_X || blockPos.z < 0 || blockPos.z >= CHUNK_SIZE_Z)
        {
            exceedsChunkDims = true;
            posXadj = blockPos.x >= CHUNK_SIZE_X;
            posZadj = blockPos.z >= CHUNK_SIZE_Z;
            negXadj = blockPos.x < 0;
            negZadj = blockPos.z < 0;

            chunkPos.X += posXadj ? 1 : negXadj ? -1 : 0;
            chunkPos.Y += posZadj ? 1 : negZadj ? -1 : 0;

            if (posXadj) blockPos.x = (short)(blockPos.x - CHUNK_SIZE_X);
            else if (negXadj) blockPos.x = (short)(CHUNK_SIZE_X + blockPos.x);

            if (posZadj) blockPos.z = (short)(blockPos.z - CHUNK_SIZE_Z);
            else if (negZadj) blockPos.z = (short)(CHUNK_SIZE_Z + blockPos.z);
        }

        blockPos.x = (short)(blockPos.x / blockSizeMultiplier);
        blockPos.x = (short)Mathf.Clamp(blockPos.x, 0, (CHUNK_SIZE_X/blockSizeMultiplier)-1);
        blockPos.y = (short)(blockPos.y / blockSizeMultiplier);
        blockPos.y = (short)Mathf.Clamp(blockPos.y, 0, (CHUNK_SIZE_Y/blockSizeMultiplier)-1);
        blockPos.z = (short)(blockPos.z / blockSizeMultiplier);
        blockPos.z = (short)Mathf.Clamp(blockPos.z, 0, (CHUNK_SIZE_Z/blockSizeMultiplier)-1);
        if (exceedsChunkDims || givenChunks[0] == null)
        {
            if (posXadj && givenChunks[4] != null)
            {
                return givenChunks[4][blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];
            }
            if (negXadj && givenChunks[3] != null)
            {
                return givenChunks[3][blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];
            }
            if (posZadj && givenChunks[1] != null)
            {
                return givenChunks[1][blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];
            }
            if (negZadj && givenChunks[2] != null)
            {
                return givenChunks[2][blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];
            }
            if (!chunkMap.TryGetValue(chunkPos, out ushort[] _chunkMap)) return 0;
            return _chunkMap[blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];
        }
        return givenChunks[0][blockPos.x * CHUNK_SIZE_Y * CHUNK_SIZE_Z + blockPos.y * CHUNK_SIZE_Z + blockPos.z];*/
    }
}
