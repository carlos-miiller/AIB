using System;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Input;
using AIB.Services;
using AIB.Services.Mail;

namespace AIB.Views
{
    /// <summary>
    /// O item de e-mail das DUAS telas: a pilha do orbe (shadow-assistant §4.8) e a lista da
    /// área central (tela-chat-v3 §3.10).
    /// <para>
    /// São o mesmo dado com a mesma leitura de relance, e duas cópias divergiriam na primeira
    /// correção feita só de um lado. As diferenças vivem em PROPRIEDADES, não em arquivos.
    /// </para>
    /// </summary>
    public partial class MailListItem : UserControl
    {
        public MailListItem()
        {
            InitializeComponent();
        }

        // ─────────────────────────────────────────────────────────────────────
        // As diferenças permitidas
        // ─────────────────────────────────────────────────────────────────────

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius), typeof(CornerRadius), typeof(MailListItem),
                new PropertyMetadata(new CornerRadius(12)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly DependencyProperty RealceLilasProperty =
            DependencyProperty.Register(
                nameof(RealceLilas), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false));

        public bool RealceLilas
        {
            get => (bool)GetValue(RealceLilasProperty);
            set => SetValue(RealceLilasProperty, value);
        }

        /// <summary>
        /// Escala de JANELA em vez de escala de painel — §3.10.
        /// <para>
        /// No orbe e no painel o item vive em 252px e as medidas são apertadas de propósito. Na
        /// área central há largura de janela, e manter 12.5px ali deixaria a lista parecendo um
        /// widget colado numa tela grande.
        /// </para>
        /// </summary>
        public static readonly DependencyProperty EscalaDeJanelaProperty =
            DependencyProperty.Register(
                nameof(EscalaDeJanela), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false));

        public bool EscalaDeJanela
        {
            get => (bool)GetValue(EscalaDeJanelaProperty);
            set => SetValue(EscalaDeJanelaProperty, value);
        }

        /// <summary>
        /// A linha de metadados — data, tamanho da conversa e de quem é a vez.
        /// <para>
        /// Padrão FALSO porque ela NÃO existe no item do orbe: lá o balão tem 252px e três
        /// itens, e uma quarta linha de texto por item comeria a pilha inteira. Aqui há largura
        /// de janela. É a propriedade que a spec pede para o controle poder ser o mesmo nos
        /// dois lugares.
        /// </para>
        /// </summary>
        public static readonly DependencyProperty MostrarMetadadosProperty =
            DependencyProperty.Register(
                nameof(MostrarMetadados), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false));

        public bool MostrarMetadados
        {
            get => (bool)GetValue(MostrarMetadadosProperty);
            set => SetValue(MostrarMetadadosProperty, value);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O acordeão
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Item aberto: o corpo aparece e o resumo de duas linhas sai.
        /// <para>
        /// Sai porque o corpo já traz o resumo INTEIRO, e mantê-lo repetiria a primeira frase
        /// duas vezes na mesma caixa.
        /// </para>
        /// <para>
        /// Um bool, e não um <c>Expander</c>: o Expander traz chevron e template próprios, e
        /// a spec desenha outra coisa.
        /// </para>
        /// </summary>
        public static readonly DependencyProperty AbertoProperty =
            DependencyProperty.Register(
                nameof(Aberto), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false));

        public bool Aberto
        {
            get => (bool)GetValue(AbertoProperty);
            set => SetValue(AbertoProperty, value);
        }

        /// <summary>
        /// O rótulo do botão primário: "Abrir com Kai", "Abrir com Ayano".
        /// <para>
        /// Vem do MESMO campo que preenche o nome no header, e muda quando a personalidade
        /// muda. Nunca a string inteira em hard-code — foi o que deixou "Kai" cravado em meia
        /// dúzia de lugares.
        /// </para>
        /// </summary>
        public static readonly DependencyProperty NomeDaInteligenciaProperty =
            DependencyProperty.Register(
                nameof(NomeDaInteligencia), typeof(string), typeof(MailListItem),
                // "AIB", o mesmo padrão de ChatWindow.NomeDaInteligencia quando não há
                // personagem ativo. Era "a IA", e a lista dizia "Abrir com a IA" enquanto o
                // placeholder ao lado dizia "Fale com AIB...".
                new PropertyMetadata("AIB"));

        public string NomeDaInteligencia
        {
            get => (string)GetValue(NomeDaInteligenciaProperty);
            set => SetValue(NomeDaInteligenciaProperty, value);
        }

        /// <summary>
        /// O rótulo do botão secundário, pelo PROVEDOR da conta: "Abrir no Gmail", "Abrir no
        /// Outlook". Provedor desconhecido vira "Abrir no cliente".
        /// </summary>
        public static readonly DependencyProperty RotuloDoClienteProperty =
            DependencyProperty.Register(
                nameof(RotuloDoCliente), typeof(string), typeof(MailListItem),
                new PropertyMetadata("Abrir no cliente"));

        public string RotuloDoCliente
        {
            get => (string)GetValue(RotuloDoClienteProperty);
            set => SetValue(RotuloDoClienteProperty, value);
        }

        /// <summary>
        /// Já existe conversa com a IA sobre este e-mail — e com ela, o botão de descartá-la.
        /// <para>
        /// Sem conversa o botão não aparece: ele não teria o que fazer, e um botão que abre uma
        /// confirmação para nada ensina a clicar sem ler.
        /// </para>
        /// <para>
        /// Visibilidade posta pelo callback, e não por binding: o item também é montado solto
        /// (ensaios, pilha do orbe), e um binding por ancestral só se resolve dentro da árvore.
        /// </para>
        /// </summary>
        public static readonly DependencyProperty TemConversaProperty =
            DependencyProperty.Register(
                nameof(TemConversa), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false, (d, e) =>
                    ((MailListItem)d).BotaoDescartarConversa.Visibility =
                        (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

        public bool TemConversa
        {
            get => (bool)GetValue(TemConversaProperty);
            set => SetValue(TemConversaProperty, value);
        }

        /// <summary>
        /// Há quem guarde o que for ignorado. Na lista da área central, sim; no orbe, não.
        /// Visibilidade pelo callback, pelo mesmo motivo de <see cref="TemConversa"/>.
        /// </summary>
        public static readonly DependencyProperty PodeIgnorarProperty =
            DependencyProperty.Register(
                nameof(PodeIgnorar), typeof(bool), typeof(MailListItem),
                new PropertyMetadata(false, (d, e) =>
                    ((MailListItem)d).BotaoIgnorar.Visibility =
                        (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

        public bool PodeIgnorar
        {
            get => (bool)GetValue(PodeIgnorarProperty);
            set => SetValue(PodeIgnorarProperty, value);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que o item pede
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>"Ignorar": tirar da lista até chegar mensagem nova. Não toca o servidor.</summary>
        public event EventHandler? PediuIgnorar;

        private void Ignorar_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // não deixa o clique subir e fechar o acordeão
            PediuIgnorar?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Descartar a conversa havida com a IA sobre este e-mail — o e-mail não é tocado.</summary>
        public event EventHandler? PediuDescartarConversa;

        private void DescartarConversa_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // não deixa o clique subir e fechar o acordeão
            PediuDescartarConversa?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Clique no item: a lista decide quem abre e quem fecha (um por vez).</summary>
        public event EventHandler? PediuAlternar;

        /// <summary>"Abrir com &lt;NOME&gt;": traz o e-mail para dentro do chat (§3.11).</summary>
        public event EventHandler? PediuAbrirComIA;

        /// <summary>"Abrir no &lt;provedor&gt;": sai do app, para o webmail ou cliente da conta.</summary>
        public event EventHandler? PediuAbrirNoCliente;

        private void Item_Click(object sender, MouseButtonEventArgs e)
        {
            // O clique no item NÃO abre mais o cliente de e-mail — isso virou o botão
            // secundário do corpo. Ele EXPANDE, que é o gesto que revela a decisão em vez de
            // tomá-la por você.
            PediuAlternar?.Invoke(this, EventArgs.Empty);
        }

        private void AbrirComIA_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // não deixa o clique subir e fechar o acordeão
            PediuAbrirComIA?.Invoke(this, EventArgs.Empty);
        }

        private void AbrirNoCliente_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            PediuAbrirNoCliente?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Abre um endereço fora do app. Devolve false quando não havia o que abrir.
        /// <para>
        /// URL vazia não pode virar <c>Process.Start("")</c>, que levanta exceção. O item vive
        /// em duas telas: falhar aqui derrubaria o orbe, que é a única coisa entre o usuário e a
        /// lista que ele acabou de ler.
        /// </para>
        /// <para>
        /// Estático e aqui, e não copiado em cada tela: é o mesmo gesto nos dois lugares, e a
        /// segunda cópia perderia o guarda na primeira correção feita só de um lado.
        /// </para>
        /// </summary>
        public static bool AbrirNoNavegador(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL] não consegui abrir o endereço: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // A linha de metadados, montada
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Preenche a linha de metadados. Em código e não por binding: são três regras de texto
        /// — dia relativo até sete dias, plural do tamanho, separador — e um MultiBinding faria
        /// cada uma virar um converter próprio.
        /// </summary>
        public void PreencherMetadados(MailSummary item, DateTime agora)
        {
            MetadadosTexto.Text = Metadados(item, agora);
            RemetenteTexto.Text = Remetente(item, agora);
            VezTexto.Text = ConversaDeEmail.DeQuemEhAVez(item?.AwaitingMe ?? true);

            // "Nova mensagem" pede ação e fica em lilás; "Aguardando retorno" é estado e fica
            // apagado junto do resto da linha. Ler os dois na mesma cor esconderia o primeiro.
            VezTexto.SetResourceReference(
                ForegroundProperty,
                (item?.AwaitingMe ?? true) ? "AccentLilacBrush" : "TextSecondaryBrush");
        }

        /// <summary>
        /// A linha de cima do corpo do acordeão: "fulano@x.com · Hoje · 10/09/2026, 09:12".
        /// <para>
        /// Vazia quando não se sabe quem mandou — melhor não ter a linha do que ter um "·"
        /// solto anunciando um campo que não veio.
        /// </para>
        /// </summary>
        public static string Remetente(MailSummary item, DateTime agora)
        {
            if (item == null) return "";

            string quem = (item.De ?? "").Trim();
            string quando = item.LastMessageAt == default
                ? ""
                : ConversaDeEmail.Quando(item.LastMessageAt, agora);

            if (quem.Length == 0) return quando;
            return quando.Length == 0 ? quem : quem + " · " + quando;
        }

        public static string Metadados(MailSummary item, DateTime agora)
        {
            if (item == null) return "";

            string quando = item.LastMessageAt == default
                ? ""
                : ConversaDeEmail.Quando(item.LastMessageAt, agora);

            string tamanho = ConversaDeEmail.Tamanho(item.MessageCount);

            return quando.Length == 0 ? tamanho : quando + " · " + tamanho;
        }
    }
}
