use proc_macro::TokenStream;
use quote::quote;
use syn::{Data, DeriveInput, Fields, parse_macro_input};

/// Derives the `AsymmetricOneToOne` and `Component` traits for a struct. Struct must be a tuple
/// struct with a single `Entity` field. This restriction will be lifted in the future, but for now
/// I want to get this out there for people to play with!
#[proc_macro_derive(AsymmetricOneToOne, attributes(target))]
pub fn derive_asymmetric_one_to_one(input: TokenStream) -> TokenStream {
    let ast = parse_macro_input!(input as DeriveInput);
    let bevy_ecs_path = bevy_macro_utils::BevyManifest::shared().get_path("bevy_ecs");
    let type_name = &ast.ident;
    let (impl_generics, type_generics, where_clause) = &ast.generics.split_for_impl();
    let mut target = None;
    for attr in &ast.attrs {
        if !attr.path().is_ident("target") {
            continue;
        }
        target = match attr.parse_args::<syn::Path>() {
            Ok(x) => Some(x),
            Err(e) => {
                return e.to_compile_error().into();
            }
        };
    }
    let Some(target) = target else {
        return syn::Error::new_spanned(
            type_name,
            "Relation target must be specified with #[target(RelationTarget)]",
        )
        .to_compile_error()
        .into();
    };
    let Data::Struct(struct_data) = ast.data else {
        return syn::Error::new_spanned(
            type_name,
            "Target type must be a tuple struct with a single field",
        )
        .to_compile_error()
        .into();
    };
    let Fields::Unnamed(fields) = struct_data.fields else {
        return syn::Error::new_spanned(
            type_name,
            "Target type must be a tuple struct with a single field",
        )
        .to_compile_error()
        .into();
    };
    if fields.unnamed.len() != 1 {
        return syn::Error::new_spanned(
            type_name,
            "Target type must be a tuple struct with a single field",
        )
        .to_compile_error()
        .into();
    }
    TokenStream::from(quote! {
        impl #impl_generics #bevy_ecs_path::component::Component for #type_name #type_generics #where_clause {
            const STORAGE_TYPE: #bevy_ecs_path::component::StorageType = #bevy_ecs_path::component::StorageType::Table;
            type Mutability = #bevy_ecs_path::component::Immutable;

            fn on_insert() -> Option<#bevy_ecs_path::component::ComponentHook> {
                Some(|mut world, ctx| {
                    let target = world.entity(ctx.entity).get::<Self>().expect("How did we get here?").0;
                    if let Ok(target_entity) = world.get_entity(target) {
                        if target_entity.get::<#target>().map(|x| x.0) == Some(ctx.entity) {
                            return;
                        }
                    }
                    if let Ok(mut entity_commands) = world.commands().get_entity(target) {
                        entity_commands.insert(#target(ctx.entity));
                    } else {
                        world.commands().entity(ctx.entity).remove::<Self>();
                    }
                })
            }

            fn on_replace() -> Option<#bevy_ecs_path::component::ComponentHook> {
                Some(|mut world, ctx| {
                    let target = world.entity(ctx.entity).get::<Self>().expect("How did we get here?").0;
                    if let Ok(target_entity) = world.get_entity(target) {
                        if target_entity.get::<#target>().map(|x| x.0) != Some(ctx.entity) {
                            return;
                        }
                    }
                    if let Ok(mut entity_commands) = world.commands().get_entity(target) {
                        entity_commands.remove::<#target>();
                    }
                })
            }
        }

        impl ::bevy_121::AsymmetricOneToOne for #type_name {
            type Target = #target;
        }
    })
}

