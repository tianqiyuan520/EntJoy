using EntJoy;
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;




public unsafe partial struct MoveSystem2 : ISystem<Position, Vel, QTComp>
{
    public static Vector2 viewportSize;
    public static float dt;

    public void Execute(ref Entity entity, ref Position pos, ref Vel vel, ref QTComp qtcomp)
    {
        pos.pos.X += vel.vel.X * dt;
        pos.pos.Y += vel.vel.Y * dt;
        if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) vel.vel.X *= -1;
        if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) vel.vel.Y *= -1;
        qtcomp.X = (int)pos.pos.X;
        qtcomp.Y = (int)pos.pos.Y;
    }
}
public partial struct DisplayLimitedEntityPosSystem2 : ISystem<Position, QTComp>
{
    public int EntityCount;
    public int Count;
    public int limitCount;

    public void Execute(ref Entity entity, ref Position pos, ref QTComp qtcomp)
    {
        if (Count > limitCount) return;
        GD.Print(entity.Id, "/", EntityCount, " ", pos.pos, " ", qtcomp.X, " ", qtcomp.Y, " ", qtcomp.Width, " ", qtcomp.Height);
        Count++;
    }
}

public partial class QuadTree : Node2D
{

    [Export]
    public Node MultiMeshgroup;
    [Export]
    public PackedScene packedScene;
    public MultiMeshInstance2D[] multiMeshInstances;
    public int SpawnMultiMeshCount = 1; // MultiMesh 个数
                                        // 每个MultiMesh的实体数量
    public int ENTITIES_PER_MESH = 10000;
    public int EntityCount = 1000;
    public World myWorld;
    public Rect2 viewportRect;
    public const int GODOT_FLOATS_PER_INSTANCE = 8;

    public MultiMeshInstance2D GenerateMultiMesh()
    {
        var multiMeshInstance2D = packedScene.Instantiate<MultiMeshInstance2D>();
        MultiMeshgroup.AddChild(multiMeshInstance2D);
        return multiMeshInstance2D;
    }



    public void CreateWorld()
    {
        myWorld = new World();
        World_Recorder.RecordWorld(myWorld);
        GD.Print($"创建世界成功; 当前世界总数:{World_Recorder.worldList.Count}");
    }

    public QTree<QTComp> qTree = new QTree<QTComp>();
    public override void _Ready()
    {
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("UpdateTree").Pressed += UpdateTree;
        GetNode<Timer>("Timer").Timeout += UpdateTree;
        //multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        //multiMeshInstance.Multimesh.InstanceCount = 30000;

        multiMeshInstances = new MultiMeshInstance2D[SpawnMultiMeshCount];

        for (int i = 0; i < SpawnMultiMeshCount; i++)
        {
            multiMeshInstances[i] = GenerateMultiMesh();
        }
        if (SpawnMultiMeshCount != 0) ENTITIES_PER_MESH = EntityCount / SpawnMultiMeshCount;
        // 初始化所有MultiMesh实例
        for (int i = 0; i < SpawnMultiMeshCount; i++)
        {
            multiMeshInstances[i].Multimesh.InstanceCount = ENTITIES_PER_MESH;
        }
        GD.Print(multiMeshInstances.Length, " ", "ENTITIES_PER_MESH:", ENTITIES_PER_MESH, " ", "EntityCount:", EntityCount);
        viewportRect = GetViewportRect();
        //moveSystemSIMD.viewportSize = viewportRect.Size;

        MoveSystem2.viewportSize = viewportRect.Size;

        qTree.Init();
        qTree.CreateRoot();
        qTree.InitRect(viewportRect.Position.X + viewportRect.Size.X / 2,
            viewportRect.Position.Y + viewportRect.Size.Y / 2,
            viewportRect.Size.X, viewportRect.Size.Y);
    }

    public void NewEntity()
    {
        for (int i = 0; i < EntityCount; i++)
        {
            var entity = myWorld.NewEntity(typeof(Position), typeof(Vel), typeof(QTComp));
            myWorld.AddComponent(entity, new Position()
            {
                pos = new Vector2(11, 11)
            });
            myWorld.AddComponent(entity, new Vel()
            {
                vel = new Vector2
                (
                    (float)GD.RandRange(100.0, 200.0),
                    (float)GD.RandRange(-200.0, 200.0)
                )
            });
            myWorld.AddComponent(entity, new QTComp()
            {
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1
            });
        }
        GD.Print($"NewEntity Success ");

        RenderingInit();
    }

