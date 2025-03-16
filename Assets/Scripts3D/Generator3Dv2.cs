using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Graphs;

public class SimpleDungeonGenerator : MonoBehaviour
{
    [Header("Grid Settings")]
    public int gridWidth = 20;
    public int gridHeight = 10;
    public int gridDepth = 20;

    [Header("Dungeon Settings")]
    public int roomCount = 5;
    // Room templates must implement IRoomTemplate; assign at least one via the Inspector.
    public BasicRoomTemplate[] roomTemplates;

    [Header("Visualization Settings")]
    // How large (in world units) one grid cell appears.
    public float cellVisualSize = 2f;
    // Materials for edge visualization.
    public Material dashedMaterial;    // For MST edges not yet completed (dashed look)
    public Material completedMaterial; // For completed edges (orange)

    // The dungeon grid: each cell is either null, a room, or a hallway.
    private DungeonElement[,,] dungeonGrid;
    // List of all placed rooms.
    private List<DungeonRoom> placedRooms = new List<DungeonRoom>();
    // Edges computed using Delaunay triangulation (room-to–room, using centers).
    private List<RoomEdge> delaunayEdges = new List<RoomEdge>();
    // MST edges (subset of delaunayEdges, computed via Kruskal).
    private List<RoomEdge> mstEdges = new List<RoomEdge>();
    // Extra edges (from remaining delaunay edges with 10% chance).
    private List<RoomEdge> extraEdges = new List<RoomEdge>();

    // Container to hold all visual GameObjects.
    private GameObject visualContainer;
    // For tracking edge visualizations.
    private Dictionary<RoomEdge, LineRenderer> edgeLines = new Dictionary<RoomEdge, LineRenderer>();

    void Start()
    {
        dungeonGrid = new DungeonElement[gridWidth, gridHeight, gridDepth];
        visualContainer = new GameObject("DungeonVisual");
        StartCoroutine(GenerateDungeon());
    }

