using System;
using MFAAvalonia.ViewModels.Windows;
using SukiUI.Controls;

namespace MFAAvalonia.Views.Windows;

public partial class LicenseWindow : SukiWindow
{
    public LicenseViewModel? ViewModel => LicenseContent.ViewModel;
    public event EventHandler? LicenseActivated;

    public LicenseWindow()
    {
        InitializeComponent();
        if (ViewModel is { } vm) vm.Activated += OnActivated;
        Closed += (_, _) =>
        {
            if (ViewModel is { } model) model.Activated -= OnActivated;
        };
    }

    private void OnActivated(object? sender, EventArgs e) => LicenseActivated?.Invoke(this, e);
}
