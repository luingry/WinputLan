namespace WinputLan.Core
{
    public enum OutboundSession
    {
        None,
        Connecting,
        AwaitingApproval,
        Connected
    }

    public sealed class MachineListInput
    {
        public string LocalName { get; set; }
        public string LocalHotkey { get; set; }
        public string RemoteHotkey { get; set; }
        // This PC controlling another one.
        public OutboundSession Outbound { get; set; }
        public bool OutboundFocused { get; set; }
        public string TargetName { get; set; }
        public string TargetAddress { get; set; }
        public bool TargetRecognized { get; set; }
        // Another PC controlling this one.
        public bool InboundConnected { get; set; }
        public bool InboundFocused { get; set; }
        public string ControllerName { get; set; }
        public string ControllerAddress { get; set; }
        // A controller this PC already trusts, shown while idle when there is no recognized target to offer.
        public string KnownControllerName { get; set; }
        public string KnownControllerAddress { get; set; }
    }

    public sealed class MachineRowModel
    {
        public bool Visible { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        // Receives mouse and keyboard right now: green row and "Ativa" badge.
        public bool IsActive { get; set; }
        public bool IsController { get; set; }
        public string Badge { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public string Subtitle { get; set; }
    }

    public sealed class MachineListModel
    {
        public MachineRowModel Local { get; set; }
        public MachineRowModel Other { get; set; }
    }

    // Decides what each machine row shows; exactly one visible row is active at a time.
    public static class MachineListState
    {
        public static MachineListModel Build(MachineListInput input)
        {
            var local = new MachineRowModel { Visible = true, Name = Or(input.LocalName, "Este computador"), Subtitle = "Este computador" };
            var other = new MachineRowModel { Visible = false };

            if (input.InboundConnected)
            {
                var controller = Or(input.ControllerName, "PC controlador");
                local.IsActive = input.InboundFocused;
                local.Badge = local.IsActive ? "Ativa" : "Disponível";
                local.Status = local.IsActive ? "Recebendo entrada" : "Aguardando controle";
                local.Detail = local.IsActive ? "Mouse e teclado de " + controller : controller + " está com o controle";
                other = new MachineRowModel
                {
                    Visible = true, Name = controller, Address = Or(input.ControllerAddress, "Rede local"), IsController = true,
                    IsActive = !input.InboundFocused, Badge = input.InboundFocused ? "Disponível" : "Ativa",
                    Status = input.InboundFocused ? "Enviando entrada" : "Usando o próprio mouse e teclado",
                    Detail = input.InboundFocused ? "Controlando este PC" : "Controle volta pelo atalho dela",
                    Subtitle = "Controla este PC"
                };
                return new MachineListModel { Local = local, Other = other };
            }

            var hasTarget = !string.IsNullOrWhiteSpace(input.TargetName) || input.Outbound != OutboundSession.None || input.TargetRecognized;
            var connected = input.Outbound == OutboundSession.Connected;
            var focused = connected && input.OutboundFocused;
            local.IsController = connected;
            local.IsActive = !focused;
            local.Badge = local.IsActive ? "Ativa" : "Disponível";
            local.Status = focused ? "Enviando entrada" : "Recebendo entrada";
            // The shortcut hint belongs on the machine opposite to the one receiving input.
            local.Detail = focused ? "Volte com " + Or(input.LocalHotkey, "o atalho") : "Mouse e teclado deste PC";
            if (input.Outbound == OutboundSession.None && !input.TargetRecognized && !string.IsNullOrWhiteSpace(input.KnownControllerName))
            {
                other = new MachineRowModel
                {
                    Visible = true, Name = input.KnownControllerName, Address = Or(input.KnownControllerAddress, "Rede local"),
                    Badge = "Desconectada", Status = "Aguardando conexão", Detail = "Máquina já reconhecida", Subtitle = "Reconhecida"
                };
                return new MachineListModel { Local = local, Other = other };
            }
            if (!hasTarget) return new MachineListModel { Local = local, Other = other };

            other.Visible = true;
            other.Name = Or(input.TargetName, "Máquina vinculada");
            other.Address = Or(input.TargetAddress, "Rede local");
            other.IsActive = focused;
            switch (input.Outbound)
            {
                case OutboundSession.Connected:
                    other.Badge = focused ? "Ativa" : "Disponível";
                    other.Status = focused ? "Recebendo entrada" : "Pronta para receber";
                    other.Detail = focused ? "Controlada por este PC" : "Envie com " + Or(input.RemoteHotkey, "o atalho");
                    other.Subtitle = "Conectada";
                    break;
                case OutboundSession.Connecting:
                    other.Badge = "Conectando";
                    other.Status = "Conectando…";
                    other.Detail = other.Address;
                    other.Subtitle = input.TargetRecognized ? "Reconhecida" : "Requer código";
                    break;
                case OutboundSession.AwaitingApproval:
                    other.Badge = "Aguardando";
                    other.Status = "Aguardando aceite";
                    other.Detail = "Confirme na tela de " + other.Name;
                    other.Subtitle = input.TargetRecognized ? "Reconhecida" : "Requer código";
                    break;
                default:
                    other.Badge = "Desconectada";
                    other.Status = input.TargetRecognized ? "Clique para conectar" : "Vincule com o código";
                    other.Detail = input.TargetRecognized ? "Reconhecida: só precisa do aceite" : "Código novo necessário";
                    other.Subtitle = input.TargetRecognized ? "Reconhecida" : "Requer código";
                    break;
            }
            return new MachineListModel { Local = local, Other = other };
        }

        private static string Or(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value; }
    }
}
