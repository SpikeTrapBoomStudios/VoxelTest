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

[Tool]
[GlobalClass]
public partial class BlockGen : Node
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

    const sbyte AXIS_X = 0;
    const sbyte AXIS_Y = 1;
    const sbyte AXIS_Z = 2;
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
    struct FaceDef(sbyte axis, sbyte dir)
    {
        public sbyte axis = axis;
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
        stopwatch.Reset();
        stopwatch.Start();
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
        ushort[,,] blockMap = new ushort[CHUNK_SIZE_X / blockSizeMultiplier, CHUNK_SIZE_Y / blockSizeMultiplier, CHUNK_SIZE_Z / blockSizeMultiplier];
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
                    blockMap[x, y, z] = blockId;
                }
            }
        }
        chunkMap[chunkPos] = blockMap;
    }

    MeshData _createVerts(Vector2 chunkPos)
    {
        MeshData thisMeshData = new MeshData();

        ushort[][,,] givenChunks = [null, null, null, null, null];
        givenChunks[0] = chunkMap.TryGetValue(chunkPos, out ushort[,,] thisChunk) ? thisChunk : null;
        givenChunks[1] = chunkMap.TryGetValue(chunkPos + new Vector2(0, 1), out ushort[,,] forwardChunk) ? forwardChunk : null;
        givenChunks[2] = chunkMap.TryGetValue(chunkPos + new Vector2(0, -1), out ushort[,,] backwardChunk) ? backwardChunk : null;
        givenChunks[3] = chunkMap.TryGetValue(chunkPos + new Vector2(-1, 0), out ushort[,,] leftChunk) ? leftChunk : null;
        givenChunks[4] = chunkMap.TryGetValue(chunkPos + new Vector2(1, 0), out ushort[,,] rightChunk) ? rightChunk : null;

        foreach (FaceDef face in FaceDefs)
        {
            _greedyMeshFace(
                ref thisMeshData,
                face.axis,
                face.dir,
                chunkPos,
                ref givenChunks
            );
        }

        return thisMeshData;
    }

    sbyte[] _getFaceAxes(sbyte axis)
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

    void _greedyMeshFace(ref MeshData meshData, sbyte axis, sbyte dir, Vector2 chunkPos, ref ushort[][,,] givenChunks)
    {
        byte sideName = _getSideNameFromAxisDir(axis, dir);
        short[][] corners = new short[4][];
        ShortVector3[] faceVerts = new ShortVector3[4];
        ShortVector2[] faceUvs = new ShortVector2[4];
        short[] pos = new short[3];
        short[] adj = new short[3];
        short[] vert = new short[3];
        ushort[] size = [(ushort)(CHUNK_SIZE_X / blockSizeMultiplier), (ushort)(CHUNK_SIZE_Y / blockSizeMultiplier), (ushort)(CHUNK_SIZE_Z / blockSizeMultiplier)];
        sbyte[] axies = _getFaceAxes(axis);
        sbyte axis1 = axies[0];
        sbyte axis2 = axies[1];

        ushort mainLimit = size[axis];
        ushort axis1Limit = size[axis1];
        ushort axis2Limit = size[axis2];

        bool[,] mask = new bool[axis1Limit, axis2Limit];
        bool[,] visited = new bool[axis1Limit, axis2Limit];
        for (ushort main = 0; main < mainLimit; main++)
        {
            /*This is the mask for-loop. Its job is to populate an array of equal size/organization as the
            chunkMap array, except instead of blocks it is true/false values of whether that block should
            show its face or not, on this side/axis.*/
            System.Array.Clear(mask, 0, mask.Length);
            for (ushort i = 0; i < axis1Limit; i++)
            {
                for (ushort j = 0; j < axis2Limit; j++)
                {
                    /*Position is represented as an array so that we can perform [axis]-like
                    operations on it. More modularity essentially.*/
                    pos[0] = 0; pos[1] = 0; pos[2] = 0;
                    pos[axis] = (short)(main * blockSizeMultiplier);
                    pos[axis1] = (short)(i * blockSizeMultiplier);
                    pos[axis2] = (short)(j * blockSizeMultiplier);

                    adj[0] = pos[0]; adj[1] = pos[1]; adj[2] = pos[2];
                    adj[axis] += (short)(dir * blockSizeMultiplier); /*Moves the block position in the direction of the axis * dir.
                                       So if the axis = 0 (x axis) and dir = -1, moves x-1 blocks.*/

                    bool current_block_exists = _doesBlockExistAt(new ShortVector3(pos[0], pos[1], pos[2]), chunkPos, ref givenChunks);
                    bool adjacent_block_exists = _doesBlockExistAt(new ShortVector3(adj[0], adj[1], adj[2]), chunkPos, ref givenChunks);

                    /*Appends a true/false value based on whether the current block is air AND the
                    adjacent block is not air*/
                    mask[i, j] = current_block_exists && !adjacent_block_exists;
                }
            }

            System.Array.Clear(visited, 0, visited.Length);

            for (ushort axis1Offset = 0; axis1Offset < axis1Limit; axis1Offset++)
            {
                for (ushort axis2Offset = 0; axis2Offset < axis2Limit; axis2Offset++)
                {
                    if (mask[axis1Offset, axis2Offset] && !visited[axis1Offset, axis2Offset])
                    {
                        pos[0] = 0; pos[1] = 0; pos[2] = 0;
                        pos[axis1] = (short)(axis1Offset * blockSizeMultiplier);
                        pos[axis2] = (short)(axis2Offset * blockSizeMultiplier);
                        pos[axis] = (short)(main * blockSizeMultiplier);
                        ushort thisBlock = _getBlockIdAt(new ShortVector3(pos[0], pos[1], pos[2]), chunkPos, ref givenChunks);
                        short width = 1;
                        
                        while ((axis1Offset + width) < axis1Limit && mask[axis1Offset + width, axis2Offset] && !visited[axis1Offset + width, axis2Offset])
                        {
                            adj[0] = 0; adj[1] = 0; adj[2] = 0;
                            adj[axis1] = (short)((axis1Offset + width) * blockSizeMultiplier);
                            adj[axis2] = (short)(axis2Offset * blockSizeMultiplier);
                            adj[axis] = (short)(main * blockSizeMultiplier);
                            ushort adjBlock = _getBlockIdAt(new ShortVector3(adj[0], adj[1], adj[2]), chunkPos, ref givenChunks);
                            if (adjBlock != thisBlock)
                            {
                                break;
                            }
                            visited[axis1Offset + width, axis2Offset] = true;
                            width++;
                        }
                        
                        short height = 1;
                        bool done = false;
                        while (!done && (axis2Offset + height) < axis2Limit)
                        {
                            
                            for (ushort widthOffset = 0; widthOffset < width; widthOffset++)
                            {
                                adj[0] = 0; adj[1] = 0; adj[2] = 0;
                                adj[axis1] = (short)((axis1Offset + widthOffset) * blockSizeMultiplier);
                                adj[axis2] = (short)((axis2Offset + height) * blockSizeMultiplier);
                                adj[axis] = (short)(main * blockSizeMultiplier);
                                ushort adjBlock = _getBlockIdAt(new ShortVector3(adj[0], adj[1], adj[2]), chunkPos, ref givenChunks);
                                if (adjBlock != thisBlock || !mask[axis1Offset + widthOffset, axis2Offset + height] || visited[axis1Offset + widthOffset, axis2Offset + height])
                                {
                                    done = true;
                                    break;
                                }
                            }
                            if (!done)
                            {
                                for (short widthOffset = 0; widthOffset < width; widthOffset++) visited[axis1Offset + widthOffset, axis2Offset + height] = true;
                                height++;
                            }
                        }

                        /*for (short offsetX = 0; offsetX < width; offsetX++)
                        {
                            for (short offsetY = 0; offsetY < height; offsetY++)
                            {
                                visited[axis1Offset + offsetX, axis2Offset + offsetY] = true;
                            }
                        }*/
                        
                        //Creates a nice set of vertices based on the size of the created greedy rectangle
                        corners[0] = [0, 0];
                        corners[1] = [width, 0];
                        corners[2] = [width, height];
                        corners[3] = [0, height];
                        byte c = 0;
                        foreach (short[] corner in corners)
                        {
                            /*Again, another Vec3 in the form of an array, so that
                            we can do [axis]-like operations on it.*/
                            vert[0] = 0; vert[1] = 0; vert[2] = 0;
                            vert[axis] = (short)((main + (dir == 1 ? 1 : 0)) * blockSizeMultiplier);
                            vert[axis1] = (short)((axis1Offset + corner[0]) * blockSizeMultiplier);
                            vert[axis2] = (short)((axis2Offset + corner[1]) * blockSizeMultiplier);

                            vert[axis] = (short)Mathf.RoundToInt(vert[axis]);
                            vert[axis1] = (short)Mathf.RoundToInt(vert[axis1]);
                            vert[axis2] = (short)Mathf.RoundToInt(vert[axis2]);
                            faceVerts[c] = new ShortVector3(vert[0], vert[1], vert[2]);

                            short u = corner[0];
                            short v = corner[1];
                            /*Dont even ask why I need to do this. All that needs to be known is that this
                            flips the uv by 90 degrees. Why would we need this? Because some faces are rotated
                            90 degrees wrongly. Why? Idk.*/
                            if (axis == AXIS_X || axis == AXIS_Y)
                            {
                                u = corner[1];
                                v = corner[0];
                            }
                            if (thisBlock == 1)
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
                            else if (thisBlock == 2)
                            {
                                meshData.uv2s.Add(new ShortVector2(2, 0));
                            }
                            else if (thisBlock == 3)
                            {
                                meshData.uv2s.Add(new ShortVector2(3, 0));
                            }
                            else if (thisBlock == 4)
                            {
                                meshData.uv2s.Add(new ShortVector2(4, 0));
                            }
                            faceUvs[c] = new ShortVector2(u, v);
                            c++;
                        }
                        /*Proper winding order for positive/negative facing faces
                        Also a ton of different UV appending operations because for some
                        reason each dir/axis has a mind of its own for UV order. :\  */
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
    }

    byte _getSideNameFromAxisDir(sbyte axis, sbyte dir)
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
    
    bool _doesBlockExistAt(ShortVector3 blockPos, Vector2 chunkPos, ref ushort[][,,] givenChunks)
    {
        return _getBlockIdAt(blockPos, chunkPos, ref givenChunks) != 0;
    }

    ushort _getBlockIdAt(ShortVector3 blockPos, Vector2 chunkPos, ref ushort[][,,] givenChunks)
    {
        if (blockPos.y < 0 || blockPos.y >= CHUNK_SIZE_Y) return 0;

        bool exceedsChunkDims = false;
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
                return givenChunks[4][blockPos.x, blockPos.y, blockPos.z];
            }
            if (negXadj && givenChunks[3] != null)
            {
                return givenChunks[3][blockPos.x, blockPos.y, blockPos.z];
            }
            if (posZadj && givenChunks[1] != null)
            {
                return givenChunks[1][blockPos.x, blockPos.y, blockPos.z];
            }
            if (negZadj && givenChunks[2] != null)
            {
                return givenChunks[2][blockPos.x, blockPos.y, blockPos.z];
            }
            if (!chunkMap.TryGetValue(chunkPos, out ushort[,,] _chunkMap)) return 0;
            return _chunkMap[blockPos.x, blockPos.y, blockPos.z];
        }
        return givenChunks[0][blockPos.x, blockPos.y, blockPos.z];
    }
}
