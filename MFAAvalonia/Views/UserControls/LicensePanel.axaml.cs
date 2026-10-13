using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MFAAvalonia.Helper.Licensing;
using MFAAvalonia.ViewModels.Windows;

namespace MFAAvalonia.Views.UserControls;

public partial class LicensePanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public LicenseViewModel? ViewModel => DataContext as LicenseViewModel;

    public LicensePanel()
    {
        InitializeComponent();
        if (Design.IsDesignMode) return;
        DataContext = new LicenseViewModel(LicenseRuntime.Service);
        _timer.Tick += RefreshStatus;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Design.IsDesignMode) return;
        _timer.Start();
        RefreshStatus(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private async void RefreshStatus(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm) await vm.RefreshAsync();
    }

    private async void CopyTrial(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ViewModel?.TrialCode);

    private async void CopyMachine(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ViewModel?.MachineCode);

    private async Task CopyAsync(string? value)
    {
        if (ViewModel is not { } vm || string.IsNullOrEmpty(value)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                vm.Message = "当前平台无法访问剪贴板，请选中文本手动复制。";
                return;
            }
            await clipboard.SetTextAsync(value);
            vm.Message = "已复制到剪贴板。";
        }
        catch (Exception)
        {
            vm.Message = "复制失败，请选中文本手动复制。";
        }
    }
}
