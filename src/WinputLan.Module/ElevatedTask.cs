using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WinputLan.Module;

/// <summary>
/// The on-demand scheduled task the app registers (elevated, after one UAC prompt) when the user enables
/// "Permitir controlar apps de administrador". Running a task the user registered needs no elevation, so the
/// module can start the app elevated at host startup without a UAC prompt. Late-bound Task Scheduler 2.0 COM.
/// Call from a background thread.
/// </summary>
internal static class ElevatedTask
{
    /// <summary>Executable path of the registered task's action, or null when the task does not exist.</summary>
    public static string? QueryExecutable()
    {
        object? service = null, folder = null, task = null, definition = null, actions = null, action = null;
        try
        {
            service = Connect();
            folder = Call(service, "GetFolder", @"\");
            task = Call(folder, "GetTask", HostedContract.ElevatedTaskName);
            definition = Get(task, "Definition");
            actions = Get(definition, "Actions");
            if (Get(actions, "Count") is not int count || count < 1)
                return null;
            action = Get(actions, "Item", 1);
            return (Get(action, "Path") as string)?.Trim('"');
        }
        catch (Exception error) when (IsMissing(error))
        {
            return null;
        }
        finally
        {
            Release(action, actions, definition, task, folder, service);
        }
    }

    /// <summary>Runs the task, which fills $(Arg0) with <paramref name="parentId"/> and $(Arg1) with the show flag.</summary>
    public static void Run(int parentId, bool show)
    {
        object? service = null, folder = null, task = null, running = null;
        try
        {
            service = Connect();
            folder = Call(service, "GetFolder", @"\");
            task = Call(folder, "GetTask", HostedContract.ElevatedTaskName);
            // Must be string[] (VT_ARRAY|VT_BSTR): an object[] (VT_ARRAY|VT_VARIANT) fails with E_INVALIDARG.
            var parameters = new string[] { parentId.ToString(), show ? "--show" : "--hidden" };
            // RunEx(params, flags = TASK_RUN_NO_FLAGS, sessionID, user)
            running = Call(task, "RunEx", parameters, 0, 0, "");
        }
        finally
        {
            Release(running, task, folder, service);
        }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        Call(service, "Connect");
        return service;
    }

    private static object? Call(object? target, string name, params object[] args) =>
        (target ?? throw new COMException("Task Scheduler returned no object.")).GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object? target, string name, params object[] args) =>
        (target ?? throw new COMException("Task Scheduler returned no object.")).GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);

    /// <summary>Task not registered (0x80070002), not readable, or Task Scheduler unavailable: no elevated launch.</summary>
    private static bool IsMissing(Exception error)
    {
        var inner = error is TargetInvocationException { InnerException: { } e } ? e : error;
        return inner is COMException or FileNotFoundException or UnauthorizedAccessException;
    }

    private static void Release(params object?[] objects)
    {
        foreach (var item in objects)
        {
            if (item is not null && Marshal.IsComObject(item))
                Marshal.FinalReleaseComObject(item);
        }
    }
}
