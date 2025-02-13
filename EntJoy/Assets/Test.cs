using System;
using EntJoy;
using UnityEngine;

public struct Position : ICom
{
    public Vector3 pos;
}

public class Test : MonoBehaviour
{
    private World myWorld;
    
    private void OnGUI()
    {
        if (GUILayout.Button("Create World"))
        {
            myWorld = new World();
        }

        if (GUILayout.Button("Create Entity"))
        {
            var entity = myWorld.NewEntity(typeof(Position));
            myWorld.AddComponent(entity, new Position()
            {
                pos = Vector3.one,
            });
        }
    }

    private void Update()
    {
        if (myWorld == null)
        {
            return;
        }

        var queryBuilder = new QueryBuilder().WithAll<Position>();
        myWorld.Query(queryBuilder, (Entity ent, ref Position pos) =>
        {
            pos.pos += Vector3.one;
            Debug.Log(pos.pos);
        });
    }
}