; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
EJ2001 | EntJoy.ECS.SourceGenerator | Error | Type marked with [ECSComponent] must be a partial struct
EJ2002 | EntJoy.ECS.SourceGenerator | Error | Component contains managed reference field (not blittable)
EJ2003 | EntJoy.ECS.SourceGenerator | Error | [ECSComponent] cannot be applied to generic struct
EJ2011 | EntJoy.ECS.SourceGenerator | Error | [Reactive] handler must define a static Execute method
EJ2012 | EntJoy.ECS.SourceGenerator | Error | [Reactive] Execute must have the signature (in ReadOnlySpan<Entity>, in ReadOnlySpan<TComponent>)
