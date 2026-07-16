using System;
using Avalonia.Media.Imaging;

namespace MiniIde.ViewModels;

public class ImageTabViewModel : TabViewModelBase
{
    public Bitmap? Image { get; }
    public string? Error { get; }

    public ImageTabViewModel(string filePath) : base(FileId(filePath), filePath)
    {
        try { Image = new Bitmap(filePath); }
        catch (Exception ex) { Error = $"Failed to decode: {ex.Message}"; }
    }
}
