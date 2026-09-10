using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As abas Ferramentas e Memória, e o que sobrou no Avançado.
    /// <para>
    /// O Avançado ficou, depois da aba Logs, com sete campos de dois assuntos que não se
    /// encostam: o que o agente PODE FAZER na máquina e o que a conversa LEMBRA. Separar não é
    /// arrumação de gaveta — a página de Ferramentas é onde o confinamento de gravação e a lista
    /// de "sempre permitir" passam a ter casa, e as duas são decisões de segurança que até agora
    /// não tinham tela nenhuma.
    /// </para>
    /// </summary>
    public class PaginasDeFerramentasEMemoriaTests
    {
        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-abas-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        /// <summary>
        /// Em QUAL página o campo mora.
        /// <para>
        /// <c>FindName</c> acha o controle esteja ele onde estiver, e por isso não prova
        /// colocação nenhuma. Subir a árvore até a Grid da página é o que transforma "o campo
        /// existe" em "o campo está onde eu disse que ia estar".
        /// </para>
        /// </summary>
        private static string PaginaDe(Window janela, string campo)
        {
            var atual = janela.FindName(campo) as DependencyObject;
            campo.Should().NotBeNull();
            atual.Should().NotBeNull($"o controle '{campo}' precisa existir");

            while (atual != null)
            {
                if (atual is FrameworkElement fe
                    && fe.Name.StartsWith("Pagina", StringComparison.Ordinal))
                    return fe.Name;

                atual = System.Windows.Media.VisualTreeHelper.GetParent(atual)
                        ?? LogicalTreeHelper.GetParent(atual);
            }

            return "(nenhuma)";
        }

        [Fact]
        public void ADeFerramentas_JuntaOQueOAgentePodeFazer()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Ferramentas);

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Ferramentas);
                ((Grid)janela.FindName("PaginaFerramentas")).Visibility.Should().Be(Visibility.Visible);

                PaginaDe(janela, "IntelligentToolsSwitch").Should().Be("PaginaFerramentas");
                PaginaDe(janela, "ConfirmDangerousSwitch").Should().Be("PaginaFerramentas");
                PaginaDe(janela, "MaxIterationsTextBox").Should().Be("PaginaFerramentas");
                PaginaDe(janela, "WriteRootsTextBox").Should().Be("PaginaFerramentas");
                PaginaDe(janela, "AutorizacoesTexto").Should().Be("PaginaFerramentas");

                janela.Close();
            });
        }

        [Fact]
        public void ADeMemoria_JuntaOQueAConversaLembra()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Memoria);

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Memoria);

                PaginaDe(janela, "CompactionTriggerTextBox").Should().Be("PaginaMemoria");
                PaginaDe(janela, "MemoryFractionTextBox").Should().Be("PaginaMemoria");
                PaginaDe(janela, "KeepAssistantSpeechSwitch").Should().Be("PaginaMemoria");

                // Mesmo gesto da aba Logs: uma configuração de memória que não diz ONDE a
                // memória mora obriga o usuário a procurar.
                ((TextBlock)janela.FindName("PastaDeMemoriaTexto")).Text.Should().Contain(".AIB");

                janela.Close();
            });
        }

        [Fact]
        public void OAvancado_FICOU_SoComORaciocinio()
        {
            // Sobraram os dois que de fato andam juntos: a ajuda de um diz "só faz diferença com
            // o raciocínio ligado". Separá-los em abas diferentes seria pior que a bagunça.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());

                PaginaDe(janela, "ModelThinkingSwitch").Should().Be("PaginaAvancado");
                PaginaDe(janela, "ThinkingInHistorySwitch").Should().Be("PaginaAvancado");

                janela.Close();
            });
        }

        [Fact]
        public void SemPastaConfigurada_ATelaDIZ_QueODiscoInteiroEstaLiberado()
        {
            // O padrão é permissivo, e um padrão permissivo que não se anuncia é o pior dos dois
            // mundos: o usuário abre a aba de segurança e sai dela sem saber o que vale.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Ferramentas);

                ((TextBlock)janela.FindName("PastasPermitidasTexto")).Text
                    .Should().Contain("disco inteiro");

                janela.Close();
            });
        }

        [Fact]
        public void ComPastaDigitada_ATela_MostraOQuePassouAValer()
        {
            // O campo é texto livre: uma linha com erro de digitação some da lista sem avisar, e
            // o usuário sairia achando que confinou a gravação quando não confinou.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Ferramentas);

                ((TextBox)janela.FindName("WriteRootsTextBox")).Text = @"C:\Trabalho";

                string ajuda = ((TextBlock)janela.FindName("PastasPermitidasTexto")).Text;
                ajuda.Should().Contain(@"C:\Trabalho");
                ajuda.Should().Contain("Ainda não existe", "pasta inexistente bloqueia gravação válida");

                janela.Close();
            });
        }

        [Fact]
        public void RestaurarPadroes_NaoAtravessa_AsAbasVizinhas()
        {
            // Cada botão mexe só na própria página. Restaurar as Ferramentas não pode zerar a
            // compactação, que agora mora noutra aba.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());

                var compactacao = (TextBox)janela.FindName("CompactionTriggerTextBox");
                compactacao.Text = "42";

                var botao = new Button { Tag = "Ferramentas" };
                janela.GetType()
                      .GetMethod("Restaurar_Click", System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Instance)!
                      .Invoke(janela, new object[] { botao, new RoutedEventArgs() });

                compactacao.Text.Should().Be("42");
                ((TextBox)janela.FindName("WriteRootsTextBox")).Text.Should().BeEmpty();

                janela.Close();
            });
        }
    }
}