    public void Display()
    {
        var queryBuilder = new QueryBuilder().WithAll<Position, QTComp>();
        GD.Print("Display:");
        DisplayLimitedEntityPosSystem2 displayLimitedEntityPosSystem2 = new() { EntityCount = EntityCount, Count = 0, limitCount = 100 };
        myWorld.Query(queryBuilder, displayLimitedEntityPosSystem2);

    }

    public QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position, Vel, QTComp>();
    public QueryBuilder queryBuilder2 = new QueryBuilder().WithAll<Position, QTComp>();
    public void RenderingInit()
    {
        if (multiMeshInstances.Length == 0) return;
        List<Position> poses = new();
        GetAllEntityPosSystem getAllEntityPosSystem = new();
        getAllEntityPosSystem.poses = poses;
        myWorld.Query(queryBuilder, getAllEntityPosSystem);
        poses = getAllEntityPosSystem.poses;


        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
        {
            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
            if (multiMesh == null) continue;

            var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
            QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position>();

            int startIndex = meshIndex * ENTITIES_PER_MESH;
            int endIndex = startIndex + ENTITIES_PER_MESH;

            int bufferIndex = 0;
            for (int entityIndex = startIndex; entityIndex < endIndex; entityIndex++)
            {
                if (entityIndex >= poses.Count) break;
                int baseIndex = bufferIndex * GODOT_FLOATS_PER_INSTANCE;
                float rotation = 0.0f;
                float cosX = Mathf.Cos(rotation);
                float sinX = Mathf.Sin(rotation);
                // 根据最新格式要求填充 (x.x, y.x, padding, origin.x, x.y, y.y, padding, origin.y)
                buffer[baseIndex] = cosX;    // x.x
                buffer[baseIndex + 1] = -sinX;   // y.x
                buffer[baseIndex + 2] = 0.0f;    // padding
                buffer[baseIndex + 3] = poses[entityIndex].pos[0]; // origin.x
                buffer[baseIndex + 4] = sinX;    // x.y
                buffer[baseIndex + 5] = cosX;    // y.y
                buffer[baseIndex + 6] = 0.0f;    // padding
                buffer[baseIndex + 7] = poses[entityIndex].pos[1]; // origin.y
                bufferIndex++;
            }

            try
            {
                RenderingServer.MultimeshSetBuffer(
                    multiMesh.GetRid(),
                    buffer
                );
            }
            catch (Exception e)
            {
                GD.PrintErr($"更新失败: {e.Message}");
            }
        }
    }

