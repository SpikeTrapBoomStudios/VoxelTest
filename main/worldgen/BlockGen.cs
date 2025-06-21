using Godot;
using System;
using Godot.Collections;
using IntArray3D = Godot.Collections.Array<Godot.Collections.Array<int[]>>;
using System.Threading;
using System.Numerics;
using Vector2 = Godot.Vector2;
using Vector3 = Godot.Vector3;
using Godot.NativeInterop;
using System.Linq;
using System.Collections.Generic;
using GodotDict = Godot.Collections.Dictionary;
using SysGeneric = System.Collections.Generic;
using Array = Godot.Collections.Array;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;

[Tool]
[GlobalClass]
public partial class BlockGen : Node
{
    [Export] FastNoiseLite noiseLite = new FastNoiseLite();
    [Export] FastNoiseLite noiseLite2 = new FastNoiseLite();
    [Export] int chunkCount = 1;
    [Export] int CHUNK_SIZE_X = 32;
    [Export] int CHUNK_SIZE_Z = 32;
    [Export] int CHUNK_SIZE_Y = 250;
    [Export] int terrainScale = 10;
    [Export] int blockSizeMultiplier = 2;

    private bool _generate = false;
    [Export]
    public bool generate
    {
        get { return false; }
        set
        {
            if (IsInsideTree() && Engine.IsEditorHint())
            {
                foreach (MeshInstance3D mesh in meshes)
                {
                    mesh.QueueFree();
                }
                genThread = null;
                meshes.Clear();
                chunkMap = new ConcurrentDictionary<Vector2,int[,,]>();
                stopwatch = new();
                _generateMesh();
            }
        }
    }
    List<MeshInstance3D> meshes = new List<MeshInstance3D>();

    private ConcurrentDictionary<Vector2,int[,,]> chunkMap = new ConcurrentDictionary<Vector2,int[,,]>();

    Thread genThread;

    Stopwatch stopwatch = new();

