using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Licensing;

namespace MFAAvalonia.ViewModels.Windows;

public partial class LicenseViewModel : ViewModelBase
{
    private readonly ILicenseService _service;
    private bool _refreshing;
    public event EventHandler? Activated;
    public string MachineCode => _service.MachineCode;
    public string TrialCode => _service.TrialCode;

    [ObservableProperty] private string _activationKey = string.Empty;
    [ObservableProperty] private string _statusTitle = "未授权";
    [ObservableProperty] private string _statusDescription = "软件已锁定，请验证激活码后继续。";
    [ObservableProperty] private string _expiresAt = "尚未激活";
    [ObservableProperty] private string _remaining = "-- : -- : --";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isAuthorized;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    [NotifyCanExecuteChangedFor(nameof(FillTrialCommand))]
    private bool _isBusy;

    public LicenseViewModel(ILicenseService service)
    {
        _service = service;
        ApplySnapshot(service.Snapshot);
    }

    private bool CanActivate() => !IsBusy && !LicenseRuntime.IsSessionRevoked;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private void FillTrial() => ActivationKey = TrialCode;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        if (string.IsNullOrWhiteSpace(ActivationKey))
        {
            Message = "请输入作者签发的激活码，或填入本机试用码。";
            return;
        }
        IsBusy = true;
        Message = string.Empty;
        var key = ActivationKey;
        try
        {
            var result = await Task.Run(() => _service.Activate(key));
            ApplySnapshot(result.Snapshot);
            if (!result.Success)
            {
                Message = DescribeError(result.Error);
                return;
            }
            ActivationKey = string.Empty;
            Message = "验证成功，授权已保存。";
            Activated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"License activation failed: {ex.GetType().Name}");
            Message = "无法完成授权验证，请检查系统时间、数据目录权限后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (_refreshing || IsBusy) return;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(_service.Refresh);
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"License status refresh failed: {ex.GetType().Name}");
            ApplySnapshot(new LicenseSnapshot(LicenseStatus.StorageError, null, DateTimeOffset.UtcNow));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySnapshot(LicenseSnapshot snapshot)
    {
        IsAuthorized = snapshot.IsAuthorized && !LicenseRuntime.IsSessionRevoked;
        StatusTitle = snapshot.Status switch
        {
            LicenseStatus.Trial => "已激活 · 试用授权",
            LicenseStatus.Formal => "已激活 · 正式授权",
            LicenseStatus.Expired => "授权已到期",
            LicenseStatus.ClockRollback => "系统时间异常",
            LicenseStatus.StorageError => "授权数据不可用",
            LicenseStatus.Invalid => "授权验证失败",
            _ => "未授权 · 软件已锁定"
        };
        StatusDescription = IsAuthorized
            ? "授权与本机绑定。到期后将停止任务并锁定软件。"
            : DescribeError(snapshot.Status);
        ExpiresAt = snapshot.ExpiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture)
            ?? "尚未激活";
        var seconds = Math.Max(0, (long)Math.Ceiling(snapshot.Remaining.TotalSeconds));
        Remaining = IsAuthorized ? $"{seconds / 3600:00} : {seconds / 60 % 60:00} : {seconds % 60:00}" : "-- : -- : --";
        if (LicenseRuntime.IsSessionRevoked)
        {
            StatusTitle = "当前会话已锁定";
            StatusDescription = "软件正在停止任务，请重新启动后激活有效授权。";
        }
        ActivateCommand.NotifyCanExecuteChanged();
        FillTrialCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeError(LicenseStatus status) => status switch
    {
        LicenseStatus.Expired => "试用或正式授权已到期，请向作者获取新的正式激活码。",
        LicenseStatus.ClockRollback => "检测到系统时钟回退，请恢复正确的系统时间后重新验证。",
        LicenseStatus.StorageError => "授权文件缺失、损坏或无法读写。请恢复原始授权数据并检查目录权限；不会自动重置试用。",
        LicenseStatus.Invalid => "激活码无效、不属于本机，或不能将有效正式授权降级为试用。",
        _ => "填入正确的激活码后才能使用软件。打开此页面不会开始试用计时。"
    };
}