    public unsafe void DisplaySprites()
    {
        if (multiMeshInstances.Length == 0) return;

        List<Position> poses = new();
        GetAllEntityPosSystem getAllEntityPosSystem = new();
        getAllEntityPosSystem.poses = poses;
        myWorld.Query(queryBuilder, getAllEntityPosSystem);
        poses = getAllEntityPosSystem.poses;

        if (poses.Count == 0) return;
        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
        {
            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
            if (multiMesh == null) continue;
            multiMesh.SetInstanceTransform2D(0, new Transform2D(0.0f, Vector2.Zero)); //解决放大缩小摄像机时，缓冲区不一致导致渲染bug
            Span<float> bufferArray = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid()).AsSpan();
            fixed (float* bufferPtr = bufferArray)
            {
                int startEntity = meshIndex * ENTITIES_PER_MESH;
                int endEntity = startEntity + ENTITIES_PER_MESH;

                // 仅处理属于当前MultiMesh的实体
                for (int entityId = startEntity; entityId < endEntity; entityId++)
                {
                    if (entityId >= poses.Count) break;

                    var pos = poses[entityId];

                    // 计算在缓冲区中的位置
                    int instanceIndex = entityId - startEntity;
                    int baseIndex = instanceIndex * GODOT_FLOATS_PER_INSTANCE;
                    bufferPtr[baseIndex + 3] = pos.pos.X;
                    bufferPtr[baseIndex + 7] = pos.pos.Y;
                }
            }
            try
            {
                RenderingServer.MultimeshSetBuffer(
                    multiMesh.GetRid(),
                    bufferArray
                );

            }
            catch (Exception e)
            {
                GD.PrintErr($"更新失败: {e.Message}");
            }
        }
    }


    public override void _Process(double delta)
    {
        if (myWorld == null)
        {
            return;
        }

        //if (Engine.GetProcessFrames() % 2 == 0)
        //{
        //	DisplaySpritesOptimized2();
        //}
    }
    public override void _PhysicsProcess(double delta)
    {

        if (myWorld == null)
        {
            return;
        }
        if (!isPaused) TickLoop(delta);
        //UpdateTree();
    }
    public MoveSystem2 moveSystem = new();

    public void TickLoop(double delta)
    {
        //var start = DateTime.Now;
        MoveSystem2.dt = (float)delta;
        myWorld.Query(queryBuilder, moveSystem);
        DisplaySprites();
        //var end = DateTime.Now;
        //if (Engine.GetPhysicsFrames() % 15 == 0)
        //{
        //    GD.Print($"TickLoop:{(end - start).TotalMilliseconds}ms");
        //}
    }

    public bool isPaused = false;
    public void Pause()
    {
        isPaused = !isPaused;
    }


    public partial struct TreeUpdateSystem : ISystem<Position, QTComp>
    {
        public static QTree<QTComp> qTree;
        public void Execute(ref Entity entity, ref Position position, ref QTComp qTComp)
        {
            qTree.Insert(qTComp);
        }
    }
    public TreeUpdateSystem treeUpdateSystem = new();
    bool IsRuningTreeBuild = false;
    public async void UpdateTree()
    {
        if (myWorld == null)
        {
            return;
        }
        if (isPaused || IsRuningTreeBuild) return;

        await Task.Run(() =>
        {
            IsRuningTreeBuild = true;
            qTree.Clear();
            TreeUpdateSystem.qTree = qTree;
            myWorld.Query(queryBuilder2, treeUpdateSystem);
            qTree = TreeUpdateSystem.qTree;
            IsRuningTreeBuild = false ;
        });
        DrawTree();
    }


    private void DrawTree()
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        List<QTree<QTComp>> qtList = new();
        qtList.Clear();
        qTree.GetAllChildNodes(ref qtList);
        for (int i = 0; i < qtList.Count; ++i)
        {
            float halfX = qtList[i].Width / 2f;
            float halfY = qtList[i].Height / 2f;
            Vector2[] point = new Vector2[4];
            point[0].X = point[1].X = point[2].X = point[3].X = qtList[i].X;
            point[0].Y = point[1].Y = point[2].Y = point[3].Y = qtList[i].Y;

            point[0].X -= halfX; point[0].Y -= halfY;
            point[1].X -= halfX; point[1].Y += halfY;
            point[2].X += halfX; point[2].Y += halfY;
            point[3].X += halfX; point[3].Y -= halfY;

            for (int j = 0; j < point.Length; j++)
            {
                DrawLine(point[j], point[(j + 1) % point.Length], new Color("#000000"), 1f);
            }
        }
    }
}





/// <summary>
/// 来源：https://www.bilibili.com/video/BV1XpHqeuEAS
/// 功能说明：四叉树节点
/// </summary>
public class QTree<T> : IRect where T : IRect
{
    public static bool InitConfig = false;
    public static int MAX_DEPTH = 5;
    public static int MAX_Threshold = 4;

    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public QTree<T> Parent { get; protected set; }
    public int Depth = 0;
    public int ChildCount { get; protected set; }
    protected bool IsLeaf = true;
    protected List<T> childList = new();
    protected QTree<T>[] childNodes;

    #region Pool
    protected static QTree<T> qtCreate()
    {
        var qt = new QTree<T>();
        qt.childNodes = new QTree<T>[4];
        return qt;
    }
    #endregion

    public QTree()
    {
        childNodes = new QTree<T>[4];
    }

    public QTree<T> CreateRoot(int maxThreshold = 4, int maxDepth = 5)
    {
        if (!InitConfig)
        {
            IsLeaf = false;
            InitConfig = true;
            MAX_DEPTH = maxDepth;
            MAX_Threshold = maxThreshold;
        }
        return this;
    }


    #region 初始化
    /// <summary>
    /// 初始化构造
    /// </summary>
    public void Init()
    {
        ChildCount = 0;     // 重置孩子数量
        IsLeaf = true;      // 设置为叶子
                            //childList = listPool.Get();     // 设置列表
    }

