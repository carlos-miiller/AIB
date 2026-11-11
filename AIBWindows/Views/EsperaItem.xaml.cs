using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using AIB.Services.Mail;
using UserControl = System.Windows.Controls.UserControl;

namespace AIB.Views
{
    /// <summary>
    /// Uma conversa à espera de resposta — a seção "Aguardando retorno" do modo e-mail.
    /// <para>
    /// Montada em código (<see cref="Preencher"/>), e não por binding: são regras de texto e de
    /// cor que dependem do relógio — "1 dia", "venceu", laranja, vermelho —, e cada uma viraria
    /// um conversor próprio. É o mesmo raciocínio de <c>MailListItem.PreencherMetadados</c>.
    /// </para>
    /// </summary>
    public partial class EsperaItem : UserControl
    {
        private static readonly CultureInfo PtBr = new("pt-BR");

        /// <summary>
        /// Os três estados de uma espera. O mesmo dado decide cor, ícone e texto, para que eles
        /// nunca discordem.
        /// </summary>
        public enum Estado { Normal, Vencendo, Vencida }

        public EsperaItem()
        {
            InitializeComponent();
        }

        /// <summary>A espera que este item mostra.</summary>
        public EsperaDeRetorno? Espera { get; private set; }

        /// <summary>O estado pintado — para o ensaio conferir sem comparar pincel.</summary>
        public Estado Pintado { get; private set; }

        private bool _aberto;

        /// <summary>Item aberto: o corpo do acordeão aparece. Um por vez — quem decide é a lista.</summary>
        public bool Aberto
        {
            get => _aberto;
            set
            {
                _aberto = value;
                Corpo.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                PintarCasca();
            }
        }

        /// <summary>Clique no item: a lista decide quem abre e quem fecha.</summary>
        public event EventHandler? PediuAlternar;

        private void Item_Click(object sender, MouseButtonEventArgs e) =>
            PediuAlternar?.Invoke(this, EventArgs.Empty);

        /// <summary>"Abrir com &lt;NOME&gt;": conversar sobre o que você enviou.</summary>
        public event EventHandler? PediuAbrirComIA;

        /// <summary>"Abrir no &lt;provedor&gt;": sai do app, para o webmail.</summary>
        public event EventHandler? PediuAbrirNoCliente;

        /// <summary>"+7 dias".</summary>
        public event EventHandler? PediuEstender;

        /// <summary>"Parar de vigiar" ou, vencida, "Dispensar".</summary>
        public event EventHandler? PediuParar;

        // e.Handled em todos: o clique não pode subir e fechar o acordeão.
        private void AbrirComIA_Click(object sender, RoutedEventArgs e) => Pedir(e, PediuAbrirComIA);
        private void AbrirNoCliente_Click(object sender, RoutedEventArgs e) => Pedir(e, PediuAbrirNoCliente);
        private void Estender_Click(object sender, RoutedEventArgs e) => Pedir(e, PediuEstender);
        private void Parar_Click(object sender, RoutedEventArgs e) => Pedir(e, PediuParar);

