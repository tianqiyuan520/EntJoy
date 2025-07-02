using EntJoy;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

public partial class DebuggerPanel : Panel
{
	public static DebuggerPanel Instance; // 单例

	[Export]
	public Tree TreeControl; // Tree 节点

	[Export]
	public Button RefreshButton; // 刷新按钮

    [Export]
    public PackedScene memoryGraphicsPackedScene; // 内存图形

	public MemoryGraphics memoryGraphics;
    private TreeItem _rootItem;

	// 存储 TreeItem 与 Archetype,Chunk 的映射关系
	private Dictionary<TreeItem, (Archetype archetype,Chunk chunk,bool IsAdded)> ChunkTreeItemMap = new();

	public DebuggerPanel()
	{
        if (Instance == null) Instance = this;
	}

	public override void _Ready()
	{
        memoryGraphics = memoryGraphicsPackedScene.Instantiate<MemoryGraphics>();
        memoryGraphics.Visible = false;
        //InitMemoryGraphics();
        AddChild(memoryGraphics);
        // 初始化 Tree
        TreeControl.Columns = 3;
		TreeControl.ColumnTitlesVisible = true;
		TreeControl.SetColumnTitle(0, "对象");
		TreeControl.SetColumnTitle(1, "地址");
		TreeControl.SetColumnTitle(2, "详细信息");

		// 创建根节点
		_rootItem = TreeControl.CreateItem();
		_rootItem.SetText(0, "ECS World");
		//_rootItem.SetIcon(0, GetThemeIcon("Node", "EditorIcons"));

		// 连接按钮信号
		RefreshButton.Pressed += OnRefreshButtonPressed;

		// 连接 选择TreeItem 信号
		TreeControl.ItemSelected += OnTreeItemSelected;
	}

	// 刷新按钮点击事件
	private void OnRefreshButtonPressed()
	{
		if (World_Recorder.GetFirstWorld(out World world))
		{
			UpdateTree(world);
		}
		else
		{
			GD.Print("当前未创建过ECS World");
		}
	}

	// Tree 节点选择事件
	private void OnTreeItemSelected()
	{
		TreeItem selectedItem = TreeControl.GetSelected();

		if (ChunkTreeItemMap.TryGetValue(selectedItem, out var chunkInfo))
		{
			if (chunkInfo.IsAdded) return;
			if (!chunkInfo.IsAdded) ExpandChunkData(selectedItem, chunkInfo.archetype, chunkInfo.chunk);
			chunkInfo.IsAdded = true;
			ChunkTreeItemMap[selectedItem] = chunkInfo;
			//memoryGraphics.Visible = true;
        }
		else
		{
            //memoryGraphics.Visible = false;
        }
	}
	//展开当前的Chunk数据
	public async void ExpandChunkData(TreeItem chunkItem,Archetype archetype, Chunk chunk)
	{
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        //获取该 Chunk 的 Entity部分
		GD.Print("获取该 Chunk 的 Entity部分");
		TreeItem entityListText = TreeControl.CreateItem(chunkItem);
		entityListText.SetText(0, "Entity列表");
		entityListText.Collapsed = true;
		IntPtr entityPtr = chunk.MemoryBlock;
		for (int k = 0; k < chunk.EntityCount; k++)
		{
			unsafe
			{
                var entity = *(Entity*)entityPtr;
                TreeItem entityItem = TreeControl.CreateItem(entityListText);
                entityItem.SetText(0, $"Entity {entity.Id}");
                entityItem.SetText(1, $"{entityPtr}");
                entityPtr += chunk.GetEntitySize();
            }
			
		}
		//获取该 Chunk 的 Component部分
		TreeItem componentListText = TreeControl.CreateItem(chunkItem);
		componentListText.SetText(0, "Component列表");
		componentListText.Collapsed = true;
		for (int k = 0; k < chunk.ComponentCount; k++)
		{
			TreeItem compTypeText = TreeControl.CreateItem(componentListText);

			var compType = archetype.Types[k].Type;
			compTypeText.SetText(0, $"组件{k} " + compType.Name);
			//获取实体
			entityPtr = chunk.MemoryBlock;
			for (int l = 0; l < chunk.EntityCount; l++)
			{
				unsafe
				{
                    TreeItem compTypeText2 = TreeControl.CreateItem(compTypeText);
                    var compType2 = archetype.Types[k].Type;
                    var thisEntity = *(Entity*)entityPtr;
                    compTypeText2.SetText(0, $"Entity {thisEntity.Id} " + compType.Name);
                    //组件地址
                    compTypeText2.SetText(1, $"{chunk.GetComponentArrayPointer(k) + chunk.GetComponentSize(compType2) * l}");
                    //实体地址偏移
                    entityPtr += chunk.GetEntitySize();
                }
			}
			compTypeText.Collapsed = true;
		}
	}

