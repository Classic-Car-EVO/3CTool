using CommunityToolkit.Mvvm.ComponentModel;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>The global library: machines, tools, materials and learned corrections.</summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private readonly bool ready;

    public LibraryViewModel(IShell shell)
        : base(shell, "Library")
    {
        Machines = new MachinesViewModel(shell);
        Tools = new ToolsViewModel(shell);
        Materials = new MaterialsViewModel(shell);
        StockSizes = new StockSizesViewModel(shell);
        Learning = new LearningViewModel(shell);
        Sections = [Machines, Tools, Materials, StockSizes, Learning];
        SelectedSection = Machines;
        ready = true;
    }

    public MachinesViewModel Machines { get; }

    public ToolsViewModel Tools { get; }

    public MaterialsViewModel Materials { get; }

    public StockSizesViewModel StockSizes { get; }

    public LearningViewModel Learning { get; }

    public IReadOnlyList<PageViewModel> Sections { get; }

    [ObservableProperty]
    public partial PageViewModel SelectedSection { get; set; }

    public override Task ShowAsync() => SelectedSection.ShowAsync();

    partial void OnSelectedSectionChanged(PageViewModel value)
    {
        if (ready)
        {
            _ = value.ShowAsync();
        }
    }
}