        private void Pedir(RoutedEventArgs e, EventHandler? pedido)
        {
            e.Handled = true;
            pedido?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// O nome da personalidade ativa, para "Abrir com &lt;NOME&gt;" — o mesmo campo do
        /// cabeçalho, nunca a string inteira em hard-code.
        /// </summary>
        public string NomeDaInteligencia
        {
            get => NomeDaIA.Text;
            set => NomeDaIA.Text = string.IsNullOrWhiteSpace(value) ? "AIB" : value;
        }

        /// <summary>
        /// O rótulo do botão do webmail ("Abrir no Gmail"). Vazio esconde o botão: sem endereço
        /// não há para onde ir, e um botão que não abre nada ensina que os desta tela não abrem.
        /// </summary>
        public string RotuloDoCliente
        {
            get => BotaoAbrirNoCliente.Content as string ?? "";
            set
            {
                BotaoAbrirNoCliente.Content = value;
                BotaoAbrirNoCliente.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>Preenche textos, barra e cor a partir da espera e do relógio.</summary>
        public void Preencher(EsperaDeRetorno espera, DateTime agora)
        {
            Espera = espera;
            var agoraUtc = agora.ToUniversalTime();

            AssuntoTexto.Text = Assunto(espera);
            AssuntoTexto.ToolTip = AssuntoTexto.Text;

            ParaTexto.Text = espera.Para.Length == 0 ? "" : "Para: " + espera.Para;
            ParaTexto.Visibility = espera.Para.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            LinhaTexto.Text = Linha(espera, agora);

            var (prazo, data) = Prazo(espera, agoraUtc);
            PrazoTexto.Text = prazo;
            DataDoPrazoTexto.Text = data;

            double consumido = espera.Consumido(agoraUtc);
            Cheia.Width = new GridLength(consumido, GridUnitType.Star);
            Vazia.Width = new GridLength(1 - consumido, GridUnitType.Star);

            HistoricoTexto.Text = Historico(espera);

            Pintado = EstadoDe(espera, agoraUtc);
            BotaoParar.Content = Pintado == Estado.Vencida ? "Dispensar" : "Parar de vigiar";
            Pintar();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Os textos — estáticos, para o ensaio conferir sem montar o controle
        // ─────────────────────────────────────────────────────────────────────

        public static string Assunto(EsperaDeRetorno e) =>
            string.IsNullOrWhiteSpace(e.Assunto) ? "(assunto não guardado)" : e.Assunto;

        /// <summary>
        /// "Enviado Hoje · 16/10/2026, 16:24 · sem resposta" na primeira mensagem;
        /// "Você respondeu Terça · 20/10/2026, 09:12 · 3 mensagens" quando a conversa já andou.
        /// </summary>
        public static string Linha(EsperaDeRetorno e, DateTime agora)
        {
            string quando = ConversaDeEmail.Quando(e.EnviadaEm.ToLocalTime(), agora);

            return e.Mensagens <= 1
                ? $"Enviado {quando} · sem resposta"
                : $"Você respondeu {quando} · {e.Mensagens} mensagens";
        }

        /// <summary>
        /// O prazo em duas linhas: "7 dias" / "até 23/10", "hoje" no último dia, e "venceu" /
        /// "em 23/10" depois dele.
        /// </summary>
        public static (string Prazo, string Data) Prazo(EsperaDeRetorno e, DateTime agoraUtc)
        {
            string data = e.Ate.ToLocalTime().ToString("dd/MM", PtBr);

            if (e.Vencida(agoraUtc)) return ("venceu", "em " + data);

            int dias = e.DiasRestantes(agoraUtc);
            string prazo = dias switch
            {
                0 => "hoje",
                1 => "1 dia",
                _ => $"{dias} dias"
            };

            return (prazo, "até " + data);
        }

        public static string Historico(EsperaDeRetorno e)
        {
            string enviada = e.EnviadaEm.ToLocalTime().ToString("dd/MM HH:mm", PtBr);
            string fim = e.Ate.ToLocalTime().ToString("dd/MM", PtBr);
            string quem = e.Mensagens <= 1 ? "Você enviou" : "Você respondeu";

            return $"{enviada}  {quem}\n{fim}        fim do prazo de retorno";
        }

        public static Estado EstadoDe(EsperaDeRetorno e, DateTime agoraUtc) =>
            e.Vencida(agoraUtc) ? Estado.Vencida
            : e.Vencendo(agoraUtc) ? Estado.Vencendo
            : Estado.Normal;

        // ─────────────────────────────────────────────────────────────────────
        // As cores
        // ─────────────────────────────────────────────────────────────────────

        private void Pintar()
        {
            string tinta = Pintado switch
            {
                Estado.Vencida => "DangerBrush",
                Estado.Vencendo => "WarnBrush",
                _ => "AccentLilacBrush"
            };

            string fundoDoCirculo = Pintado switch
            {
                Estado.Vencida => "DangerFill12Brush",
                Estado.Vencendo => "WarnFill10Brush",
                _ => "AccentFill12Brush"
            };

            Icone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tinta);
            Circulo.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, fundoDoCirculo);
            PrazoTexto.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, tinta);
            Preenchimento.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, tinta);

            // O relógio vira alerta quando venceu: a cor sozinha não chega a quem não a distingue.
            Icone.Data = System.Windows.Media.Geometry.Parse(Pintado == Estado.Vencida
                ? "M10,3 L18,17 H2 Z M10,8.5 V12 M10,14.5 V15"
                : "M10,2.5 A7.5,7.5 0 1 1 9.99,2.5 Z M10,5.5 V10 L13,12");

            PintarCasca();
        }

        private void PintarCasca()
        {
            string borda = Pintado switch
            {
                Estado.Vencida => "DangerBorderBrush",
                Estado.Vencendo => "WarnBorderBrush",
                _ => _aberto ? "AccentBorderStrongBrush" : "AccentEdge28Brush"
            };

            Casca.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, borda);
        }
    }
}