	// 更新 Tree 内容
	public unsafe void UpdateTree(World world)
	{
		// 清除所有子节点
		foreach (var item in _rootItem.GetChildren())
		{
			_rootItem.RemoveChild(item);
			item.Free();
		}
        ChunkTreeItemMap.Clear();
        //_archetypeMap.Clear();
        //_componentArrayMap.Clear();
        //_componentValueMap.Clear();

        // 添加世界信息
        TreeItem worldItem = TreeControl.CreateItem(_rootItem);
		worldItem.SetText(0, "World");
		worldItem.SetText(2, $"实体总数: {world.EntityCount}, 原型总数: {world.ArchetypeCount}");

		var archetypes = world.GetAllArchetypes();
		// 获取所有 Archetype
		for (int i = 0; i < archetypes.Count(); i++)
		{
			var archetype = archetypes[i];
			if (archetype == null) continue;

			// 添加 Archetype 节点
			TreeItem archItem = TreeControl.CreateItem(worldItem);
			//_archetypeMap[archItem] = archetype;

			// 设置 Archetype 信息
			archItem.SetText(0, $"Archetype {i}");
			archItem.SetText(1, $"{archetype.GetAddress().ToString("D")}");
			archItem.SetText(2, $"实体总数: {archetype.EntityCount}\n组件总数: {archetype.ComponentCount}\nChunk总数: {archetype.ChunkCount}");
			//说明 组件类型
            TreeItem CompText = TreeControl.CreateItem(archItem);
            CompText.SetText(0, "组件类型");

			StringBuilder sb = new StringBuilder();
			StringBuilder sb2 = new StringBuilder();
            sb2.AppendLine($"Entity 大小: {Marshal.SizeOf<Entity>()}");

            for (int j = 0; j < archetype.ComponentCount; j++)
            {
				var compType = archetype.Types[i].Type;

                sb.Append(compType.Name + (j != archetype.ComponentCount ? ", " : ""));
				sb2.AppendLine($"{compType.Name} 大小: {Marshal.SizeOf(compType)}");
            }
			CompText.SetText(1, sb2.ToString());
            CompText.SetText(2, sb.ToString());

            //获取该 Archetype 的 Chunk
            TreeItem chunkListText = TreeControl.CreateItem(archItem);
			chunkListText.SetText(0, "Chunk列表");
            chunkListText.Collapsed = true;
            var chunkCount = archetype.ChunkCount;
			var chunkList = archetype.GetChunks();
			for (int j = 0; j < chunkCount; j++)
			{
				var chunk = chunkList[j];
				if (chunk == null) continue;

                TreeItem chunkItem = TreeControl.CreateItem(chunkListText);
                ChunkTreeItemMap.Add(chunkItem, (archetype,chunk,false)); //加入映射
                chunkItem.SetText(0, $"Chunk {j}");
                chunkItem.SetText(2, $"实体数: {chunk.EntityCount}, 组件数: {archetype.ComponentCount}");

                chunkItem.SetText(1, $"Chunk 内存地址:{chunk.MemoryBlock}  \n总共大小{chunk.TotalSize}");

                chunkItem.Collapsed = true;


			}
				// 添加 Chunk 节点
            // 添加实体数组节点
            //TreeItem entityArrayItem = TreeControl.CreateItem(archItem);
            //entityArrayItem.SetText(0, "Entity 数组");

            //entityArrayItem.SetText(1, $"{archetype.GetEntityArrayAddress().ToString("D")}");
            //entityArrayItem.SetText(2, $"Count: {archetype.EntityCount}");
            ////entityArrayItem.SetIcon(0, GetThemeIcon("CollisionPolygon2D", "EditorIcons"));

            //// 添加组件数组节点
            //for (int compIndex = 0; compIndex < archetype.ComponentCount; compIndex++)
            //{
            //	var array = archetype.GetComponentStructArray()[compIndex]; //获取当前组件数组
            //	var compType = archetype.Types[compIndex].Type; //获取组件类型

            //	// 添加组件数组节点
            //	TreeItem compArrayItem = TreeControl.CreateItem(archItem);
            //	_componentArrayMap[compArrayItem] = array;

            //	compArrayItem.SetText(0, compType.Name);
            //	compArrayItem.SetText(1, $"{archetype.GetComponentArrayAddress(compIndex).ToString("D")}");
            //	compArrayItem.SetText(2, $"Count: {array.Length}, Memory Size: {array.GetMemorySize()} bytes");
            //	//compArrayItem.SetIcon(0, GetThemeIcon("PackedScene", "EditorIcons"));

            //	// 添加组件值节点 (只显示前5个)
            //	int displayCount = Math.Min(5, array.Length);
            //	for (int entityIndex = 0; entityIndex < displayCount; entityIndex++)
            //	{
            //		TreeItem valueItem = TreeControl.CreateItem(compArrayItem);
            //		_componentValueMap[valueItem] = (archetype, compIndex, entityIndex);

            //		var value = array.GetDebugValue(entityIndex);
            //		valueItem.SetText(0, $"Entity {archetype.GetEntity(entityIndex).Id}");
            //		valueItem.SetText(1, $"{array.GetElementAddress(entityIndex).ToString("D")}");
            //		valueItem.SetText(2, value);
            //		//valueItem.SetIcon(0, GetThemeIcon("Mesh", "EditorIcons"));
            //	}

            //	if (array.Length > displayCount)
            //	{
            //		TreeItem moreItem = TreeControl.CreateItem(compArrayItem);
            //		moreItem.SetText(0, $"And {array.Length - displayCount} more...");
            //		//moreItem.SetIcon(0, GetThemeIcon("ArrowRight", "EditorIcons"));
            //	}
            //}
        }

		_rootItem.Collapsed = false;
		worldItem.Collapsed = false;
	}


	public void InitMemoryGraphics()
	{
        for (int i = 0; i < 1024; i++)
        {
            ReferenceRect referenceRect = new ReferenceRect();
            memoryGraphics.GetNode("GridContainer").AddChild(referenceRect);

            ColorRect colorRect = new ColorRect() { Color = new Color(10f,0f,0f) };
            colorRect.SetAnchorsPreset(LayoutPreset.FullRect);

            referenceRect.AddChild(colorRect);
			referenceRect.EditorOnly = false;
            referenceRect.BorderWidth = 1.5f;
			referenceRect.BorderColor = new Color("009d94");
            referenceRect.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            referenceRect.SizeFlagsVertical = SizeFlags.ExpandFill;

        }
        
    }


}
