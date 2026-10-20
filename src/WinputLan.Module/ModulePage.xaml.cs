using System.Windows;
using System.Windows.Media.Animation;
using SwissTechKnife.Sdk;

namespace WinputLan.Module;

public partial class ModulePage
{
    private readonly HostedAppSupervisor _supervisor;
    private readonly IModuleContext _context;
    private HostedAppState? _shownState;
    private bool _shownElevated;

    internal ModulePage(HostedAppSupervisor supervisor, IModuleContext context)
    {
        _supervisor = supervisor;
        _context = context;
        InitializeComponent();
        _supervisor.Changed += OnChanged;
        Apply(_supervisor.Status);
    }

    private void OnChanged() =>
        _context.Dispatcher.BeginInvoke(new Action(() => Apply(_supervisor.Status)));

    private void Apply(HostedAppStatus status)
    {
        ConfirmAdminButton.IsEnabled = !status.Busy;
        AllowNetworkButton.IsEnabled = !status.Busy;
        var running = status.State == HostedAppState.Running;
        SetNotice(AdminNotice, status.AdminNeedsConfirm && status.State != HostedAppState.RunningOutside);
        SetNotice(NetworkNotice, status.NetworkBlocked && running);

        if (_shownState == status.State && _shownElevated == status.Elevated)
            return;
        var first = _shownState is null;
        _shownState = status.State;
        _shownElevated = status.Elevated;

        var (label, detail, soft, strong) = status.State switch
        {
            HostedAppState.Running => ("Em execução",
                "O Winput LAN está ativo: os atalhos e as conexões com os outros PCs funcionam mesmo com esta janela fechada.",
                "Brush.Success.Soft", "Brush.Success"),
            HostedAppState.Starting => ("Iniciando",
                "O Winput LAN está sendo iniciado.",
                "Brush.Info.Soft", "Brush.Info"),
            HostedAppState.RunningOutside => ("Aberto fora do SwissTechKnife",
                "Outro Winput LAN já está aberto e continua cuidando do mouse e do teclado. O módulo assume assim que ele for fechado.",
                "Brush.Warning.Soft", "Brush.Warning"),
            HostedAppState.Failed => ("Parado",
                "O Winput LAN fechou inesperadamente várias vezes. Abra-o para tentar de novo.",
                "Brush.Danger.Soft", "Brush.Danger"),
            _ => ("Parado",
                "O Winput LAN não está em execução. Abra-o para compartilhar o mouse e o teclado.",
                "Brush.Surface4", "Brush.Text.Secondary"),
        };

        StatusText.Text = label;
        StatusDetail.Text = detail;
        ElevationText.Text = running
            ? status.Elevated
                ? "Com permissão de administrador: também controla apps como o Gerenciador de Tarefas."
                : "Sem permissão de administrador. Para controlar apps de administrador, marque a opção na janela do Winput LAN."
            : string.Empty;
        ElevationText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StatusChip.SetResourceReference(BackgroundProperty, soft);
        StatusText.SetResourceReference(ForegroundProperty, strong);

        if (!first)
            FadeIn(StatusChip, StatusDetail, ElevationText);
    }

    private void SetNotice(FrameworkElement notice, bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (notice.Visibility == target)
            return;
        notice.Visibility = target;
        if (visible)
            FadeIn(notice);
    }

    private void FadeIn(params UIElement[] elements)
    {
        var duration = TryFindResource("Motion.Normal") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(200));
        var fade = new DoubleAnimation(0.4, 1, duration) { EasingFunction = TryFindResource("Ease.Out") as IEasingFunction };
        fade.Freeze();
        foreach (var element in elements)
            element.BeginAnimation(OpacityProperty, fade);
    }

    private void OpenButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.ShowWindow();

    private void ConfirmAdminButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.ConfirmAdministrator();

    private void AllowNetworkButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.AllowNetwork();
}
