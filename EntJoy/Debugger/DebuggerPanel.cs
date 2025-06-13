//using EntJoy;
//using EntJoy.Debugger;
//using Godot;
//using System;
//using System.Collections.Generic;
//using System.Linq;

//public partial class DebuggerPanel : Panel
//{
//    public static DebuggerPanel Instance; // 单例

//    [Export]
//    public Tree TreeControl; // Tree 节点

//    [Export]
//    public Button RefreshButton; // 刷新按钮

//    private TreeItem _rootItem;

//    // 存储 TreeItem 与 Archetype 的映射关系
//    private Dictionary<TreeItem, Archetype> _archetypeMap = new Dictionary<TreeItem, Archetype>();
//    private Dictionary<TreeItem, StructArray> _componentArrayMap = new Dictionary<TreeItem, StructArray>();
//    private Dictionary<TreeItem, (Archetype, int, int)> _componentValueMap = new Dictionary<TreeItem, (Archetype, int, int)>();

//    public DebuggerPanel()
//    {
//        if (Instance == null) Instance = this;
//    }

//    public override void _Ready()
//    {
//        // 初始化 Tree
//        TreeControl.Columns = 3;
//        TreeControl.ColumnTitlesVisible = true;
//        TreeControl.SetColumnTitle(0, "对象");
//        TreeControl.SetColumnTitle(1, "地址");
//        TreeControl.SetColumnTitle(2, "详细信息");

//        // 创建根节点
//        _rootItem = TreeControl.CreateItem();
//        _rootItem.SetText(0, "ECS World");
//        //_rootItem.SetIcon(0, GetThemeIcon("Node", "EditorIcons"));

//        // 连接按钮信号
//        RefreshButton.Pressed += OnRefreshButtonPressed;

//        // 连接 选择TreeItem 信号
//        TreeControl.ItemSelected += OnTreeItemSelected;
//    }

//    // 刷新按钮点击事件
//    private void OnRefreshButtonPressed()
//    {
//        if (World_Recorder.GetFirstWorld(out World world))
//        {
//            UpdateTree(world);
//        }
//        else
//        {
//            GD.Print("当前未创建过ECS World");
//        }
//    }

//    // Tree 节点选择事件
//    private void OnTreeItemSelected()
//    {
//        TreeItem selectedItem = TreeControl.GetSelected();

//        // 如果选择的是组件值节点
//        if (_componentValueMap.TryGetValue(selectedItem, out var valueInfo))
//        {
//            var (archetype, componentIndex, entityIndex) = valueInfo;
//            var array = archetype.GetComponentStructArray()[componentIndex];
//            //var compType = archetype.Types[componentIndex].GetType();
//            var value = array.GetDebugValue(entityIndex);

//            GD.Print($"选择的组件为: {archetype.Types[componentIndex].Type.Name}");
//            GD.Print($"  Entity: {archetype.GetEntity(entityIndex).Id}, Value: {value}");
//            GD.Print($"  Address: {archetype.GetComponentArrayAddress(componentIndex).ToString("D")}");
//        }
//    }

//    // 更新 Tree 内容
//    public void UpdateTree(World world)
//    {
//        // 清除所有子节点
//        foreach (var item in _rootItem.GetChildren())
//        {
//            _rootItem.RemoveChild(item);
//            item.Free();
//        }
//        _archetypeMap.Clear();
//        _componentArrayMap.Clear();
//        _componentValueMap.Clear();

//        // 添加世界信息
//        TreeItem worldItem = TreeControl.CreateItem(_rootItem);
//        worldItem.SetText(0, "World");
//        worldItem.SetText(1, $"{MemoryAddress.GetAddress(world).ToString("D")}");
//        //worldItem.SetText(2, $"Entities: {world.entityCount}, Archetypes: {world.archetypeCount}");
//        //worldItem.SetIcon(0, GetThemeIcon("World", "EditorIcons"));

//        var archetypes = world.GetAllArchetypes();
//        // 获取所有 Archetype
//        for (int i = 0; i < archetypes.Count(); i++)
//        {
//            var archetype = archetypes[i];
//            if (archetype == null) continue;

//            // 添加 Archetype 节点
//            TreeItem archItem = TreeControl.CreateItem(worldItem);
//            _archetypeMap[archItem] = archetype;

//            // 设置 Archetype 信息
//            archItem.SetText(0, $"Archetype {i}");
//            archItem.SetText(1, $"{archetype.GetAddress().ToString("D")}");
//            archItem.SetText(2, $"Entities: {archetype.EntityCount}, Components: {archetype.ComponentCount}");
//            //archItem.SetIcon(0, GetThemeIcon("Grid", "EditorIcons"));

//            // 添加实体数组节点
//            TreeItem entityArrayItem = TreeControl.CreateItem(archItem);
//            entityArrayItem.SetText(0, "Entity 数组");

//            entityArrayItem.SetText(1, $"{archetype.GetEntityArrayAddress().ToString("D")}");
//            entityArrayItem.SetText(2, $"Count: {archetype.EntityCount}");
//            //entityArrayItem.SetIcon(0, GetThemeIcon("CollisionPolygon2D", "EditorIcons"));

//            // 添加组件数组节点
//            for (int compIndex = 0; compIndex < archetype.ComponentCount; compIndex++)
//            {
//                var array = archetype.GetComponentStructArray()[compIndex]; //获取当前组件数组
//                var compType = archetype.Types[compIndex].Type; //获取组件类型

//                // 添加组件数组节点
//                TreeItem compArrayItem = TreeControl.CreateItem(archItem);
//                _componentArrayMap[compArrayItem] = array;

//                compArrayItem.SetText(0, compType.Name);
//                compArrayItem.SetText(1, $"{archetype.GetComponentArrayAddress(compIndex).ToString("D")}");
//                compArrayItem.SetText(2, $"Count: {array.Length}, Memory Size: {array.GetMemorySize()} bytes");
//                //compArrayItem.SetIcon(0, GetThemeIcon("PackedScene", "EditorIcons"));

//                // 添加组件值节点 (只显示前5个)
//                int displayCount = Math.Min(5, array.Length);
//                for (int entityIndex = 0; entityIndex < displayCount; entityIndex++)
//                {
//                    TreeItem valueItem = TreeControl.CreateItem(compArrayItem);
//                    _componentValueMap[valueItem] = (archetype, compIndex, entityIndex);

//                    var value = array.GetDebugValue(entityIndex);
//                    valueItem.SetText(0, $"Entity {archetype.GetEntity(entityIndex).Id}");
//                    valueItem.SetText(1, $"{array.GetElementAddress(entityIndex).ToString("D")}");
//                    valueItem.SetText(2, value);
//                    //valueItem.SetIcon(0, GetThemeIcon("Mesh", "EditorIcons"));
//                }

//                if (array.Length > displayCount)
//                {
//                    TreeItem moreItem = TreeControl.CreateItem(compArrayItem);
//                    moreItem.SetText(0, $"And {array.Length - displayCount} more...");
//                    //moreItem.SetIcon(0, GetThemeIcon("ArrowRight", "EditorIcons"));
//                }
//            }
//        }

//        // 展开所有节点
//        _rootItem.Collapsed = false;
//        worldItem.Collapsed = false;
//    }
//}
