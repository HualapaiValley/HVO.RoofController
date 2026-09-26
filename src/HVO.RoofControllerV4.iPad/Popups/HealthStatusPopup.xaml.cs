using System;
using CommunityToolkit.Maui.Views;
using HVO.RoofControllerV4.iPad.ViewModels;

namespace HVO.RoofControllerV4.iPad.Popups;

public partial class HealthStatusPopup : Popup
{
    public HealthStatusPopup(HealthStatusPopupViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.Popup = this;
    }
}
