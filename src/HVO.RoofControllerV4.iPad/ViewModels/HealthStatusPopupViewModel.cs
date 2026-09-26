using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using HVO.RoofControllerV4.iPad.Popups;
using Microsoft.Maui.Controls;

namespace HVO.RoofControllerV4.iPad.ViewModels;

/// <summary>
/// View model used by the health status popup when displayed via <see cref="CommunityToolkit.Maui.Core.IPopupService"/>.
/// Wraps the primary <see cref="RoofControllerViewModel"/> so bindings can access dashboard state through the <see cref="Dashboard"/> property.
/// </summary>
public sealed partial class HealthStatusPopupViewModel : ObservableObject, IQueryAttributable
{
    internal const string DashboardQueryParameter = nameof(Dashboard);

    [ObservableProperty]
    private RoofControllerViewModel? dashboard;

    internal HealthStatusPopup? Popup { get; set; }

    /// <inheritdoc />
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (query.TryGetValue(DashboardQueryParameter, out var value) && value is RoofControllerViewModel viewModel)
        {
            Dashboard = viewModel;
        }
    }
}
