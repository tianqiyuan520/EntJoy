# `bevy_121`

Bevy 0.16 finally brought an end to the days of Bevy users gazing longingly in `flecs`'s direction, dreaming of the time when Bevy too would be able to express complex relationships directly in the ECS (or in the case of some contributors, designing and building relations). That's right: we finally have non-fragmenting, one-to-many relations! This allows users to express such patterns as:

```rs
#[derive(Component)]
#[relationship(relationship_target = LikedBy)]
struct Likes(pub Entity);

/// This is the "relationship target" component.
/// It will be automatically inserted and updated to contain
/// all entities that currently "like" this entity.
#[derive(Component, Deref)]
#[relationship_target(relationship = Likes)]
struct LikedBy(Vec<Entity>);

let alice = world.spawn_empty().id();
let bob = world.spawn(Likes(alice)).id();
let cart = world.spawn(Likes(alice)).id();

let liked_by = world.entity(alice).get::<LikedBy>(); // [bob, cart]
```

This is a major step forward in terms of ergonomics. Unfortunately, a number of other useful features are still missing, notably one-to-one and many-to-many relations. Fortunately, with immutable components and hooks both merged, this is something that can be implemented somewhat satisfactorily in user-space.

`bevy_121` introduces one-to-one relationships into the ECS:

```rs
#[derive(AsymmetricOneToOne)]
#[target(Occupying)]
struct OccupiedBy(Entity);

#[derive(AsymmetricOneToOne)]
#[target(OccupiedBy)]
struct Occupying(Entity);

let player = world.spawn((Player, Occupying(cells[2][3]))).id();
world.entity(cells[2][3]).get::<OccupiedBy>(); // player
```

At the moment, only asymmetric 1:1 relationships can be modeled, meaning the relationship components come in pairs, like in the example above. However, support for symmetric 1:1 relationships is something I want to add very soon, where the relating component is the same for both sides:

```rs
#[derive(SymmetricOneToOne)]
struct MonogamouslyMarriedTo(Entity);

let james_potter = world.spawn(()).id();
let lily_potter = world.spawn(MonogamouslyMarriedTo(james_potter)).id();
world.entity(james_potter).get::<MonogamouslyMarriedTo>(); // lily_potter
```

Essentially the difference being modeled here is the difference between a directed graph and an undirected graph.

## Examples

### [`simple`](crates/bevy_121/examples/simple.rs)
Demonstrates a simple example where each contributor can only work on one project at a time, and each project can only be worked on by one contributor at a time.

## Bevy Version

| `bevy` | `bevy_121` |
|--------|------------|
| 0.16   | 0.1        |

## Contributions and Known Limitations

PRs welcome. My game needs asymmetric relations *now* which is why they came first, but symmetric relations are also very much on the to-do list. Additionally, the derive is currently quite restrictive in that it requires target structs to be unit structs with a single `Entity` field. In the future, I'd like to be able to do things like:

```rs
#[derive(AsymmetricOneToOne)]
#[target(Other)]
struct MyRelation {
    #[target]
    target: Entity,
    associated_data: MyData,
}
```

Additionally, since it derives `Component` for you, it's missing out on several nice-to-haves that the `Component` derive gives you. Bevy avoids this by implementing the relationship derive in the `Component` derive code, which we can't have since we're 3rd-party. On the other hand, not all options on the `Component` derive make sense in this context: letting the user specify mutability makes no sense, for example. We *might* be able to allow custom hooks, although currently Bevy heavily limits what you can do with hooks for relationship components, so it's not high on the priority list. Allowing the user to customize storage type and required components would be nice, though. I'm fairly inexperienced with proc-macros, so I'll figure this all out eventually, but if someone beats me to it, all the better!

There also may be bugs in the implementation. There are no tests yet (something I'll change soon), and while I did some testing prior to release, it was by no means exhaustive. Please report any bugs you encounter, and I'm happy to merge bug fixes as well.