    // Regenerate dungeon on R key press.
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            ClearVisualizations();
            StartCoroutine(GenerateDungeon());
        }
    }

    void ClearVisualizations()
    {
        foreach (Transform child in visualContainer.transform)
        {
            Destroy(child.gameObject);
        }
        edgeLines.Clear();
        placedRooms.Clear();
        delaunayEdges.Clear();
        mstEdges.Clear();
        extraEdges.Clear();
        dungeonGrid = new DungeonElement[gridWidth, gridHeight, gridDepth];
    }

    IEnumerator GenerateDungeon()
    {
        if (roomTemplates == null || roomTemplates.Length == 0)
        {
            Debug.LogError("No room templates provided! Please assign at least one BasicRoomTemplate.");
            yield break;
        }

        // 1. Place rooms.
        yield return StartCoroutine(PlaceRooms());

        // 2. Compute Delaunay triangulation from room centers.
        ComputeDelaunayEdges();

        // 3. Compute MST (and extra edges) from Delaunay edges.
        ComputeMSTEdges();

        // Visualize the MST and extra edges.
        ClearEdgeVisualizations();

        foreach (RoomEdge edge in mstEdges)
            VisualizeEdge(edge, true);

        ComputeExtraEdges();

        // 4. Build hallways along each edge (choosing valid exits from each room).
        yield return StartCoroutine(DrawHallwaysForEdges());

        Debug.Log("Dungeon generation complete.");
    }

    IEnumerator PlaceRooms()
    {
        int attempts = 0;
        int placed = 0;
        int maxAttempts = 1000;
        while (placed < roomCount && attempts < maxAttempts)
        {
            attempts++;
            BasicRoomTemplate template = roomTemplates[Random.Range(0, roomTemplates.Length)];
            Vector3Int roomSize = template.GetRandomSize();
            int posX = Random.Range(0, gridWidth - roomSize.x + 1);
            int posY = Random.Range(0, gridHeight - roomSize.y + 1);
            int posZ = Random.Range(0, gridDepth - roomSize.z + 1);
            Vector3Int roomPos = new Vector3Int(posX, posY, posZ);

            if (IsAreaFree(roomPos, roomSize))
            {
                DungeonRoom room = new DungeonRoom(roomPos, roomSize, template);
                for (int x = roomPos.x; x < roomPos.x + roomSize.x; x++)
                    for (int y = roomPos.y; y < roomPos.y + roomSize.y; y++)
                        for (int z = roomPos.z; z < roomPos.z + roomSize.z; z++)
                            dungeonGrid[x, y, z] = room;
                placedRooms.Add(room);
                placed++;
                VisualizeRoom(room);
                Debug.Log("Placed room at " + roomPos + " size " + roomSize);
                yield return new WaitForSeconds(0.1f);
            }
        }
        if (placed < roomCount)
            Debug.LogWarning("Could only place " + placed + " rooms after " + attempts + " attempts.");
        yield return null;
    }

    bool IsAreaFree(Vector3Int pos, Vector3Int size)
    {
        for (int x = pos.x; x < pos.x + size.x; x++)
            for (int y = pos.y; y < pos.y + size.y; y++)
                for (int z = pos.z; z < pos.z + size.z; z++)
                    if (dungeonGrid[x, y, z] != null)
                        return false;
        return true;
    }

    void VisualizeRoom(DungeonRoom room)
    {
        for (int x = room.position.x; x < room.position.x + room.size.x; x++)
            for (int y = room.position.y; y < room.position.y + room.size.y; y++)
                for (int z = room.position.z; z < room.position.z + room.size.z; z++)
                {
                    Vector3 pos = new Vector3(x * cellVisualSize, y * cellVisualSize, z * cellVisualSize);
                    GameObject cellObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cellObj.transform.position = pos;
                    cellObj.transform.localScale = Vector3.one * cellVisualSize;
                    cellObj.GetComponent<Renderer>().material.color = Color.red;
                    cellObj.transform.parent = visualContainer.transform;
                }
    }

    // Returns the room center as a Vector3Int.
    Vector3Int GetRoomCenter(DungeonRoom room)
    {
        return room.position + new Vector3Int(room.size.x / 2, room.size.y / 2, room.size.z / 2);
    }

    int ManhattanDistance(Vector3Int a, Vector3Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z);
    }

    // --- Delaunay Triangulation Integration ---
    // We define a RoomVertex that implements the Vertex interface expected by Delaunay3D.
    public class RoomVertex : Vertex
    {
        public DungeonRoom room;
        public RoomVertex(Vector3 pos, DungeonRoom room) : base(pos)
        {
            this.room = room;
        }
    }

    // Compute the Delaunay triangulation for room centers and convert its edges to RoomEdge objects.
    void ComputeDelaunayEdges()
    {
        List<RoomVertex> vertices = new List<RoomVertex>();
        List<Vertex> vertexList = new List<Vertex>();
        foreach (DungeonRoom room in placedRooms)
        {
            Vector3Int center = GetRoomCenter(room);
            // Multiply by cellVisualSize if needed to match world coordinates.
            RoomVertex v = new RoomVertex(new Vector3(center.x, center.y, center.z), room);
            vertices.Add(v);
            vertexList.Add(v);
        }
        // Call the provided Delaunay3D triangulation (assumed available).
        Delaunay3D delaunay = Delaunay3D.Triangulate(vertexList);
        delaunayEdges.Clear();
        foreach (var e in delaunay.Edges)
        {
            // We assume e.U and e.V are RoomVertex instances.
            RoomVertex rv1 = e.U as RoomVertex;
            RoomVertex rv2 = e.V as RoomVertex;
            if (rv1 != null && rv2 != null)
            {
                RoomEdge re = new RoomEdge
                {
                    a = rv1.room,
                    b = rv2.room,
                    weight = ManhattanDistance(Vector3Int.RoundToInt(rv1.Position), Vector3Int.RoundToInt(rv2.Position)),
                    completed = false
                };
                // Avoid duplicates.
                if (!RoomEdgeExists(delaunayEdges, re))
                    delaunayEdges.Add(re);
            }
        }

        // Remove any existing edge visualizations.
        ClearEdgeVisualizations();

        // Visualize the Delaunay triangulation edges.
        foreach (RoomEdge edge in delaunayEdges)
            VisualizeEdge(edge, true);
    }

    bool RoomEdgeExists(List<RoomEdge> list, RoomEdge edge)
    {
        foreach (RoomEdge e in list)
        {
            if ((e.a == edge.a && e.b == edge.b) || (e.a == edge.b && e.b == edge.a))
                return true;
        }
        return false;
    }

    // --- MST and Extra Edge Computation ---
    // Here, we run Kruskal's algorithm on the Delaunay edges.
    void ComputeMSTEdges()
    {
        // We'll use the precomputed delaunayEdges as our candidate edge set.
        List<RoomEdge> edges = new List<RoomEdge>(delaunayEdges);
        edges.Sort((e1, e2) => e1.weight.CompareTo(e2.weight));

        // Union-find over rooms.
        Dictionary<DungeonRoom, DungeonRoom> parent = new Dictionary<DungeonRoom, DungeonRoom>();
        foreach (DungeonRoom room in placedRooms)
            parent[room] = room;
        System.Func<DungeonRoom, DungeonRoom> find = null;
        find = (DungeonRoom r) =>
        {
            if (parent[r] != r)
                parent[r] = find(parent[r]);
            return parent[r];
        };
        System.Action<DungeonRoom, DungeonRoom> union = (DungeonRoom a, DungeonRoom b) =>
        {
            DungeonRoom rootA = find(a);
            DungeonRoom rootB = find(b);
            parent[rootB] = rootA;
        };

        mstEdges.Clear();
        foreach (RoomEdge edge in edges)
        {
            if (find(edge.a) != find(edge.b))
            {
                mstEdges.Add(edge);
                union(edge.a, edge.b);
            }
        }
    }

    // For extra edges, we choose some edges from the Delaunay set that aren’t in the MST (with 10% chance).
    void ComputeExtraEdges()
    {
        extraEdges.Clear();
        foreach (RoomEdge edge in delaunayEdges)
        {
            if (Random.value < 0.10f)
                extraEdges.Add(edge);
        }
    }

    // --- Hallway Creation ---
    // For each edge (MST or extra), we now determine valid exit points for each room at the time of hallway creation.
    // The hallway will be built between the two chosen exits.
    IEnumerator DrawHallwaysForEdges()
    {
        // Process MST edges.
        for (int i = 0; i < mstEdges.Count; i++)
        {
            RoomEdge edge = mstEdges[i];
            Vector3Int centerA = GetRoomCenter(edge.a);
            Vector3Int centerB = GetRoomCenter(edge.b);
            RoomExit exitA = GetOrCreateExitForRoom(edge.a, centerB);
            RoomExit exitB = GetOrCreateExitForRoom(edge.b, centerA);
            yield return StartCoroutine(DrawHallway(exitA, exitB));
            edge.completed = true;
            edge.exitA = exitA;
            edge.exitB = exitB;
            mstEdges[i] = edge;
            UpdateEdgeVisualization(edge);
        }
        // Process extra edges.
        for (int i = 0; i < extraEdges.Count; i++)
        {
            RoomEdge edge = extraEdges[i];
            Vector3Int centerA = GetRoomCenter(edge.a);
            Vector3Int centerB = GetRoomCenter(edge.b);
            RoomExit exitA = GetOrCreateExitForRoom(edge.a, centerB);
            RoomExit exitB = GetOrCreateExitForRoom(edge.b, centerA);
            yield return StartCoroutine(DrawHallway(exitA, exitB));
            edge.completed = true;
            edge.exitA = exitA;
            edge.exitB = exitB;
            extraEdges[i] = edge;
            UpdateEdgeVisualization(edge);
        }
    }

    // Draws a hallway (blue cubes) along the Manhattan path between two exit positions.
    IEnumerator DrawHallway(RoomExit exitA, RoomExit exitB)
    {
        List<Vector3Int> path = GetManhattanPath(exitA.position, exitB.position);
        Vector3Int previousPos = path[0];
        for (int i = 1; i < path.Count - 1; i++)
        {
            Vector3Int pos = path[i];
            if (dungeonGrid[pos.x, pos.y, pos.z] == null)
            {
                DungeonHallway hallway = new DungeonHallway(pos);
                dungeonGrid[pos.x, pos.y, pos.z] = hallway;
                VisualizeHallway(pos);
            }

            // Detect corners and create new edges
            if (i > 1 && (pos.x != previousPos.x && pos.y != previousPos.y || pos.x != previousPos.x && pos.z != previousPos.z || pos.y != previousPos.y && pos.z != previousPos.z))
            {
                RoomEdge newEdge = new RoomEdge
                {
                    a = exitA.room,
                    b = exitB.room,
                    weight = ManhattanDistance(previousPos, pos),
                    completed = false
                };
                if (!RoomEdgeExists(delaunayEdges, newEdge))
                {
                    delaunayEdges.Add(newEdge);
                }
            }
            previousPos = pos;
        }
        yield return new WaitForSeconds(0.1f);

        // Recalculate Delaunay triangulation
        ComputeDelaunayEdges();
    }

    void VisualizeHallway(Vector3Int gridPos)
    {
        Vector3 pos = new Vector3(gridPos.x * cellVisualSize, gridPos.y * cellVisualSize, gridPos.z * cellVisualSize);
        GameObject cellObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cellObj.transform.position = pos;
        cellObj.transform.localScale = Vector3.one * cellVisualSize;
        cellObj.GetComponent<Renderer>().material.color = Color.blue;
        cellObj.transform.parent = visualContainer.transform;
    }

    // --- Exit Creation ---
    // When building a hallway for an edge, if a room doesn't have an exit yet, choose the candidate (from valid exit candidates)
    // that minimizes Manhattan distance to the target room center.
    RoomExit GetOrCreateExitForRoom(DungeonRoom room, Vector3Int target)
    {
        if (room.exits.Count > 0)
            return room.exits[0];
        List<Vector3Int> candidates = room.template.GetValidExitCandidates(room, dungeonGrid, gridWidth, gridHeight, gridDepth);
        if (candidates.Count == 0)
            return null;
        Vector3Int bestCandidate = candidates[0];
        int bestDist = ManhattanDistance(bestCandidate, target);
        for (int i = 1; i < candidates.Count; i++)
        {
            int d = ManhattanDistance(candidates[i], target);
            if (d < bestDist)
            {
                bestDist = d;
                bestCandidate = candidates[i];
            }
        }
        RoomExit newExit = new RoomExit(bestCandidate, room);
        room.exits.Add(newExit);
        VisualizeExit(newExit);
        return newExit;
    }

    void VisualizeExit(RoomExit exit)
    {
        Vector3 pos = new Vector3(exit.position.x * cellVisualSize, exit.position.y * cellVisualSize, exit.position.z * cellVisualSize);
        GameObject cellObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cellObj.transform.position = pos;
        cellObj.transform.localScale = Vector3.one * cellVisualSize;
        cellObj.GetComponent<Renderer>().material.color = Color.green;
        cellObj.transform.parent = visualContainer.transform;
    }

    // --- Edge Visualization ---
    // Visualize a room-to-room edge guideline as a line from room center to room center.
    // If the edge is not yet completed, use a dashed material; if completed, use the completed (orange) material.
    void VisualizeEdge(RoomEdge edge, bool forceCompleted)
    {
        GameObject edgeObj = new GameObject("Edge");
        edgeObj.transform.parent = visualContainer.transform;
        LineRenderer lr = edgeObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        Vector3 posA = new Vector3(GetRoomCenter(edge.a).x * cellVisualSize, GetRoomCenter(edge.a).y * cellVisualSize, GetRoomCenter(edge.a).z * cellVisualSize);
        Vector3 posB = new Vector3(GetRoomCenter(edge.b).x * cellVisualSize, GetRoomCenter(edge.b).y * cellVisualSize, GetRoomCenter(edge.b).z * cellVisualSize);
        lr.SetPosition(0, posA);
        lr.SetPosition(1, posB);
        lr.startWidth = cellVisualSize * 0.2f;
        lr.endWidth = cellVisualSize * 0.2f;
        lr.material = (edge.completed || forceCompleted) ? completedMaterial : dashedMaterial;
        lr.startColor = (edge.completed || forceCompleted) ? Color.Lerp(Color.red, Color.yellow, 0.5f) : Color.yellow;
        lr.endColor = lr.startColor;
        if (edgeLines.ContainsKey(edge))
            edgeLines[edge] = lr;
        else
            edgeLines.Add(edge, lr);
    }

    void UpdateEdgeVisualization(RoomEdge edge)
    {
        if (edgeLines.ContainsKey(edge))
        {
            LineRenderer lr = edgeLines[edge];
            lr.material = completedMaterial;
            lr.startColor = Color.Lerp(Color.red, Color.yellow, 0.5f);
            lr.endColor = lr.startColor;
        }
    }

    // Clears all edge (LineRenderer) visualizations.
    void ClearEdgeVisualizations()
    {
        List<Transform> toDelete = new List<Transform>();
        foreach (Transform child in visualContainer.transform)
        {
            if (child.name == "Edge")
                toDelete.Add(child);
        }
        foreach (Transform t in toDelete)
            Destroy(t.gameObject);
        edgeLines.Clear();
    }

    // Returns a Manhattan path (first along X, then Y, then Z) between two grid positions.
    List<Vector3Int> GetManhattanPath(Vector3Int start, Vector3Int end)
    {
        List<Vector3Int> path = new List<Vector3Int>();
        path.Add(start);
        Vector3Int current = start;
        while (current.x != end.x)
        {
            current.x += (end.x > current.x) ? 1 : -1;
            path.Add(new Vector3Int(current.x, current.y, current.z));
        }
        while (current.y != end.y)
        {
            current.y += (end.y > current.y) ? 1 : -1;
            path.Add(new Vector3Int(current.x, current.y, current.z));
        }
        while (current.z != end.z)
        {
            current.z += (end.z > current.z) ? 1 : -1;
            path.Add(new Vector3Int(current.x, current.y, current.z));
        }
        return path;
    }
}

