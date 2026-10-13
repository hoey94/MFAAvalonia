using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Licensing;
using MFAAvalonia.Views.UserControls;
using MFAAvalonia.Views.Windows;

namespace MFAAvalonia;

public partial class App
{
    private bool _authorizedApplicationStarted;
    private bool _licenseShutdownInProgress;

    private void InitializeLicenseGate()
    {
        LicenseRuntime.AuthorizationLost += OnAuthorizationLost;
        if (LicenseRuntime.Service.Refresh().IsAuthorized)
        {
            InitializeAuthorizedApplication();
            return;
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var gate = new LicenseWindow();
            gate.Activated += (_, _) => ContinueAfterActivation(gate);
            desktop.MainWindow = gate;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var gate = new LicensePanel();
            if (gate.ViewModel is { } vm)
                vm.Activated += (_, _) => ContinueAfterActivation(null);
            singleView.MainView = gate;
        }
        else
        {
            throw new InvalidOperationException("No supported application lifetime for license activation.");
        }
    }

    private void ContinueAfterActivation(Window? gate)
    {
        if (_authorizedApplicationStarted || LicenseRuntime.IsSessionRevoked) return;
        try
        {
            // Validate persisted state again; a UI success event alone is not an authority.
            LicenseRuntime.RequireAuthorized();
            InitializeAuthorizedApplication();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var shutdownMode = desktop.ShutdownMode;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    desktop.MainWindow?.Show();
                    gate?.Close();
                }
                finally { desktop.ShutdownMode = shutdownMode; }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("Authorized application startup failed.", ex);
            ShowStartupErrorAndExit(ex, "授权后启动");
        }
    }

    private void OnAuthorizationLost(LicenseSnapshot snapshot)
    {
        // LicenseRuntime has already latched the session closed on a background thread.
        Dispatcher.UIThread.Post(async () => await StopExpiredSessionAsync());
    }

    private async Task StopExpiredSessionAsync()
    {
        if (_licenseShutdownInProgress) return;
        _licenseShutdownInProgress = true;
        var processors = _authorizedApplicationStarted ? MaaProcessor.Processors.ToArray() : Array.Empty<MaaProcessor>();
        // Begin native cleanup before touching presentation code. A notice/tray failure must
        // not skip stopping existing taskers, screenshots or agent processes.
        var stopping = Task.WhenAll(processors.Select(processor => Task.Run(() =>
        {
            try { processor.SetTasker(null, requireAllStopped: true); }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"License shutdown could not stop an instance: {ex.GetType().Name}");
            }
        })));
        try
        {
            try { GlobalHotkeyService.Shutdown(); }
            catch (Exception ex) { LoggerHelper.Warning($"License hotkey shutdown: {ex.GetType().Name}"); }
            try { TrayIconManager.DisposeTrayIcon(this); }
            catch (Exception ex) { LoggerHelper.Warning($"License tray shutdown: {ex.GetType().Name}"); }
            try
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    foreach (var window in desktop.Windows.ToArray()) window.IsEnabled = false;
                    var notice = new LicenseWindow();
                    desktop.MainWindow = notice;
                    notice.Show();
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
                {
                    singleView.MainView = new LicensePanel();
                }
                if (_authorizedApplicationStarted) Instances.PersistRuntimeState();
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"License lock-screen update failed: {ex.GetType().Name}");
            }
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var processor in processors) processor.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"License shutdown encountered: {ex.GetType().Name}");
        }
        finally
        {
            LicenseRuntime.StopMonitoring();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(3);
            else
                Environment.Exit(3);
        }
    }
}
