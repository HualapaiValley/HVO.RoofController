using HVO.RoofControllerV4.iPad.ViewModels;

namespace HVO.RoofControllerV4.iPad;

public partial class ConfigurationPage : ContentPage
{
    private readonly RoofControllerViewModel _viewModel;

    public ConfigurationPage(RoofControllerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
        await _viewModel.LoadConfigurationEditorAsync();
    }
}
