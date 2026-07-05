# Avalonia gotchas

- `TabControl.DataTemplates` (Control-inherited collection) selects the content template by the item's runtime type. Use it instead of `ContentTemplate` when items are heterogeneous (e.g. editor tab + image tab under one `TabControl`). Each `DataTemplate` needs an explicit `DataType`. `ItemTemplate` (tab header) can target the common base type because it only binds base members.
- `Avalonia.Data.Converters.ObjectConverters.IsNotNull` (and `IsNull`) are the idiomatic converters for gating `IsVisible` on a reference property being null/non-null. Wire with `Converter={x:Static ObjectConverters.IsNotNull}`. No custom `IValueConverter` needed.
- `Avalonia.Media.Imaging.Bitmap` ctor decodes synchronously and throws on failure. Wrap in `try/catch` when decoding user-supplied image paths; store the message for display.
