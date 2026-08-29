pub use bevy_121_macros::AsymmetricOneToOne;

/// Marker trait for components that are related to each other in an asymmetric one-to-one
/// relationship. **Note that you should not implement this trait directly; derive it instead.**
///
/// An example of such a relationship is a grid-based game where each cell has at most
/// one occupant, and each occupant is in exactly one cell:
///
/// ```
/// # use bevy_121::AsymmetricOneToOne;
///
/// #[derive(AsymmetricOneToOne)]
/// #[target(Occupant)]
/// struct Cell(Entity);
///
/// #[derive(AsymmetricOneToOne)]
/// #[target(Cell)]
/// struct Occupant(Entity);
/// ```
///
/// Note that in this example, while the `AsymmetricOneToOne` mechanism will enforce the 1:1 nature
/// of the relationship, it does not enforce that all occupants have a cell. It's up to the user to
/// be careful about removing the `Cell` component.
pub trait AsymmetricOneToOne {
    type Target: AsymmetricOneToOne<Target = Self>;
}