#region Room Template and Helper Classes

// Vertex base class expected by Delaunay3D.
// public class Vertex
// {
//     public Vector3 Position { get; set; }
//     public Vertex(Vector3 pos)
//     {
//         Position = pos;
//     }
// }

// Interface for room templates.
public interface IRoomTemplate
{
    Vector3Int GetRandomSize();
    // Returns valid candidate exit positions (from faces and corners) for a room.
    List<Vector3Int> GetValidExitCandidates(DungeonRoom room, DungeonElement[,,] dungeonGrid, int gridWidth, int gridHeight, int gridDepth);
}

// BasicRoomTemplate returns candidates from the six face–centers plus eight corner–based candidates.
[System.Serializable]
public class BasicRoomTemplate : IRoomTemplate
{
    public string templateName;
    public Vector3Int minSize;
    public Vector3Int maxSize;

    public Vector3Int GetRandomSize()
    {
        int width = Random.Range(minSize.x, maxSize.x + 1);
        int height = Random.Range(minSize.y, maxSize.y + 1);
        int depth = Random.Range(minSize.z, maxSize.z + 1);
        return new Vector3Int(width, height, depth);
    }

    public List<Vector3Int> GetValidExitCandidates(DungeonRoom room, DungeonElement[,,] dungeonGrid, int gridWidth, int gridHeight, int gridDepth)
    {
        List<Vector3Int> candidates = new List<Vector3Int>();

        // Face-center candidates.
        Vector3Int leftCandidate = new Vector3Int(room.position.x - 1, room.position.y + room.size.y / 2, room.position.z + room.size.z / 2);
        if (IsValidExitCandidate(room, leftCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(leftCandidate);

        Vector3Int rightCandidate = new Vector3Int(room.position.x + room.size.x, room.position.y + room.size.y / 2, room.position.z + room.size.z / 2);
        if (IsValidExitCandidate(room, rightCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(rightCandidate);

        Vector3Int bottomCandidate = new Vector3Int(room.position.x + room.size.x / 2, room.position.y - 1, room.position.z + room.size.z / 2);
        if (IsValidExitCandidate(room, bottomCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(bottomCandidate);

        Vector3Int topCandidate = new Vector3Int(room.position.x + room.size.x / 2, room.position.y + room.size.y, room.position.z + room.size.z / 2);
        if (IsValidExitCandidate(room, topCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(topCandidate);

        Vector3Int backCandidate = new Vector3Int(room.position.x + room.size.x / 2, room.position.y + room.size.y / 2, room.position.z - 1);
        if (IsValidExitCandidate(room, backCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(backCandidate);

        Vector3Int frontCandidate = new Vector3Int(room.position.x + room.size.x / 2, room.position.y + room.size.y / 2, room.position.z + room.size.z);
        if (IsValidExitCandidate(room, frontCandidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
            candidates.Add(frontCandidate);

        // Corner candidates.
        Vector3Int center = room.position + new Vector3Int(room.size.x / 2, room.size.y / 2, room.size.z / 2);
        List<Vector3Int> roomCorners = new List<Vector3Int>();
        roomCorners.Add(room.position);
        roomCorners.Add(new Vector3Int(room.position.x + room.size.x, room.position.y, room.position.z));
        roomCorners.Add(new Vector3Int(room.position.x, room.position.y + room.size.y, room.position.z));
        roomCorners.Add(new Vector3Int(room.position.x + room.size.x, room.position.y + room.size.y, room.position.z));
        roomCorners.Add(new Vector3Int(room.position.x, room.position.y, room.position.z + room.size.z));
        roomCorners.Add(new Vector3Int(room.position.x + room.size.x, room.position.y, room.position.z + room.size.z));
        roomCorners.Add(new Vector3Int(room.position.x, room.position.y + room.size.y, room.position.z + room.size.z));
        roomCorners.Add(new Vector3Int(room.position.x + room.size.x, room.position.y + room.size.y, room.position.z + room.size.z));

        foreach (Vector3Int corner in roomCorners)
        {
            int offsetX = (corner.x - center.x) >= 0 ? 1 : -1;
            int offsetY = (corner.y - center.y) >= 0 ? 1 : -1;
            int offsetZ = (corner.z - center.z) >= 0 ? 1 : -1;
            Vector3Int candidate = new Vector3Int(corner.x + offsetX, corner.y + offsetY, corner.z + offsetZ);
            if (IsValidExitCandidate(room, candidate, dungeonGrid, gridWidth, gridHeight, gridDepth))
                candidates.Add(candidate);
        }
        return candidates;
    }

    bool IsValidExitCandidate(DungeonRoom room, Vector3Int candidate, DungeonElement[,,] dungeonGrid, int gridWidth, int gridHeight, int gridDepth)
    {
        if (candidate.x < 0 || candidate.x >= gridWidth ||
            candidate.y < 0 || candidate.y >= gridHeight ||
            candidate.z < 0 || candidate.z >= gridDepth)
            return false;
        if (dungeonGrid[candidate.x, candidate.y, candidate.z] != null)
            return false;
        // Candidate must lie exactly one cell outside a face of the room.
        bool onFace = false;
        if (candidate.x == room.position.x - 1 &&
            candidate.y >= room.position.y && candidate.y < room.position.y + room.size.y &&
            candidate.z >= room.position.z && candidate.z < room.position.z + room.size.z)
            onFace = true;
        else if (candidate.x == room.position.x + room.size.x &&
            candidate.y >= room.position.y && candidate.y < room.position.y + room.size.y &&
            candidate.z >= room.position.z && candidate.z < room.position.z + room.size.z)
            onFace = true;
        else if (candidate.y == room.position.y - 1 &&
            candidate.x >= room.position.x && candidate.x < room.position.x + room.size.x &&
            candidate.z >= room.position.z && candidate.z < room.position.z + room.size.z)
            onFace = true;
        else if (candidate.y == room.position.y + room.size.y &&
            candidate.x >= room.position.x && candidate.x < room.position.x + room.size.x &&
            candidate.z >= room.position.z && candidate.z < room.position.z + room.size.z)
            onFace = true;
        else if (candidate.z == room.position.z - 1 &&
            candidate.x >= room.position.x && candidate.x < room.position.x + room.size.x &&
            candidate.y >= room.position.y && candidate.y < room.position.y + room.size.y)
            onFace = true;
        else if (candidate.z == room.position.z + room.size.z &&
            candidate.x >= room.position.x && candidate.x < room.position.x + room.size.x &&
            candidate.y >= room.position.y && candidate.y < room.position.y + room.size.y)
            onFace = true;
        return onFace;
    }
}

// Base dungeon element.
public abstract class DungeonElement { }

public class DungeonRoom : DungeonElement
{
    public Vector3Int position;
    public Vector3Int size;
    public IRoomTemplate template;
    public List<RoomExit> exits = new List<RoomExit>();

    public DungeonRoom(Vector3Int pos, Vector3Int size, IRoomTemplate template)
    {
        this.position = pos;
        this.size = size;
        this.template = template;
    }
}

public class DungeonHallway : DungeonElement
{
    public Vector3Int position;
    public DungeonHallway(Vector3Int pos)
    {
        this.position = pos;
    }
}

// Associates an exit position with its room.
public class RoomExit
{
    public Vector3Int position;
    public DungeonRoom room;
    public RoomExit(Vector3Int pos, DungeonRoom room)
    {
        this.position = pos;
        this.room = room;
    }
}

// Represents an edge between two rooms.
public struct RoomEdge
{
    public DungeonRoom a;
    public DungeonRoom b;
    public int weight;
    public bool completed;
    public RoomExit exitA; // Valid exit chosen for room a when hallway is built.
    public RoomExit exitB; // Valid exit chosen for room b.
}

#endregion
