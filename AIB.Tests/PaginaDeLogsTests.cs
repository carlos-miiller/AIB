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
    /// A aba Logs, e a arrumação que veio com ela.
    /// <para>
    /// O Avançado tinha treze campos de assuntos diferentes: teto de contexto, keep-alive,
    /// ferramentas, raciocínio, compactação e os dois de log. Uma página que junta tudo o que
    /// não coube nas outras deixa de ser uma página e vira um depósito.
    /// </para>
    /// </summary>
    public class PaginaDeLogsTests
    {
        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-logs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        [Fact]
        public void ADeLogs_EXISTE_ETemOsDoisRegistros()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Logs);

                janela.PaginaAtiva.Should().Be(PaginaDeConfiguracoes.Logs);
                ((Grid)janela.FindName("PaginaLogs")).Visibility.Should().Be(Visibility.Visible);
                ((Grid)janela.FindName("PaginaAvancado")).Visibility.Should().Be(Visibility.Collapsed);

                janela.FindName("VerboseLoggingSwitch").Should().NotBeNull();
                janela.FindName("ExecutionLogSwitch").Should().NotBeNull();

                // O diário da compactação: a chave mora aqui, com as outras de registro, mas o
                // ARQUIVO vai para a pasta da sessão — separá-lo da conversa que ele compactou
                // obrigaria a cruzar horário à mão.
                janela.FindName("CompactionLogSwitch").Should().NotBeNull();

                // Uma chave que grava em disco e não diz ONDE obriga o usuário a procurar.
                ((TextBlock)janela.FindName("PastaDeLogsTexto")).Text
                    .Should().Contain(".AIB");

                janela.Close();
            });
        }

        [Fact]
        public void OQueEhDaCONEXAO_MudouDePagina()
        {
            // Keep-alive e "enviar system prompt" configuram a conversa com o MODELO, e moravam
            // no Avançado — longe do campo onde se escolhe o modelo que eles afetam.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.IrPara(PaginaDeConfiguracoes.Conexao);

                var conexao = (Grid)janela.FindName("PaginaConexao");
                conexao.Visibility.Should().Be(Visibility.Visible);

                janela.FindName("KeepAliveComboBox").Should().NotBeNull();
                janela.FindName("SendSystemPromptSwitch").Should().NotBeNull();
                janela.FindName("MaxHistoryTextBox").Should().NotBeNull();

                janela.Close();
            });
        }
    }
}
