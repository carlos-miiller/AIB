using System;
using System.IO;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A lista de arquivos do contexto — §6.2.
    /// <para>
    /// Estes testes existem porque o serviço era casca: <c>ActiveFiles</c> devolvia uma lista
    /// NOVA e vazia a cada chamada e <c>AddFile</c> não fazia nada. A aba do painel nunca mostrou
    /// um arquivo, o arrastar-e-soltar não guardava, e o registro de arquivo recente que o laço
    /// do agente chama a cada leitura era descartado. Nada disso acusava: parecia funcionalidade
    /// e não era.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class ContextServiceTests : IDisposable
    {
        // O serviço é estático porque o painel, o chat e o laço do agente precisam da MESMA
        // lista. Cada ensaio limpa antes e depois para não herdar o estado do anterior.
        public ContextServiceTests() => ContextService.Clear();

        public void Dispose() => ContextService.Clear();

        private static string Caminho(string nome) =>
            Path.Combine(Path.GetTempPath(), "aib-ctx", nome);

        [Fact]
        public void ArquivoAdicionado_FicaNaLista()
        {
            ContextService.AddFile(Caminho("a.txt"));

            ContextService.ActiveFiles.Should().HaveCount(1);
            ContextService.ActiveFiles[0].Name.Should().Be("a.txt");
        }

        [Fact]
        public void ALista_EhAMesmaEntreChamadas()
        {
            // O defeito original em uma linha: cada leitura devolvia uma coleção nova, então o
            // que uma parte do app guardava a outra nunca via.
            var primeira = ContextService.ActiveFiles;
            ContextService.AddFile(Caminho("b.txt"));

            ReferenceEquals(primeira, ContextService.ActiveFiles).Should().BeTrue();
            primeira.Should().HaveCount(1);
        }

        [Fact]
        public void MesmoArquivoDuasVezes_NaoDuplica_ESobeParaOTopo()
        {
            ContextService.AddFile(Caminho("primeiro.txt"));
            ContextService.AddFile(Caminho("segundo.txt"));
            ContextService.AddFile(Caminho("primeiro.txt"));

            ContextService.ActiveFiles.Should().HaveCount(2);
            ContextService.ActiveFiles[0].Name.Should().Be("primeiro.txt",
                "encostar de novo traz o arquivo para o topo");
        }

        [Fact]
        public void OrigemDaIa_VenceAOrigemMaisFraca()
        {
            // Ler e depois gravar o mesmo arquivo: quem escreveu fez mais do que abrir.
            ContextService.AddFile(Caminho("c.cs"), ContextOrigin.ReadByAi);
            ContextService.AddFile(Caminho("c.cs"), ContextOrigin.CreatedByAi);

            ContextService.ActiveFiles[0].Origin.Should().Be(ContextOrigin.CreatedByAi);
            ContextService.ActiveFiles[0].OriginLabel.Should().Be("criado");
        }

        [Fact]
        public void OrigemMaisFraca_NaoRebaixaAMaisForte()
        {
            ContextService.AddFile(Caminho("d.cs"), ContextOrigin.CreatedByAi);
            ContextService.AddFile(Caminho("d.cs"), ContextOrigin.ReadByAi);

            ContextService.ActiveFiles[0].Origin.Should().Be(ContextOrigin.CreatedByAi);
        }

        [Fact]
        public void CaminhoRelativo_ViraAbsoluto()
        {
            // O caminho é literal e vai para o Explorer. Relativo abriria a pasta errada.
            ContextService.AddFile("arquivo-relativo.txt");

            Path.IsPathRooted(ContextService.ActiveFiles[0].FilePath).Should().BeTrue();
        }

        [Fact]
        public void CaminhoVazio_EhIgnorado()
        {
            ContextService.AddFile("");
            ContextService.AddFile("   ");
            ContextService.AddFile(null);

            ContextService.ActiveFiles.Should().BeEmpty();
        }

        [Fact]
        public void CaminhoEstranho_NaoDerrubaALista()
        {
            // O contrato aqui é NÃO EXPLODIR, e não filtrar. Path.GetFullPath do .NET 8 aceita
            // caracteres que nenhum sistema de arquivos aceitaria — a validação de verdade só
            // acontece na operação de disco. Filtrar por conta própria arriscaria recusar
            // caminho legítimo (UNC, caminho longo) para ganhar pouco: o clique no item já
            // confere existência antes de abrir o Explorer, e pelo caminho real só entram
            // arquivos de ações que deram certo.
            var acao = () => ContextService.AddFile("C:\\<>:\"|?*\\impossivel.txt");

            acao.Should().NotThrow();
        }

        [Fact]
        public void Remover_TiraDoContexto()
        {
            ContextService.AddFile(Caminho("e.txt"));
            var arquivo = ContextService.ActiveFiles[0];

            ContextService.RemoveFile(arquivo);

            ContextService.ActiveFiles.Should().BeEmpty();
        }

        [Fact]
        public void ArquivoQueNaoExiste_TemTamanhoZeroSemExplodir()
        {
            ContextService.AddFile(Caminho("nao-existe-mesmo.txt"));

            ContextService.ActiveFiles[0].SizeBytes.Should().Be(0);
            ContextService.TotalBytes().Should().Be(0);
        }

        [Fact]
        public void PillDeOrigem_DistingueIaDeUsuario()
        {
            ContextService.AddFile(Caminho("f.txt"), ContextOrigin.AttachedByUser);
            ContextService.AddFile(Caminho("g.txt"), ContextOrigin.ReadByAi);

            var doUsuario = ContextService.ActiveFiles.First(a => a.Name == "f.txt");
            var daIa = ContextService.ActiveFiles.First(a => a.Name == "g.txt");

            doUsuario.ByAi.Should().BeFalse();
            doUsuario.OriginLabel.Should().Be("anexado");
            daIa.ByAi.Should().BeTrue();
            daIa.OriginLabel.Should().Be("lido");
        }

        [Fact]
        public void Recentes_NaoPassamDoTeto()
        {
            for (int i = 0; i < ContextService.MaxRecentes + 12; i++)
                ContextService.AddRecentFile(Caminho($"r{i}.txt"));

            ContextService.RecentFiles.Should().HaveCount(ContextService.MaxRecentes);
            ContextService.RecentFiles[0].Name.Should().Be($"r{ContextService.MaxRecentes + 11}.txt",
                "o mais recente fica no topo");
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(2048, "2 KB")]
        [InlineData(1572864, "1,5 MB")]
        public void TamanhoLegivel(long bytes, string esperado)
        {
            ContextService.Humanizar(bytes).Should().Be(esperado);
        }
    }

    /// <summary>
    /// A conversa nova pela janela: o que ela leva e o que ela deixa para trás.
    /// <para>
    /// Na coleção ContextoGlobal porque <c>NovaConversa</c> limpa <c>ContextService</c> e
    /// <c>ActionLogService</c>, que são estáticos — ver a nota da coleção.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class ConversaNovaNaJanelaTests
    {
        private static T Achar<T>(System.Windows.DependencyObject janela, string nome) where T : class
            => (T)((System.Windows.FrameworkElement)janela).FindName(nome)!;

        private static void Clicar(System.Windows.Controls.Button botao) =>
            botao.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        [Fact]
        public void ConversaNova_TIRA_OsAnexosDaAnterior()
        {
            // Os anexos são "desta conversa", mas só os ensaios limpavam a lista: depois de uma
            // conversa nova eles continuavam lá e entravam no prompt da seguinte
            // (RenderizarAnexados), sobre um assunto em que ninguém os anexou.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                ContextService.Clear();

                var janela = JanelaDeEnsaio.Nova();

                ContextService.AddFile(@"C:\projeto\anexo-da-conversa-velha.txt");
                ContextService.AddRecentFile(@"C:\projeto\lido-pela-ia.txt");
                ContextService.RenderizarAnexados().Should().NotBeEmpty();

                Clicar(Achar<System.Windows.Controls.Button>(janela, "ClearButton"));

                ContextService.ActiveFiles.Should().BeEmpty();
                ContextService.RenderizarAnexados().Should().BeEmpty();
                ContextService.RecentFiles.Should().NotBeEmpty("os recentes são histórico, não estado");

                janela.Close();
                ContextService.Clear();
            });
        }

        [Fact]
        public void TrocarDePersonagem_COMECA_ConversaNova_PeloCaminhoInteiro()
        {
            // O prompt de sistema é do personagem: com ele trocado a conversa recomeça — e por
            // NovaConversa, não pela cópia parcial que esquecia o título, o contador e os anexos.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var servico = JanelaDeEnsaio.Servico();
                var janela = new AIB.Views.ChatWindow(JanelaDeEnsaio.Conversa(servico), servico);

                var mensagens = Achar<System.Windows.Controls.StackPanel>(janela, "MessagesPanel");
                mensagens.Children.Add(new System.Windows.Controls.TextBlock { Text = "uma fala" });
                Achar<System.Windows.Controls.TextBlock>(janela, "ChatTitleText").Text = "Conversa em andamento";
                ContextService.AddFile(@"C:\projeto\anexo.txt");

                var config = servico.LoadSettings();
                config.ActiveCharacter = config.ActiveCharacter + "-outro";
                servico.SaveSettings(config);

                janela.ApplyCharacterUI();

                mensagens.Children.Count.Should().Be(0);
                Achar<System.Windows.Controls.TextBlock>(janela, "ChatTitleText").Text.Should().Be("Nova conversa");
                Achar<System.Windows.Controls.TextBlock>(janela, "AgentNameText").Text
                    .Should().Be(config.ActiveCharacter.ToUpper());
                ContextService.ActiveFiles.Should().BeEmpty();

                janela.Close();
                ContextService.Clear();
            });
        }
    }

    /// <summary>
    /// Os serviços de painel são ESTÁTICOS e compartilhados, e o xUnit roda classes em
    /// paralelo. Sem esta coleção uma classe limpa a lista da outra no meio do ensaio.
    /// <para>
    /// Entra aqui TODA classe que escreve em <c>ContextService</c> ou <c>ActionLogService</c> —
    /// e escrever inclui limpar. Hoje: esta, ActionLogServiceTests, SkillActionDisplayTests,
    /// ModalGuardTests, AttachedFilesPromptTests, WindowSmokeTests e ConversationServiceTests.
    /// </para>
    /// <para>
    /// AttachedFilesPromptTests morava numa coleção chamada "ContextService", que NUNCA foi
    /// definida. O xUnit não reclama disso: ele cria uma coleção ad hoc com esse nome, e como
    /// nenhuma outra classe a usava, o isolamento era de uma classe consigo mesma. Ela rodava
    /// em paralelo com exatamente quem deveria evitar.
    /// </para>
    /// </summary>
    [CollectionDefinition("ContextoGlobal", DisableParallelization = true)]
    public class ContextoGlobalCollection { }
}
