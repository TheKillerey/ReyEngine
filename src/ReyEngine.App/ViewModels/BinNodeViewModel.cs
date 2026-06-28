using System.Collections.ObjectModel;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

public sealed class BinNodeViewModel : ViewModelBase
{
    public BinNode Model { get; }
    public ObservableCollection<BinNodeViewModel> Children { get; } = new();

    public BinNodeViewModel(BinNode model)
    {
        Model = model;
        foreach (var child in model.Children)
            Children.Add(new BinNodeViewModel(child));
    }

    public string Name => Model.Name;
    public string Value => Model.Value;
    public bool IsBranch => Model.IsBranch;
}
