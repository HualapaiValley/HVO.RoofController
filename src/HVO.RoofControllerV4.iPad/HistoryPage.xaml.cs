using HVO.RoofControllerV4.iPad.ViewModels;

namespace HVO.RoofControllerV4.iPad;

public partial class HistoryPage : ContentPage
{
    private readonly RoofControllerViewModel _viewModel;

    public HistoryPage(RoofControllerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}
