# EntJoy

Entity Just For Fun!

一个C#编写的轻量级ECS框架(基于Archetype)，没有额外的性能优化，一切以简单为主

它可以用于：了解ECS的一般构建方式（开发此ECS的最初目的）

## 安装
通过Git Clone/直接下载zip，并复制到你的项目中即可使用

## 使用方式

```csharp

public struct Position : ICom {
    public Vector3 value;
}

public struct Name : ICom {
    public string value;
}

public class Project {
    private World myWorld;

    void Main() {
        // 创建一个世界:你可以在构造函数中传入世界的标识符, 也可以不传入(默认为default)
        myWorld = new World("GameWorld");

        // 创建一个实体
        Entity entity = myWorld.NewEntity(typeof(Position));
        
        // 添加一个组件
        myWorld.AddComponent<Name>(entity);

        // 覆盖组件
        myWorld.Set<Name>(entity, new Name() { value = "Jack" });

        // 移除一个组件
        myWorld.RemoveComponent<Name>(entity);

        // 构建一个查询,我们通过它读取、修改组件字段值(通常,在ECS中查询是在System中运行的,你可以自定义这部分逻辑的位置)
        QueryBuilder builder = new QueryBuilder().WithAll<Position>();
        myWorld.Query(builder, (Entity ent, ref Position pos) => {
            // 写入:例如我们让位置不停变化
            pos.value += Vector3.one;
            // 读取:在控制台打印当前位置
            Debug.Log(pos.value);   
        })
    }
}

```

## 感谢
在开发过程中，以下优秀的开源框架给了EntJoy很大的灵感和帮助，十分感谢！如果可以，请您也多多支持:

Arch: https://github.com/genaray/Arch

Friflo.Engine.ECS: https://github.com/friflo/Friflo.Engine.ECS

## 最后
如果项目对您有帮助，请给个Star吧！
