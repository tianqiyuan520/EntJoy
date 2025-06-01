# EntJoy

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/tianqiyuan520/EntJoy)![Godot Engine](https://img.shields.io/badge/GODOT-000000.svg?style=for-the-badge&logo=godot-engine)

![Visual Studio](https://img.shields.io/badge/Visual%20Studio-5C2D91.svg?style=for-the-badge&logo=visual-studio&logoColor=white)![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)

Entity Just For Fun

一个C#编写的轻量级ECS框架(基于Archetype)

## 原项目

coinsoundsbetter: <https://github.com/coinsoundsbetter/EntJoy>

## 安装

通过Git Clone/直接下载zip，通过godot脚本运行即可

详情可见: [精灵随机移动实例](Example/SpritesRandomMove/SpritesRandomMove.cs)

## 使用方式

```csharp
//位移组件
public struct Position : IComponent
{
    public Vector2 pos;
    //public Vector2 vel;
    public override string ToString()
    {
        return $"({pos[0]:F2}, {pos[1]:F2})";
    }
}
//速度组件
public struct Vel : IComponent
{
    public Vector2 vel;
    public override string ToString()
    {
        return $"({vel[0]:F2}, {vel[1]:F2})";
    }
}

public partial class SpritesRandomMove : Node2D
{
    public World myWorld;
    public override void _Ready()
    {
        //新建世界
        myWorld = new World();
        World_Recorder.RecordWorld(myWorld);

        //新建实体
        var entity = myWorld.NewEntity(typeof(Position), typeof(Vel));
        //添加组件
        myWorld.AddComponent(entity, new Position()
        {pos = new Vector2(0, 0)});
        myWorld.AddComponent(entity, new Vel()
        {
            vel = new Vector2(10,0)
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        if (myWorld == null)
        {
            return;
        }

        //构造一个查询器
        QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position, Vel>();

        moveSystem.dt = delta;  
        myWorld.Query(queryBuilder, moveSystem);
    }
}

//运动系统
public struct MoveSystem : ISystem<Position, Vel>
{
    public double dt;
    public Vector2 viewportSize;

    public void Execute(ref Entity entity, ref Position pos, ref Vel vel)
    {
        pos.pos += vel.vel * (float)dt;
        if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) vel.vel.X *= -1;
        if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) vel.vel.Y *= -1;
    }
}

```

## 感谢

项目改自于：coinsoundsbetter 的 EntJoy

鸣谢 coinsoundsbetter

参考项目：

Arch: <https://github.com/genaray/Arch>

Friflo.Engine.ECS: <https://github.com/friflo/Friflo.Engine.ECS>