    /// <summary>
    /// 初始化矩形
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    public QTree<T> InitRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        return this;
    }

    /// <summary>
    /// 设置父节点
    /// </summary>
    /// <param name="parent"></param>
    /// <returns></returns>
    protected QTree<T> SetParent(QTree<T> parent)
    {
        Parent = parent;
        return this;
    }
    /// <summary>
    /// 设置深度
    /// </summary>
    /// <param name="depth"></param>
    /// <returns></returns>
    protected QTree<T> SetDepth(int depth)
    {
        Depth = depth;
        return this;
    }
    #endregion

    /// <summary>
    /// 分割空间
    /// </summary>
    public void Split()
    {
        IsLeaf = false;     // 变为非子叶
        float hWidth = Width / 2;
        float hHeight = Height / 2;
        float hhWidth = hWidth / 2;
        float hhHeight = hHeight / 2;

        int newDepth = Depth + 1;
        float xMin = X - hhWidth;
        float xMax = X + hhWidth;
        float yMin = Y - hhHeight;
        float yMax = Y + hhHeight;
        #region 1
        //childNodes[0] = qtPool.Get()
        //    .SetDepth(newDepth)
        //    .InitRect(xMin, yMax, hWidth, hHeight)
        //    .SetParent(this);
        //childNodes[1] = qtPool.Get()
        //    .SetDepth(newDepth)
        //    .InitRect(xMax, yMax, hWidth, hHeight)
        //    .SetParent(this);
        //childNodes[2] = qtPool.Get()
        //    .SetDepth(newDepth)
        //    .InitRect(xMin, yMin, hWidth, hHeight)
        //    .SetParent(this);
        //childNodes[3] = qtPool.Get()
        //    .SetDepth(newDepth)
        //    .InitRect(xMax, yMin, hWidth, hHeight)
        //    .SetParent(this);
        #endregion 1
        childNodes[0] = qtCreate()
            .SetDepth(newDepth)
            .InitRect(xMin, yMax, hWidth, hHeight).SetParent(this);
        childNodes[1] = qtCreate()
            .SetDepth(newDepth)
            .InitRect(xMax, yMax, hWidth, hHeight).SetParent(this);
        childNodes[2] = qtCreate()
            .SetDepth(newDepth)
            .InitRect(xMin, yMin, hWidth, hHeight).SetParent(this);
        childNodes[3] = qtCreate()
            .SetDepth(newDepth)
            .InitRect(xMax, yMin, hWidth, hHeight).SetParent(this);
        // 将孩子放入子节点
        for (int i = childList.Count - 1; i >= 0; --i)
        {
            --ChildCount;
            Insert(childList[i]);
            childList.RemoveAt(i);
        }

        //listPool.Release(childList);      // 回收列表
        //childList = null;
    }


    /// <summary>
    /// 获取目标所在的象限
    /// </summary>
    /// <param name="node"></param>
    /// <returns>在范围内>0,在范围外==0</returns>
    public int GetTargetIndex(T node)
    {
        //获取节点左下角，右上角
        float halfWidth = Width / 2;
        float halfHeight = Height / 2;
        float min_x = node.X - node.Width / 2;
        float min_y = node.Y - node.Height / 2;
        float max_x = node.X + node.Width / 2;
        float max_y = node.Y + node.Height / 2;
        //判断范围
        if (min_x > X + halfWidth || max_x < X - halfWidth || min_y > Y + halfHeight || max_y < Y - halfHeight) return 0;

        int idx = 0;
        bool IsLeft = min_x <= X ? true : false;
        bool IsRight = max_x >= X ? true : false;
        bool IsBottom = min_y <= Y ? true : false;
        bool IsTop = max_y >= Y ? true : false;

        if (IsLeft)
        {
            if (IsTop) idx |= AreaType.LT;
            if (IsBottom) idx |= AreaType.LB;
        }
        if (IsRight)
        {
            if (IsTop) idx |= AreaType.RT;
            if (IsBottom) idx |= AreaType.RB;
        }
        return idx;
    }

    /// <summary>
    /// 插入节点
    /// </summary>
    /// <param name="node"></param>
    public void Insert(T node)
    {
        if (IsLeaf)
        {
            // 大于区域上限 && 当前深度未满足上限 =》 分割+重新插入
            if (ChildCount + 1 > MAX_Threshold && Depth < MAX_DEPTH)
            {
                Split();
                Insert(node);
            }
            else
            {
                childList.Add(node);
                ++ChildCount;
            }
        }
        else
        {
            // 非叶子节点则获取所属区域并插入子区域
            int idx = GetTargetIndex(node);

            if (idx != 0) ++ChildCount;
            if ((idx & AreaType.LT) != 0) childNodes[0].Insert(node);
            if ((idx & AreaType.RT) != 0) childNodes[1].Insert(node);
            if ((idx & AreaType.LB) != 0) childNodes[2].Insert(node);
            if ((idx & AreaType.RB) != 0) childNodes[3].Insert(node);
        }
    }

    /// <summary>
    /// 移除节点
    /// </summary>
    /// <param name="node">移除的对象</param>
    /// <returns>True该区域有移除</returns>
    //public bool Remove(T node)
    //{
    //    bool off = false;
    //    if (IsLeaf)
    //    {
    //        int idx = childList.IndexOf(node);
    //        if (idx >= 0)
    //        {
    //            childList.Swap(idx, childList.Count - 1);
    //            childList.RemoveAt(childList.Count - 1);
    //            --ChildCount;   // 区域内孩子数量-1
    //            off = true;
    //        }
    //        else
    //        {
    //            off = false;
    //        }
    //    }
    //    else
    //    {
    //        // 非叶子节点则获取所属区域并插入子区域
    //        int idx = GetTargetIndex(node);
    //        if ((idx & AreaType.LT) != 0)
    //        {
    //            off |= childNodes[0].Remove(node);
    //        }
    //        if ((idx & AreaType.RT) != 0)
    //        {
    //            off |= childNodes[1].Remove(node);
    //        }
    //        if ((idx & AreaType.LB) != 0)
    //        {
    //            off |= childNodes[2].Remove(node);
    //        }
    //        if ((idx & AreaType.RB) != 0)
    //        {
    //            off |= childNodes[3].Remove(node);
    //        }
    //        if (off)
    //        {
    //            --ChildCount;   // 区域中有移除则自身区域孩子-1
    //        }
    //    }
    //    return off;
    //}

    /// <summary>
    /// 获取该节点下的所有孩子（包括自己）
    /// </summary>
    /// <param name="qtList"></param>
    public void GetAllChildNodes(ref List<QTree<T>> qtList)
    {
        qtList.Add(this);
        if (!IsLeaf)
        {
            for (int i = 0; i < childNodes.Length; ++i)
            {
                if (childNodes[i] != null) childNodes[i].GetAllChildNodes(ref qtList);
            }
        }
    }

    /// <summary>
    /// 获取该节点下的所有孩子（包括自己）深度排序
    /// </summary>
    /// <param name="qtList"></param>
    public void GetAllChildNodesByDepth(ref List<QTree<T>> qtList)
    {
        // 比较方法
        int cmp(QTree<T> x, QTree<T> y)
        {
            if (x.Depth > y.Depth) return 1;
            else if (x.Depth == y.Depth) return 0;
            return -1;
        }

        GetAllChildNodes(ref qtList);
        qtList.Sort(cmp);   // 深度排序
    }

    /// <summary>
    /// 获取目标周围对象
    /// </summary>
    /// <param name="target">目标</param>
    /// <param name="qtList">所有对象</param>
    public void GetAroundObj(ref T target, ref List<T> qtList)
    {
        if (IsLeaf)
        {
            for (int i = 0; i < childList.Count; ++i)
            {
                if (!target.Equals(childList[i])) qtList.Add(childList[i]);
            }
        }
        else
        {
            // 非叶子节点则获取所属区域并插入子区域
            int idx = GetTargetIndex(target);
            if ((idx & AreaType.LT) != 0) childNodes[0].GetAroundObj(ref target, ref qtList);
            if ((idx & AreaType.RT) != 0) childNodes[1].GetAroundObj(ref target, ref qtList);
            if ((idx & AreaType.LB) != 0) childNodes[2].GetAroundObj(ref target, ref qtList);
            if ((idx & AreaType.RB) != 0) childNodes[3].GetAroundObj(ref target, ref qtList);
        }
    }

    /// <summary>
    /// 清除节点（顺带子节点）
    /// </summary>
    public void Clear()
    {
        // 叶子
        if (IsLeaf)
        {
            ChildCount = 0;
            if (childList != null) childList.Clear();
            // 非根节点回收列表
            if (Parent != null)
            {
                //listPool.Release(childList);
                //childList = null;
            }
        }
        // 非叶子
        else
        {
            IsLeaf = true;
            for (int i = 0; i < childNodes.Length; ++i)
            {
                if (childNodes[i] != null) childNodes[i].Clear();              // 子节点清除
                                                                               //qtPool.Release(childNodes[i]);  // 回收子节点
                                                                               //childNodes[i] = null;
            }

            // 根节点重置列表
            if (Parent == null)
            {
                Init();
            }

        }
    }
}
