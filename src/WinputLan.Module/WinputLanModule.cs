using System.Windows;
using SwissTechKnife.Sdk;

namespace WinputLan.Module;

/// <summary>
/// SwissTechKnife entry point. Winput LAN keeps its own process and window; the module starts it with the host
/// (elevated through the app's task when the user enabled administrator control), restarts it after a crash,
/// stops it when disabled and opens its window from the module page.
/// </summary>
public sealed class WinputLanModule : IBackgroundModule
{
    private IModuleContext? _context;
    private HostedAppSupervisor? _supervisor;

    public void Initialize(IModuleContext context)
    {
        _context = context;
        var deployment = new AppDeployment(context.InstallDirectory, context.DataDirectory, context.Log);
        _supervisor = new HostedAppSupervisor(deployment, context.DataDirectory, context.Log,
            (message, kind) => context.Dispatcher.BeginInvoke(new Action(() => context.Notify(message, kind))));
    }

    public void Start() => _supervisor?.Start();

    public void Stop() => _supervisor?.Stop();

    public FrameworkElement CreateView() => new ModulePage(_supervisor!, _context!);

    public void Dispose()
    {
        _supervisor?.Dispose();
        _supervisor = null;
    }
}
