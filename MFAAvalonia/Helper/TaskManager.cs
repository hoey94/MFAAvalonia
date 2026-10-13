using MFAAvalonia.Helper.Licensing;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public static class TaskManager
{
    private static string FormatTaskPrefix(string prompt, string name) => $"{prompt}任务 {name}";

    private static void RunAuthorized(Action? action)
    {
        // Check inside the executing delegate, not only when work is queued.
        LicenseRuntime.RequireAuthorized();
        action?.Invoke();
    }

    /// <summary>
    /// 执行任务, 并带有更好的日志显示
    /// </summary>
    public static void RunTask(
        Action action,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool noMessage = false)
    {
        if (!noMessage)
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始。");
        if (catchException)
        {
            try { RunAuthorized(action); }
            catch (Exception e)
            {
                LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{e.Message}", e);
            }
        }
        else RunAuthorized(action);
        if (!noMessage)
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成。");
    }

    public static void RunTask(
        Action? action,
        CancellationToken token,
        Action<Exception>? handleError = null,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool shouldLog = true)
    {
        LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始（异步）。");
        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                RunAuthorized(action);
                LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成（异步）。");
            }
            catch (Exception ex) when (catchException && !(ex is OperationCanceledException))
            {
                var baseEx = ex.GetBaseException();
                handleError?.Invoke(baseEx);
                if (shouldLog) LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{baseEx.Message}", baseEx);
            }
        }, token);
    }

    public async static Task RunTaskAsync(
        Action? action,
        Action? handleError = null,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool shouldLog = true)
    {
        LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始（异步）。");
        try
        {
            await Task.Run(() => RunAuthorized(action));
        }
        catch (Exception ex) when (catchException && ex is not OperationCanceledException)
        {
            handleError?.Invoke();
            if (shouldLog)
                LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{ex.GetBaseException().Message}", ex.GetBaseException());
        }
        finally
        {
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成（异步）。");
        }
    }

    public async static Task RunTaskAsync(
        Func<Task>? action,
        Action? handleError = null,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool shouldLog = true)
    {
        LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始（异步）。");
        try
        {
            LicenseRuntime.RequireAuthorized();
            if (action != null)
                await action();
        }
        catch (Exception ex) when (catchException && ex is not OperationCanceledException)
        {
            var baseEx = ex.GetBaseException();
            handleError?.Invoke();
            if (shouldLog)
                LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{baseEx.Message}", baseEx);
        }
        finally
        {
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成（异步）。");
        }
    }

    public async static Task RunTaskAsync(
        Action? action,
        CancellationToken token,
        Action<Exception>? handleError = null,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool shouldLog = true)
    {
        LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始（异步）。");
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                RunAuthorized(action);
            }, token);
        }
        catch (Exception ex) when (catchException && !(ex is OperationCanceledException))
        {
            var baseEx = ex.GetBaseException();
            handleError?.Invoke(baseEx);
            if (shouldLog) LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{baseEx.Message}", baseEx);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成（异步）。");
        }
    }

    public async static Task<TResult> RunTaskAsync<TResult>(
        Func<TResult> function,
        CancellationToken token,
        Action<Exception>? handleError = null,
        string name = nameof(Action),
        string prompt = ">>> ",
        bool catchException = true,
        bool shouldLog = true,
        bool noMessage = false)
    {
        if (!noMessage)
            LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}开始（异步）。");
        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                LicenseRuntime.RequireAuthorized();
                return function();
            }, token);

            if (result is Task nestedTask)
                await nestedTask;
            return result;
        }
        catch (Exception ex) when (catchException && !(ex is OperationCanceledException))
        {
            var baseEx = ex.GetBaseException();
            handleError?.Invoke(baseEx);
            if (shouldLog) LoggerHelper.Error($"{FormatTaskPrefix(prompt, name)}执行失败：{baseEx.Message}", baseEx);
            return default!;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            if (!noMessage)
                LoggerHelper.Info($"{FormatTaskPrefix(prompt, name)}完成（异步）。");
        }
    }
}
