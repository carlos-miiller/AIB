using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Memory;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Constrói cada janela de verdade e a força a medir, arranjar e desenhar.
    /// <para>
    /// O <see cref="ThemeResourcesTests"/> confere as chaves como texto, mas não pega tudo:
    /// um <c>{StaticResource}</c> dentro de um ControlTemplate só é resolvido quando aquele
    /// controle é criado, e um Setter com valor de tipo errado só estoura no parse do BAML.
    /// Renderizar a janela exercita os dois caminhos.
    /// </para>
    /// <para>
    /// Definindo <c>AIB_UI_PNG=1</c>, cada janela também é salva como PNG na pasta indicada
    /// por <c>AIB_UI_PNG_DIR</c> (ou no temporário). Serve para conferir o desenho contra as
    /// specs sem precisar abrir o app.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class WindowSmokeTests
    {
        /// <summary>
        /// WPF exige STA. O xUnit roda em MTA, então cada ensaio abre a sua própria thread —
        /// é mais simples que trazer um pacote só para isso, e deixa a falha visível como
        /// exceção normal.
        /// </summary>
        private static void EmSta(Action acao) => WpfHost.EmSta(acao);

        private static void GarantirRecursos() => WpfHost.GarantirRecursos();

        private static void Desenhar(Window janela, string nome, double largura = 0, double altura = 0)
        {
            double w = largura > 0 ? largura : janela.Width;
            double h = altura > 0 ? altura : janela.Height;

            // O conteúdo, e não a Window. RenderTargetBitmap sobre uma Window que nunca foi
            // exibida desenha uma imagem em branco: a árvore visual da janela só existe depois
            // do Show(). O conteúdo medido e arranjado à mão, não.
            if (janela.Content is not FrameworkElement raiz) return;

            raiz.Measure(new Size(w, h));
            raiz.Arrange(new Rect(0, 0, w, h));
            raiz.UpdateLayout();

            if (Environment.GetEnvironmentVariable("AIB_UI_PNG") != "1") return;

            string dir = Environment.GetEnvironmentVariable("AIB_UI_PNG_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Fundo escuro no lugar do desktop, como o mock faz: sem ele a janela de vidro
                // e a sombra ficam invisíveis sobre o alfa zerado do PNG.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x1E)),
                                 null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(raiz), null, new Rect(0, 0, w, h));
            }

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));

            using var fs = File.Create(Path.Combine(dir, nome + ".png"));
            png.Save(fs);
        }

        /// <summary>Desenha um controle avulso, sem janela em volta.</summary>
        private static void DesenharSolto(FrameworkElement raiz, string nome, double w, double h)
        {
            raiz.Measure(new Size(w, h));
            raiz.Arrange(new Rect(0, 0, w, h));
            raiz.UpdateLayout();

            if (Environment.GetEnvironmentVariable("AIB_UI_PNG") != "1") return;

            string dir = Environment.GetEnvironmentVariable("AIB_UI_PNG_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x14, 0x1C)),
                                 null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(raiz), null, new Rect(8, 8, w - 16, h - 16));
            }

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));

            using var fs = File.Create(Path.Combine(dir, nome + ".png"));
            png.Save(fs);
        }

        /// <summary>
        /// SettingsService apontado para um arquivo descartável. O ensaio NUNCA pode escrever
        /// nas configurações reais do usuário.
        /// </summary>
        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-ensaio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        /// <summary>
        /// Provider mudo. A tela de chat só precisa de um para montar; nenhum ensaio daqui
        /// gera texto, então qualquer chamada que escape é erro do ensaio, não silêncio.
        /// </summary>
        private sealed class ProviderMudo : AIB.Services.Ai.IChatProvider
        {
            public string Name => "Ensaio";
            public string Model => "ensaio";

            public async System.Collections.Generic.IAsyncEnumerable<AIB.Services.Ai.StreamChunk> StreamAsync(
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatTool> tools,
                AIB.Services.Ai.ChatRequestOptions options,
                [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct)
            {
                await System.Threading.Tasks.Task.CompletedTask;
                yield break;
            }

            public System.Threading.Tasks.Task<AIB.Services.Ai.ChatCompletionResult> CompleteAsync(
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
                System.Collections.Generic.IReadOnlyList<OpenAI.Chat.ChatTool> tools,
                AIB.Services.Ai.ChatRequestOptions options,
                System.Threading.CancellationToken ct) =>
                throw new NotSupportedException("ensaio de janela não conversa com modelo");

            public System.Threading.Tasks.Task WarmupAsync(System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.CompletedTask;
        }

        private sealed class FabricaMuda : AIB.Services.Ai.IChatProviderFactory
        {
            private readonly AIB.Services.Ai.IChatProvider _p = new ProviderMudo();
            public AIB.Services.Ai.IChatProvider GetProvider(UserAppSettings settings) => _p;
        }

        private static ConversationService ConversaDescartavel(SettingsService servico)
        {
            var registro = new ToolRegistry();
            var contador = new TokenCounter();
            var fabrica = new FabricaMuda();
            var loop = new AgentLoop(registro, fabrica, servico, contador);

            // Raiz de memória redirecionada: sem isto o ensaio gravaria em ~/.AIB/memory.
            return new ConversationService(
                servico, registro, loop, contador, fabrica,
                Path.Combine(Path.GetTempPath(), "aib-ensaio-mem-" + Guid.NewGuid().ToString("N")));
        }


        [Fact]
        public void TelaDeConfiguracoes_MontaEDesenha()
        {
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                Desenhar(janela, "configuracoes");

                // Uma passada por página. Com o menu lateral não existe mais "a tela inteira":
                // cada página é uma View, e desenhar só a primeira deixaria as outras três sem
                // nenhuma verificação de montagem — inclusive a de e-mail, que é a única com
                // DataTemplate e conversores.
                foreach (var pagina in new[]
                {
                    PaginaDeConfiguracoes.Identidade,
                    PaginaDeConfiguracoes.Conexao,
                    PaginaDeConfiguracoes.Email,
                    PaginaDeConfiguracoes.Avancado
                })
                {
                    janela.IrPara(pagina);
                    Desenhar(janela, "configuracoes-" + pagina.ToString().ToLowerInvariant(), 960, 650);
                }

                janela.Close();
            });
        }

        [Fact]
        public void TelaDeConfiguracoes_SalvarNasceDesabilitado()
        {
            // O7 / A8: sem isso o usuário não sabe se mexeu em algo.
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.Measure(new Size(janela.Width, janela.Height));

                var salvar = (System.Windows.Controls.Button)janela.FindName("SaveButton");
                salvar.Should().NotBeNull();
                salvar.IsEnabled.Should().BeFalse("Salvar só habilita quando algo muda");

                janela.Close();
            });
        }

        [Fact]
        public void TelaDeChat_MontaEDesenha()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                var janela = new ChatWindow(ConversaDescartavel(servico), servico);
                Desenhar(janela, "chat", 820, 605);
                janela.Close();
            });
        }

        [Fact]
        public void ABolhaDoUsuario_NASCE_ComOEstiloAzul()
        {
            // A bolha azul sumiu da tela e nenhum ensaio viu. O que existia desenhava um PNG e
            // nao afirmava nada sobre o estilo, entao um Style nulo passava batido: a Border
            // continuava sendo criada, so que sem fundo, sem padding e sem alinhamento.
            //
            // A causa era Resources["UserBubble"] — o indexador olha o dicionario DESTA janela e
            // mais nada. O estilo tinha mudado para o Themes/Controls.xaml, que e mesclado no
            // Application.Resources, e so o FindResource chega la.
            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                var janela = new ChatWindow(ConversaDescartavel(servico), servico);
                Desenhar(janela, "chat-bolha-descartar", 820, 605);

                const System.Reflection.BindingFlags Privados =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

                typeof(ChatWindow).GetMethod("AddUserBubble", Privados)!
                    .Invoke(janela, new object[] { "oi" });

                var lista = (System.Windows.Controls.Panel)janela.FindName("MessagesPanel");

                var bolha = lista.Children.OfType<Grid>()
                    .SelectMany(l => l.Children.OfType<Border>())
                    .Single();

                bolha.Style.Should().NotBeNull("sem estilo a bolha nasce transparente e sem forma");
                bolha.Background.Should().Be(Application.Current.FindResource("PrimaryBrush"),
                                             "e o azul do usuario que a §3.5 pede");
                bolha.HorizontalAlignment.Should().Be(System.Windows.HorizontalAlignment.Right);

                janela.Close();
            });
        }

        [Fact]
        public void TelaDeChat_ComBolhas_DesenhaAsTresVariantes()
        {
            // Chama os criadores de bolha por reflexão. São privados, e expor cada um só para
            // o ensaio ver seria alargar a superfície pública da janela por causa de um PNG.
            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                var janela = new ChatWindow(ConversaDescartavel(servico), servico);

                // Uma passada de arranjo antes: MaxWidth da bolha é 74% da largura da lista, e
                // a lista só tem largura depois de medida.
                Desenhar(janela, "chat-vazio-descartar", 820, 605);

                const System.Reflection.BindingFlags Privados =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var tipo = typeof(ChatWindow);

                tipo.GetMethod("AddUserBubble", Privados)!
                    .Invoke(janela, new object[] { "Leia o AGENTS.md e me diga o que falta na fase 3." });

                tipo.GetMethod("AddAgentBubble", Privados)!
                    .Invoke(janela, new object?[]
                    {
                        "Li o arquivo. Faltam **dois** itens: validar a promoção de ato contra o "
                        + "Ollama real e produzir um `facts.md` de execução, que hoje só existe "
                        + "em teste unitário."
                    });

                tipo.GetMethod("AddUserBubble", Privados)!.Invoke(janela, new object[] { "Ok" });

                // Resposta curta: é o caso que denunciou o problema. Um FlowDocument aceita
                // toda a largura oferecida, então "Kai online. Olá." vinha numa bolha de 74%
                // da lista com um vão enorme à direita, enquanto a bolha do usuário — que é
                // um TextBlock — encolhia certo.
                tipo.GetMethod("AddAgentBubble", Privados)!
                    .Invoke(janela, new object?[] { "Kai online. Olá." });

                tipo.GetMethod("AddTypingIndicator", Privados)!.Invoke(janela, null);

                // AnimateBubbleIn zera a opacidade e anima de volta para 1. A animação é
                // tocada pelo relógio de composição, que só corre quando há renderização de
                // verdade — numa janela nunca exibida ela fica parada no quadro zero e a
                // bolha some do PNG. Aqui a animação é retirada e o estado final aplicado à
                // mão. Não é problema da tela: no app o laço existe.
                var lista = (System.Windows.Controls.Panel)janela.FindName("MessagesPanel");
                foreach (FrameworkElement filho in lista.Children)
                {
                    filho.BeginAnimation(UIElement.OpacityProperty, null);
                    filho.Opacity = 1;
                    filho.RenderTransform = Transform.Identity;
                }

                Desenhar(janela, "chat-bolhas", 820, 605);
                janela.Close();
            });
        }

        /// <summary>
        /// Força a cadeia a desenhar tudo que está na fila, sem esperar o ritmo de meio segundo.
        /// Por reflexão para não abrir um método de produção só por causa do ensaio — é o mesmo
        /// caminho que os outros ensaios daqui usam para alcançar membros privados.
        /// </summary>
        private static void Drenar(ToolChainView cadeia)
        {
            typeof(ToolChainView)
                .GetMethod("DrenarParaEnsaio", System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance)!
                .Invoke(cadeia, null);
        }

        [Fact]
        public void CadeiaDeAcoes_ColapsaConcluidasEMostraAEmCurso()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                var cadeia = new ToolChainView();

                // Sete concluídas: a trilha mostra no máximo cinco por vez e a barra de 4px é
                // o único aviso de que há mais (A11 — nada de contador "+N").
                for (int i = 0; i < 7; i++)
                {
                    string id = "t" + i;
                    cadeia.Iniciar(id, "read", $@"C:\Users\Carlo\CPAPS\AIB\arquivo{i}.cs");
                    cadeia.Concluir(id, falhou: false, recusada: false,
                        artefato: new Artifact(ArtifactKind.FileRead, "read",
                                               $@"C:\Users\Carlo\CPAPS\AIB\arquivo{i}.cs", false),
                        detalhe: null);
                Drenar(cadeia);
                }

                cadeia.Concluidas.Should().Be(7);

                // E uma em curso ao lado.
                cadeia.Iniciar("t7", "shell", "dotnet test AIB.Tests");
                cadeia.TemAcaoEmCurso.Should().BeTrue();

                DesenharSolto(cadeia, "cadeia-acoes", 640, 70);
            });
        }

        [Fact]
        public void CadeiaDeAcoes_VariasEmParalelo_MostramATodasEmVezDaUltima()
        {
            // O modelo pede várias ferramentas na mesma iteração e elas rodam juntas. O chip é
            // um só: antes, cada anúncio sobrescrevia o anterior e a tela mostrava uma das
            // três. Depois as três terminavam quase juntas e viravam três ícones de uma vez —
            // parecia rajada, era a fila escondida atrás do último nome.
            EmSta(() =>
            {
                GarantirRecursos();

                var cadeia = new ToolChainView();
                cadeia.Iniciar("a", "read", @"C:.cs");
                cadeia.Iniciar("b", "read", @"C:.cs");
                cadeia.Iniciar("c", "read", @"C:\c.cs");

                var nome = (System.Windows.Controls.TextBlock)cadeia.FindName("NomeFerramenta");
                nome.Text.Should().Be("3 ferramentas", "as três em curso precisam ser visíveis");

                // Uma termina: a contagem acompanha, e o chip não some com duas ainda rodando.
                cadeia.Concluir("a", false, false,
                    new Artifact(ArtifactKind.FileRead, "read", @"C:.cs", false), null);
                Drenar(cadeia);

                cadeia.TemAcaoEmCurso.Should().BeTrue("ainda há duas em curso");
                nome.Text.Should().Be("2 ferramentas");

                cadeia.Concluir("b", false, false,
                    new Artifact(ArtifactKind.FileRead, "read", @"C:.cs", false), null);
                Drenar(cadeia);
                nome.Text.Should().Be("Lendo arquivo",
                    "sobrando uma, ela volta a aparecer — e pelo ROTULO, que e o que a "
                    + "pessoa olhando a tela entende. 'read' e endereco, nao noticia");

                cadeia.Concluir("c", false, false,
                    new Artifact(ArtifactKind.FileRead, "read", @"C:\c.cs", false), null);
                Drenar(cadeia);

                cadeia.TemAcaoEmCurso.Should().BeFalse();
                cadeia.Concluidas.Should().Be(3);
            });
        }

        [Fact]
        public void CadeiaDeAcoes_FalhaFicaVisivelAteAProximaAcao()
        {
            // §4.7: o erro é o que o usuário precisa ler. Colapsar num ícone de 22px junto com
            // os sucessos transformaria a falha em detalhe.
            EmSta(() =>
            {
                GarantirRecursos();

                var cadeia = new ToolChainView();

                cadeia.Iniciar("a", "write", @"C:\Windows\System32\config\algo.txt");
                cadeia.Concluir("a", falhou: true, recusada: false,
                    artefato: new Artifact(ArtifactKind.FileWritten, "write",
                                           @"C:\Windows\System32\config\algo.txt", true, "acesso negado"),
                    detalhe: "ERRO: acesso negado ao caminho.");
                Drenar(cadeia);

                cadeia.TemAcaoEmCurso.Should().BeTrue("a falha continua visível");
                cadeia.Concluidas.Should().Be(0, "a falha ainda não virou ícone");

                DesenharSolto(cadeia, "cadeia-falha", 640, 70);

                // A ação seguinte recolhe a falha para a trilha.
                cadeia.RecolherFalhaPendente();
                Drenar(cadeia);
                cadeia.Concluidas.Should().Be(1);
            });
        }

        [Fact]
        public void CadeiaDeAcoes_AguardandoConfirmacao_TrocaORotulo()
        {
            // §5.3: enquanto o card de confirmação está na tela, a fila fica bloqueada e o chip
            // correspondente diz "Aguardando", não "Executando".
            EmSta(() =>
            {
                GarantirRecursos();

                var cadeia = new ToolChainView();
                cadeia.Iniciar("x", "shell", "Remove-Item -Recurse C:\\temp");
                cadeia.Aguardar();

                var rotulo = (System.Windows.Controls.TextBlock)cadeia.FindName("RotuloEstado");
                rotulo.Text.Should().Be("Aguardando");

                DesenharSolto(cadeia, "cadeia-aguardando", 640, 70);
            });
        }

        [Fact]
        public void CadeiaDeAcoes_AcaoSemArtefato_TooltipDizOAlvo()
        {
            // O tooltip de um glob dizia só "glob": sem artefato, o literal era vazio. O alvo
            // anunciado no começo da ação é o que sobra para descrevê-la, e precisa chegar ao fim.
            EmSta(() =>
            {
                GarantirRecursos();

                var cadeia = new ToolChainView();
                cadeia.Iniciar("g", "glob", @"*.html em C:\temp\emails fisio");
                cadeia.Concluir("g", falhou: false, recusada: false, artefato: null, detalhe: null);
                Drenar(cadeia);

                var trilha = (System.Windows.Controls.Panel)cadeia.FindName("Trilha");
                var icone = (FrameworkElement)trilha.Children[0];
                var conteudo = (System.Windows.Controls.StackPanel)icone.ToolTip;
                var linha = (System.Windows.Controls.TextBlock)conteudo.Children[0];

                linha.Text.Should().Be(@"glob  •  *.html em C:\temp\emails fisio");
                System.Windows.Controls.ToolTipService.GetShowDuration(icone)
                    .Should().Be(int.MaxValue, "o padrão do WPF fecha em 5 segundos, no meio da leitura");

                var casca = new System.Windows.Controls.ToolTip { Content = conteudo };
                DesenharSolto(casca, "tooltip-simples", 420, 60);
            });
        }

        [Fact]
        public void Tooltip_TemACascaDoDesignSystem()
        {
            // D8: sem estilo próprio, valia a casca nativa do Windows — fundo claro e canto reto.
            EmSta(() =>
            {
                GarantirRecursos();

                var estilo = Application.Current.TryFindResource(typeof(System.Windows.Controls.ToolTip)) as Style;
                estilo.Should().NotBeNull();

                var tooltip = new System.Windows.Controls.ToolTip { Content = "Painel lateral" };
                tooltip.Measure(new Size(200, 60));

                ((System.Windows.Media.SolidColorBrush)tooltip.Background).Color
                    .Should().Be(((System.Windows.Media.SolidColorBrush)Application.Current
                        .FindResource("SurfaceTooltipBrush")).Color);
            });
        }

        [Fact]
        public void PainelLateral_TooltipEmBloco_SegueOMock()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                var comando = ActionLogService.Construir(
                    "shell",
                    new Artifact(ArtifactKind.CommandRun, "shell",
                                 @"Get-ChildItem -Path C:\Users\Carlo\Documentos\notas -File | Measure-Object -Property Length -Sum", false),
                    falhou: false, detalhe: null,
                    saidaBruta: "Count    : 12\nAverage  :\nSum      : 38912\nMaximum  :\nMinimum  :\nProperty : Length",
                    resumo: "saída: 6 linhas");

                var painel = new SidePanelWindow();
                var metodo = typeof(SidePanelWindow).GetMethod("TooltipEmBloco",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

                var tooltip = (System.Windows.Controls.ToolTip)metodo.Invoke(painel, new object[] { comando })!;
                var pilha = (System.Windows.Controls.StackPanel)tooltip.Content;

                pilha.Width.Should().Be(340, "largura FIXA (D9), e não teto");
                pilha.Children.Count.Should().Be(3, "comando, resultado e saída bruta");

                DesenharSolto(tooltip, "tooltip-bloco", 380, 320);

                var busca = ActionLogService.Construir(
                    "glob", artefato: null, falhou: false, detalhe: null,
                    saidaBruta: null, argumento: @"*.html em C:\temp\emails fisio", resumo: "2 arquivos");

                var tooltipBusca = (System.Windows.Controls.ToolTip)metodo.Invoke(painel, new object[] { busca })!;
                DesenharSolto(tooltipBusca, "tooltip-bloco-busca", 380, 160);

                var edicao = ActionLogService.Construir(
                    "edit",
                    new Artifact(ArtifactKind.FileWritten, "edit",
                                 @"C:\temp\emails fisio\email carlos.html", false, "+2 linhas, −1 linha"),
                    falhou: false, detalhe: null, saidaBruta: null,
                    troca: new TrocaDeTexto(
                        "\t\t\t\t<td><span style=\"font-size: 12px\">Carlos Silva</span></td>",
                        "\t\t\t\t<td><span style=\"font-size: 12px\">Carlos H. Souza</span></td>\n"
                        + "\t\t\t\t\t<td>carlos@exemplo.com</td>"));

                var tooltipEdicao = (System.Windows.Controls.ToolTip)metodo.Invoke(painel, new object[] { edicao })!;
                ((System.Windows.Controls.StackPanel)tooltipEdicao.Content).Children.Count
                    .Should().Be(3, "caminho, resultado e antes e depois");

                DesenharSolto(tooltipEdicao, "tooltip-bloco-edicao", 380, 230);

                painel.Close();
            });
        }

        [Fact]
        public void CardDeConfirmacao_MostraOComandoExatoEDesenha()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                var card = new ConfirmCardView();
                card.Preencher(new CommandConfirmationContext
                {
                    Tool = "shell",
                    Command = "Remove-Item -Recurse -Force C:\\Users\\Carlo\\CPAPS\\AIB\\bin",
                    Level = 5,
                    Cwd = "C:\\Users\\Carlo\\CPAPS\\AIB"
                });

                var alvo = (System.Windows.Controls.TextBlock)card.FindName("AlvoText");
                alvo.Text.Should().Be("Remove-Item -Recurse -Force C:\\Users\\Carlo\\CPAPS\\AIB\\bin",
                    "o card mostra o comando EXATO que vai rodar");

                DesenharSolto(card, "card-confirmacao", 620, 260);
            });
        }

        [Fact]
        public void CardDeConfirmacao_ComEmailNoContexto_AvisaESomeOSemprePermitir()
        {
            // O texto de um e-mail lido na conversa pode trazer instruções escritas por
            // terceiros. O card é a única barreira, e precisa dizer isso.
            EmSta(() =>
            {
                GarantirRecursos();

                var card = new ConfirmCardView();
                card.Preencher(new CommandConfirmationContext
                {
                    Tool = "shell",
                    Command = "Remove-Item C:\\temp\\contrato.pdf",
                    Level = 5,
                    ConteudoDeEmailNoContexto = true
                });

                ((System.Windows.Controls.TextBlock)card.FindName("AvisoEmailText"))
                    .Visibility.Should().Be(Visibility.Visible);
                ((System.Windows.Controls.CheckBox)card.FindName("SempreCheck"))
                    .Visibility.Should().Be(Visibility.Collapsed,
                        "o portão não respeita o sempre permitir com e-mail no contexto");

                DesenharSolto(card, "card-confirmacao-email", 620, 300);
            });
        }

        [Fact]
        public void ItemDeEmail_AbertoComConversa_DesenhaODescarteAoLadoDoCliente()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                var item = new MailListItem
                {
                    DataContext = new MailSummary(
                        "Jurídico — contrato Vertex", "Pedem sua assinatura no aditivo até as 18h.",
                        MailUrgency.Maxima, Account: "eu@gmail.com", ThreadId: "123",
                        De: "juridico@vertex.com.br"),
                    EscalaDeJanela = true,
                    RealceLilas = true,
                    Aberto = true,
                    NomeDaInteligencia = "Kai",
                    RotuloDoCliente = "Abrir no Gmail",
                    TemConversa = true
                };

                DesenharSolto(item, "item-email-descartar", 560, 190);
            });
        }

        [Fact]
        public void CardDeConfirmacao_Descartado_DevolveRecusa()
        {
            // O card vive na lista de mensagens: limpar a conversa apaga o elemento da tela, e
            // quem espera a resposta precisa receber recusa em vez de esperar para sempre.
            EmSta(() =>
            {
                GarantirRecursos();

                var card = new ConfirmCardView();
                card.Preencher(new CommandConfirmationContext { Tool = "shell", Command = "x" });

                card.Resposta.IsCompleted.Should().BeFalse();
                card.Descartar();

                card.Resposta.IsCompleted.Should().BeTrue();

                // xUnit1031 avisa contra ler .Result, e está certo no caso geral. Aqui não há
                // o que esperar: a linha acima acabou de exigir IsCompleted, e este corpo roda
                // dentro de um Action numa thread STA, onde não existe await para dar.
#pragma warning disable xUnit1031
                card.Resposta.Result.Allowed.Should().BeFalse("descarte nunca autoriza");
#pragma warning restore xUnit1031
            });
        }

        [Fact]
        public void PainelLateral_AsTresAbasMontamEDesenham()
        {
            EmSta(() =>
            {
                GarantirRecursos();

                ContextService.Clear();
                ActionLogService.Clear();

                // Arquivos: um criado pela IA, um lido, um anexado pelo usuário.
                //
                // Caminhos INVENTADOS, e não arquivos reais desta máquina. O que este ensaio
                // mede é o desenho da linha — ícone pelo tipo, pill pela origem, reticencias no
                // meio do caminho — e nada disso depende de o arquivo existir. Apontar para
                // arquivo de verdade fazia o ensaio depender do disco de quem roda: um deles
                // era .planning\MEMORIA-HIERARQUICA.md, que deixou de existir.
                ContextService.AddFile(@"C:\projeto\src\Ui\ShrinkWrap.cs",
                                       ContextOrigin.CreatedByAi);
                ContextService.AddFile(@"C:\projeto\docs\MEMORIA-HIERARQUICA.md",
                                       ContextOrigin.ReadByAi);
                ContextService.AddFile(@"C:\Users\alguem\Downloads\relatorio-de-erros.log",
                                       ContextOrigin.AttachedByUser);

                // Ações: escrita, leitura e uma falha.
                ActionLogService.Add(ActionLogService.Construir(
                    "write",
                    new Artifact(ArtifactKind.FileWritten, "write",
                                 @"C:\projeto\src\Ui\ShrinkWrap.cs", false, "2,1 KB"),
                    falhou: false, detalhe: null, saidaBruta: null));

                ActionLogService.Add(ActionLogService.Construir(
                    "read",
                    new Artifact(ArtifactKind.FileRead, "read",
                                 @"C:\projeto\docs\MEMORIA-HIERARQUICA.md", false),
                    falhou: false, detalhe: null, saidaBruta: null));

                ActionLogService.Add(ActionLogService.Construir(
                    "shell",
                    new Artifact(ArtifactKind.CommandRun, "shell",
                                 "dotnet test AIB.Tests --filter Categoria=Integracao", true,
                                 "conexão recusada"),
                    falhou: true, detalhe: "ERRO: 127.0.0.1:11434 recusou a conexão.",
                    saidaBruta: null));

                var painel = new SidePanelWindow();

                var abaArquivos = (System.Windows.Controls.RadioButton)painel.FindName("AbaArquivos");
                var abaAcoes = (System.Windows.Controls.RadioButton)painel.FindName("AbaAcoes");

                Desenhar(painel, "painel-historico", 350, 605);

                abaArquivos.IsChecked = true;
                Desenhar(painel, "painel-arquivos", 350, 605);

                abaAcoes.IsChecked = true;
                Desenhar(painel, "painel-acoes", 350, 605);

                painel.Close();

                ContextService.Clear();
                ActionLogService.Clear();
            });
        }


        [Fact]
        public async System.Threading.Tasks.Task CardDeConfirmacao_SaiDaConversaDepoisDeDecidido()
        {
            // O card é uma PERGUNTA, não uma mensagem. Respondida, ela sai: uma pergunta morta
            // ocupando espaço permanente empurra o que veio depois para longe, e numa conversa
            // com várias ações a lista vira uma pilha de formulários mortos.
            //
            // Nada se perde ao removê-lo: o autorizado vira ícone na cadeia de ações, e o
            // histórico de ações do painel guarda a linha inteira com o comando exato.
            System.Threading.Tasks.Task<(bool, bool)>? pergunta = null;
            ChatWindow? chat = null;
            System.Windows.Controls.Panel? lista = null;

            EmSta(() =>
            {
                GarantirRecursos();

                var servico = ServicoDescartavel();
                chat = new ChatWindow(ConversaDescartavel(servico), servico);
                lista = (System.Windows.Controls.Panel)chat.FindName("MessagesPanel");

                pergunta = chat.PerguntarConfirmacaoAsync(new CommandConfirmationContext
                {
                    Tool = "shell",
                    Command = "dotnet --version",
                    Level = 5
                });
            });

            // O card não aparece no mesmo instante: PerguntarConfirmacaoAsync começa por um
            // Dispatcher.InvokeAsync, que ENFILEIRA quando o chamador já está na thread de
            // interface. Em produção a chamada vem da execução da ferramenta, que roda fora
            // dela; aqui o segundo bloco é o que deixa a fila andar.
            EmSta(() =>
            {
                var card = AcharCard(lista!);
                card.Should().NotBeNull("o card entra na conversa ao perguntar");

                var permitir = (System.Windows.Controls.Button)card!.FindName("PermitirButton");
                permitir.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            });

            var resposta = await pergunta!;
            resposta.Item1.Should().BeTrue("o clique em Permitir autoriza");

            EmSta(() =>
            {
                AcharCard(lista!).Should().BeNull("respondido, o card sai da conversa");
                chat!.Close();
            });
        }

        private static ConfirmCardView? AcharCard(System.Windows.Controls.Panel lista)
        {
            foreach (var filho in lista.Children)
                if (filho is ConfirmCardView card) return card;

            return null;
        }

        [Fact]
        public void ConfirmacaoDestrutiva_Monta()
        {
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new ConfirmDialog();
                janela.Measure(new Size(520, 400));
                janela.Close();
            });
        }
    }
}