    void _generateMesh()
    {
        stopwatch.Start();
        genThread = new Thread(new ThreadStart(_threadedGenerateChunk));
        genThread.Start();
    }

    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
        {
            _generateMesh();
        }
        base._Ready();
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
                Parallel.For(0, chunkCount, z => _populateChunk(new Vector2(x, z)))
            );
            Parallel.For(0, chunkCount, x =>
                Parallel.For(0, chunkCount, z => _chunkGeneration(new Vector2(x, z)))
            );
        }
        catch (Exception e)
        {
            GD.Print(e);
        }
        stopwatch.Stop();
        GD.Print(stopwatch.ElapsedMilliseconds);
    }

    void _populateChunk(Vector2 chunkPos)
    {
        var newBlockMap = _populateBlockMap(chunkPos);
        chunkMap[chunkPos] = newBlockMap;
    }

    void _chunkGeneration(Vector2 chunkPos)
    {
        var newMeshData = _createVerts(chunkPos);

        GodotDict godotCompatMeshData = new GodotDict();

        Vector3[] vertices = ((List<Vector3>)newMeshData["vertices"]).ToArray();
        godotCompatMeshData.Add("vertices", vertices);
        int[] indices = ((List<int>)newMeshData["indices"]).ToArray();
        godotCompatMeshData.Add("indices", indices);
        Vector2[] uvs = ((List<Vector2>)newMeshData["uvs"]).ToArray();
        godotCompatMeshData.Add("uvs", uvs);
        Vector2[] uv2s = ((List<Vector2>)newMeshData["uv2s"]).ToArray();
        godotCompatMeshData.Add("uv2s", uv2s);

        CallDeferred("_createChunkMeshFromData", godotCompatMeshData, chunkPos);
    }

    void _createChunkMeshFromData(Dictionary data, Vector2 chunkPos)
    {
        try
        {
            Array array = [];
            array.Resize((int) Mesh.ArrayType.Max);
            array[(int)Mesh.ArrayType.Vertex] = data["vertices"];
            array[(int)Mesh.ArrayType.Index] = data["indices"];
            array[(int)Mesh.ArrayType.TexUV] = data["uvs"];
            array[(int)Mesh.ArrayType.TexUV2] = data["uv2s"];
            var arrayMesh = new ArrayMesh();
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, array);
            var mesh = new MeshInstance3D();
            mesh.Mesh = arrayMesh;
            AddChild(mesh);
            if (Engine.IsEditorHint())
            {
                mesh.Owner = GetTree().EditedSceneRoot;
            }
            mesh.MaterialOverride = (Material) ResourceLoader.Load("res://resources/materials/voxel_material_main.tres");
            mesh.Position = new Vector3(chunkPos.X * CHUNK_SIZE_X, 0, chunkPos.Y * CHUNK_SIZE_Z);
            meshes.Add(mesh);
        }
        catch (Exception ex)
        {
            GD.PrintErr("Exception in _createChunkMeshFromData: ", ex.ToString());
        }
    }

    int[,,] _populateBlockMap(Vector2 chunkPos)
    {
        int[,,] blockMap = new int[CHUNK_SIZE_X / blockSizeMultiplier, CHUNK_SIZE_Y / blockSizeMultiplier, CHUNK_SIZE_Z / blockSizeMultiplier];
        for (int x = 0; x < CHUNK_SIZE_X / blockSizeMultiplier; x++)
        {
            int chunkOffsetX = x * blockSizeMultiplier + (int)chunkPos.X * CHUNK_SIZE_X;
            for (int y = 0; y < CHUNK_SIZE_Y / blockSizeMultiplier; y++)
            {
                for (int z = 0; z < CHUNK_SIZE_Z / blockSizeMultiplier; z++)
                {
                    int chunkOffsetZ = z * blockSizeMultiplier + (int)chunkPos.Y * CHUNK_SIZE_Z;

                    float rawHeight = noiseLite.GetNoise2D(chunkOffsetX, chunkOffsetZ);
                    rawHeight = (rawHeight + 1) / 2;

                    float surfaceY = rawHeight * terrainScale;
                    surfaceY = Mathf.Pow(2, surfaceY);
                    surfaceY = Math.Clamp(surfaceY, 0, CHUNK_SIZE_Y - 1);

                    surfaceY = Mathf.Round(surfaceY);

                    int correctedY = y * blockSizeMultiplier;
                    int toleratedRange = (int) (correctedY - surfaceY);
                    int blockId;
                    if (toleratedRange > 0)
                    {
                        blockId = 0;
                    }
                    else if (Math.Abs(toleratedRange) < blockSizeMultiplier)
                    {
                        blockId = 1;
                    }
                    else
                    {
                        blockId = 2;
                    }
                    blockMap[x, y, z] = blockId;
                }
            }
        }
        return blockMap;
    }

    const int AXIS_X = 0;
    const int AXIS_Y = 1;
    const int AXIS_Z = 2;

    const int DIR_POS = 1;
    const int DIR_NEG = -1;

    SysGeneric.Dictionary<string, Object> _createVerts(Vector2 chunkPos)
    {
        int c = 0;

        List<SysGeneric.Dictionary<string, Object>> faceDefinitions = [
            new SysGeneric.Dictionary<string, Object>{
                { "name", "right" },
                { "axis", AXIS_X },
                { "dir", DIR_POS }
            },
            new SysGeneric.Dictionary<string, Object>{
                { "name", "left" },
                { "axis", AXIS_X },
                { "dir", DIR_NEG }
            },
            new SysGeneric.Dictionary<string, Object>{
                { "name", "top" },
                { "axis", AXIS_Y },
                { "dir", DIR_POS }
            },
            new SysGeneric.Dictionary<string, Object>{
                { "name", "bottom" },
                { "axis", AXIS_Y },
                { "dir", DIR_NEG }
            },
            new SysGeneric.Dictionary<string, Object>{
                { "name", "front" },
                { "axis", AXIS_Z },
                { "dir", DIR_POS }
            },
            new SysGeneric.Dictionary<string, Object>{
                { "name", "back" },
                { "axis", AXIS_Z },
                { "dir", DIR_NEG }
            }
        ];

        SysGeneric.Dictionary<string, Object> thisMeshData = new SysGeneric.Dictionary<string, Object> {
            {"vertices", new List<Vector3>()},
            {"indices", new List<int>()},
            {"uvs", new List<Vector2>()},
            {"uv2s", new List<Vector2>()}
        };

        foreach (SysGeneric.Dictionary<string, Object> face in faceDefinitions)
        {
            SysGeneric.Dictionary<string, Object> newMeshData = _greedyMeshFace(
                (int)face["axis"],
                (int)face["dir"],
                c,
                chunkPos
            );
            c = (int)newMeshData["c"];
            ((List<Vector3>)thisMeshData["vertices"]).AddRange((List<Vector3>)newMeshData["vertices"]);
            ((List<int>)thisMeshData["indices"]).AddRange((List<int>)newMeshData["indices"]);
            ((List<Vector2>)thisMeshData["uvs"]).AddRange((List<Vector2>)newMeshData["uvs"]);
            ((List<Vector2>)thisMeshData["uv2s"]).AddRange((List<Vector2>)newMeshData["uv2s"]);
        }

        return thisMeshData;
    }

    int[] _getFaceAxes(int axis)
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

    SysGeneric.Dictionary<string, Object> _greedyMeshFace(int axis, int dir, int c, Vector2 chunkPos)
    {
        SysGeneric.Dictionary<string, Object> thisMeshData = new SysGeneric.Dictionary<string, Object> {
            {"c", c},
            {"vertices", new List<Vector3>()},
            {"indices", new List<int>()},
            {"uvs", new List<Vector2>()},
            {"uv2s", new List<Vector2>()}
        };

        int[] size = [CHUNK_SIZE_X / blockSizeMultiplier, CHUNK_SIZE_Y / blockSizeMultiplier, CHUNK_SIZE_Z / blockSizeMultiplier];
        int[] axies = _getFaceAxes(axis);
        int axis1 = axies[0];
        int axis2 = axies[1];

        int main_limit = size[axis];
        int axis1_limit = size[axis1];
        int axis2_limit = size[axis2];

        for (var main = 0; main < main_limit; main++)
        {
            /*This is the mask for-loop. Its job is to populate an array of equal size/organization as the
            chunkMap array, except instead of blocks it is true/false values of whether that block should
            show its face or not, on this side/axis.*/
            bool[,] mask = new bool[axis1_limit, axis2_limit];
            for (var i = 0; i < axis1_limit; i++)
            {
                for (var j = 0; j < axis2_limit; j++)
                {
                    /*Position is represented as an array so that we can perform [axis]-like
                    operations on it. More modularity essentially.*/
                    int[] pos = [0, 0, 0];
                    pos[axis] = main * blockSizeMultiplier;
                    pos[axis1] = i * blockSizeMultiplier;
                    pos[axis2] = j * blockSizeMultiplier;

                    int[] adj = [pos[0], pos[1], pos[2]];
                    adj[axis] += dir * blockSizeMultiplier; /*Moves the block position in the direction of the axis * dir.
                                       So if the axis = 0 (x axis) and dir = -1, moves x-1 blocks.*/

                    bool current_block_exists = _doesBlockExistAt(new Vector3(pos[0], pos[1], pos[2]), chunkPos);
                    bool adjacent_block_exists = _doesBlockExistAt(new Vector3(adj[0], adj[1], adj[2]), chunkPos);

                    /*Appends a true/false value based on whether the current block is air AND the
                    adjacent block is not air*/
                    mask[i, j] = current_block_exists && !adjacent_block_exists;
                }
            }

            bool[,] visited = new bool[axis1_limit, axis2_limit];
            for (var i = 0; i < axis1_limit; i++)
            {
                for (var j = 0; j < axis2_limit; j++)
                    visited[i, j] = false;
            }


            for (int axis1_offset = 0; axis1_offset < axis1_limit; axis1_offset++)
            {
                for (int axis2_offset = 0; axis2_offset < axis2_limit; axis2_offset++)
                {
                    if (mask[axis1_offset, axis2_offset] && !visited[axis1_offset, axis2_offset])
                    {
                        int[] this_pos = [0, 0, 0];
                        this_pos[axis1] = axis1_offset * blockSizeMultiplier;
                        this_pos[axis2] = axis2_offset * blockSizeMultiplier;
                        this_pos[axis] = main * blockSizeMultiplier;
                        int thisBlock = _getBlockIdAt(new Vector3(this_pos[0], this_pos[1], this_pos[2]), chunkPos);
                        int width = 1;
                        while ((axis1_offset + width) < axis1_limit && mask[axis1_offset + width, axis2_offset] && !visited[axis1_offset + width, axis2_offset])
                        {
                            int[] adj_pos = [0, 0, 0];
                            adj_pos[axis1] = (axis1_offset + width) * blockSizeMultiplier;
                            adj_pos[axis2] = axis2_offset * blockSizeMultiplier;
                            adj_pos[axis] = main * blockSizeMultiplier;
                            int adj_block = _getBlockIdAt(new Vector3(adj_pos[0], adj_pos[1], adj_pos[2]), chunkPos);
                            if (adj_block != thisBlock)
                            {
                                break;
                            }
                            width++;
                        }
                        
                        int height = 1;
                        bool done = false;
                        while (!done && (axis2_offset + height) < axis2_limit)
                        {
                            for (int width_offset = 0; width_offset < width; width_offset++)
                            {
                                int[] adj_pos = [0, 0, 0];
                                adj_pos[axis1] = (axis1_offset + width_offset) * blockSizeMultiplier;
                                adj_pos[axis2] = (axis2_offset + height) * blockSizeMultiplier;
                                adj_pos[axis] = main * blockSizeMultiplier;
                                var adj_block = _getBlockIdAt(new Vector3(adj_pos[0], adj_pos[1], adj_pos[2]), chunkPos);
                                if (adj_block != thisBlock) 
                                {
                                    done = true;
                                    break;
                                }
                                if (!mask[axis1_offset + width_offset, axis2_offset + height] || visited[axis1_offset + width_offset, axis2_offset + height])
                                {
                                    done = true;
                                    break;
                                }
                            }
                            if (!done)
                            {
                                height++;
                            }
                        }


                        for (int offsetX=0; offsetX < width; offsetX++)
                        {
                            for (int offsetY=0; offsetY < height; offsetY++)
                            {
                                visited[axis1_offset + offsetX, axis2_offset + offsetY] = true;
                            }
                        }

                        string side_name = _getSideNameFromAxisDir(axis, dir);
                        List<Vector3> face_verts = new List<Vector3>();
                        List<Vector2> face_uvs = new List<Vector2>();
                        //Creates a nice set of vertices based on the size of the created greedy rectangle
                        
                        int[][] corners = [
                            [0, 0],
                            [width, 0],
                            [width, height],
                            [0, height]
                        ];
                        foreach (int[] corner in corners)
                        {
                            int[] vert = [0, 0, 0]; /*Again, another Vec3 in the form of an array, so that
                                                         we can do [axis]-like operations on it.*/
                            vert[axis] = (main + (dir == 1 ? 1 : 0)) * blockSizeMultiplier;
                            vert[axis1] = (axis1_offset + corner[0]) * blockSizeMultiplier;
                            vert[axis2] = (axis2_offset + corner[1]) * blockSizeMultiplier;

                            vert[axis] = Mathf.RoundToInt(vert[axis]);
                            vert[axis1] = Mathf.RoundToInt(vert[axis1]);
                            vert[axis2] = Mathf.RoundToInt(vert[axis2]);
                            face_verts.Add(new Vector3(vert[0], vert[1], vert[2])); //Convert back to vec3 from array
                            
                            int u = corner[0];
                            int v = corner[1];
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
                                if (side_name == "top")
                                {
                                    ((List<Vector2>) thisMeshData["uv2s"]).Add(new Vector2(0, 0));
                                }
                                else if (side_name == "left" || side_name == "right" || side_name == "front" || side_name == "back")
                                {
                                    ((List<Vector2>) thisMeshData["uv2s"]).Add(new Vector2(1, 0));
                                }
                                else
                                {
                                    ((List<Vector2>) thisMeshData["uv2s"]).Add(new Vector2(2, 0));
                                }
                            }
                            else if (thisBlock == 2)
                            {
                                ((List<Vector2>) thisMeshData["uv2s"]).Add(new Vector2(2, 0));
                            }
                            face_uvs.Add(new Vector2(u, v));
                        }
                        
                        /*Proper winding order for positive/negative facing faces
                        Also a ton of different UV appending operations because for some
                        reason each dir/axis has a mind of its own for UV order. :\  */
                        if (dir == DIR_POS)
                        {
                            ((List<Vector3>) thisMeshData["vertices"]).AddRange([face_verts[0], face_verts[1], face_verts[2], face_verts[3]]);
                            if (axis == AXIS_Z)
                            {
                                ((List<Vector2>) thisMeshData["uvs"]).AddRange([face_uvs[3],face_uvs[2],face_uvs[1],face_uvs[0]]);
                            }
                            else
                            {
                                ((List<Vector2>) thisMeshData["uvs"]).AddRange([face_uvs[2], face_uvs[3], face_uvs[0], face_uvs[1]]);
                            }
                        }
                        else
                        {
                            ((List<Vector3>) thisMeshData["vertices"]).AddRange([face_verts[0], face_verts[3], face_verts[2], face_verts[1]]);
                            if (axis == AXIS_X)
                            {
                                ((List<Vector2>) thisMeshData["uvs"]).AddRange([face_uvs[1], face_uvs[2], face_uvs[3], face_uvs[0]]);
                            }
                            else if (axis == AXIS_Z)
                            {
                                ((List<Vector2>) thisMeshData["uvs"]).AddRange([face_uvs[2],face_uvs[1],face_uvs[0],face_uvs[3]]);
                            }
                            else
                            {
                                ((List<Vector2>) thisMeshData["uvs"]).AddRange([face_uvs[3],face_uvs[0],face_uvs[1],face_uvs[2]]);
                            }
                        }
                        int ic = (int) thisMeshData["c"];
                        ((List<int>)thisMeshData["indices"]).AddRange([
                                ic+2, ic + 1, ic,
                                ic+3, ic + 2, ic
                            ]);
                        thisMeshData["c"] = (int)thisMeshData["c"] + 4;
                    }   
                }   
            }
        }
        return thisMeshData;
    }

    string _getSideNameFromAxisDir(int axis, int dir)
    {
        if (dir == DIR_POS)
        {
            switch (axis)
            {
                case AXIS_X: return "right";
                case AXIS_Y: return "top";
                case AXIS_Z: return "front";
            }
        }
        else
        {
            switch (axis)
            {
                case AXIS_X: return "left";
                case AXIS_Y: return "bottom";
                case AXIS_Z: return "back";
            }
        }
        return "front";
    }
    
    bool _doesBlockExistAt(Vector3 blockPos, Vector2 chunkPos)
    {
        return _getBlockIdAt(blockPos, chunkPos) != 0;
    }

    int _getBlockIdAt(Vector3 blockPos, Vector2 chunkPos)
    {
        int chunkOffsetX = (int) (blockPos.X + chunkPos.X * CHUNK_SIZE_X);
        int chunkOffsetY = (int) blockPos.Y;
        int chunkOffsetZ = (int) (blockPos.Z + chunkPos.Y * CHUNK_SIZE_Z);

        Vector2 adjChunkPos = Vector2.Zero;
        if (blockPos.X < 0 || blockPos.X >= CHUNK_SIZE_X || blockPos.Y < 0 || blockPos.Y >= CHUNK_SIZE_Y || blockPos.Z < 0 || blockPos.Z >= CHUNK_SIZE_Z)
        {
            int adjXswitch = blockPos.X >= CHUNK_SIZE_X ? 1 : 0;
            int adjZswitch = blockPos.Z >= CHUNK_SIZE_Z ? 1 : 0;
            if (adjXswitch == 0 && adjZswitch == 0)
            {
                return 0;
            }
            else
            {
                adjChunkPos = chunkPos + new Vector2(adjXswitch, adjZswitch);
            }
        }
        
        Vector2 _chunk_pos = adjChunkPos.LengthSquared() == 0 ? chunkPos : adjChunkPos;
        int[,,] _chunk_map;
        bool chunkExists = chunkMap.TryGetValue(_chunk_pos, out _chunk_map);
        if (!chunkExists)
        {
            return 0;
        }

        if (chunkOffsetX < 0 || chunkOffsetZ < 0 || chunkOffsetY < 0 || chunkOffsetY >= CHUNK_SIZE_Y)
        {
            return 0;
        }
        int correctedBlockX = Mathf.RoundToInt(blockPos.X / blockSizeMultiplier);
        correctedBlockX = Mathf.Clamp(correctedBlockX, 0, _chunk_map.GetLength(0)-1);
        int correctedBlockY = Mathf.RoundToInt(blockPos.Y / blockSizeMultiplier);
        correctedBlockY = Mathf.Clamp(correctedBlockY, 0, _chunk_map.GetLength(1)-1);
        int correctedBlockZ = Mathf.RoundToInt(blockPos.Z / blockSizeMultiplier);
        correctedBlockZ = Mathf.Clamp(correctedBlockZ, 0, _chunk_map.GetLength(2)-1);
        return _chunk_map[correctedBlockX, correctedBlockY, correctedBlockZ];
    }
}
