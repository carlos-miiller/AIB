using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Mail;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A tela de configurações sem desfazer, calada, o que está valendo: um número fora da lista,
    /// o keep-alive desconhecido, o modelo do OpenRouter ao restaurar.
    /// <para>
    /// Na coleção ContextoGlobal porque mexe em estado estático — o catálogo do OpenRouter, posto
    /// vazio para a tela não ir à rede, e os valores em vigor que o Salvar configura.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class TelaConfiguracoesProvedorTests : IDisposable
    {
        // O Salvar configura os valores em vigor; o ensaio devolve os de antes.
        private readonly int _janela = ChatRequestOptions.JanelaAtual;
        private readonly int _janelaDoOllama = ChatRequestOptions.JanelaDoOllama;
        private readonly int _keepAlive = ChatRequestOptions.KeepAliveAtual;
        private readonly LimitesDoProvedor _limites = LimitesDoProvedor.Atual;

        public TelaConfiguracoesProvedorTests()
        {
            // Catálogo vazio mas PRESENTE: a tela do OpenRouter lê dele e não vai à rede.
            CatalogoDoOpenRouter.DefinirCache(Array.Empty<ModeloDoOpenRouter>());
        }

        public void Dispose()
        {
            CatalogoDoOpenRouter.DefinirCache(null);
            ChatRequestOptions.JanelaAtual = _janela;
            ChatRequestOptions.JanelaDoOllama = _janelaDoOllama;
            ChatRequestOptions.KeepAliveAtual = _keepAlive;
            LimitesDoProvedor.Atual = _limites;
        }

        private static T Achar<T>(SettingsWindow j, string nome) where T : class => (T)j.FindName(nome)!;

        /// <summary>Tela sobre um arquivo descartável — nunca o profile.dat real.</summary>
        private static (SettingsWindow janela, string caminho) Nova(PaginaDeConfiguracoes pagina,
                                                                   Action<UserAppSettings> preparar)
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-cfg-prov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            string caminho = Path.Combine(pasta, "settings.json");

            var servico = new SettingsService(caminho);
            var s = servico.LoadSettings();
            s.PerfisMigrados = true;
            preparar(s);
            servico.SaveSettings(s.Sanear());

            var janela = new SettingsWindow(servico, pagina, new MailServiceStub(),
                                            new MailVault(pasta), new EstadoDasCaixas(pasta));
            return (janela, caminho);
        }

        private static void Salvar(SettingsWindow janela) =>
            Achar<Button>(janela, "SaveButton").RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        private static void Restaurar(SettingsWindow janela, string pagina) =>
            typeof(SettingsWindow)
                .GetMethod("Restaurar_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(janela, new object[] { new Button { Tag = pagina }, new RoutedEventArgs() });

        [Fact]
        public void NumeroForaDaLista_GANHA_UmItem_EOSalvarNaoOZera()
        {
            // O arquivo aceita 2 a 24 capítulos por ato e 2 a 50 turnos; a lista oferece alguns.
            // Um 6 caía em "Automático", e o próximo Salvar — de qualquer página — gravava 0.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, caminho) = Nova(PaginaDeConfiguracoes.Memoria, s =>
                {
                    s.AiProvider = ProvedoresDeIa.Ollama;
                    s.CapitulosPorAto = 6;
                    s.EsconderResultadosDepoisDe = 10;
                });

                var porAto = Achar<ComboBox>(janela, "CapitulosPorAtoComboBox");
                var esconder = Achar<ComboBox>(janela, "EsconderResultadosComboBox");

                var item = porAto.SelectedItem.Should().BeOfType<ComboBoxItem>().Subject;
                item.Tag.Should().Be("6");
                item.Content.Should().Be("6", "no formato dos outros números da lista");

                var outro = esconder.SelectedItem.Should().BeOfType<ComboBoxItem>().Subject;
                outro.Tag.Should().Be("10");
                outro.Content.Should().Be("Depois de 10 turnos");

                // Na ordem: o 6 entre o 4 e o 8.
                porAto.Items.Cast<ComboBoxItem>().Select(i => i.Tag?.ToString())
                    .Should().ContainInOrder("4", "6", "8");

                // Um Salvar por causa de OUTRA coisa não pode levar o número junto.
                Achar<TextBox>(janela, "MaxIterationsTextBox").Text = "20";
                Salvar(janela);

                var gravado = new SettingsService(caminho).LoadSettings();
                gravado.CapitulosPorAto.Should().Be(6);
                gravado.EsconderResultadosDepoisDe.Should().Be(10);
            });
        }

        [Fact]
        public void NumeroDaLista_NaoDuplicaItem()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _) = Nova(PaginaDeConfiguracoes.Memoria, s => s.CapitulosPorAto = 8);

                var porAto = Achar<ComboBox>(janela, "CapitulosPorAtoComboBox");
                porAto.Items.Cast<ComboBoxItem>().Count(i => i.Tag?.ToString() == "8").Should().Be(1);
                ((ComboBoxItem)porAto.SelectedItem).Tag.Should().Be("8");
                janela.Close();
            });
        }

        [Fact]
        public void KeepAliveDesconhecido_CaiNoSempreCarregado()
        {
            // O padrão é -1 e a tela o recomenda; cair em "5 Minutos" trocava o padrão no Salvar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _) = Nova(PaginaDeConfiguracoes.Conexao, s => s.AiProvider = ProvedoresDeIa.Ollama);

                typeof(SettingsWindow)
                    .GetMethod("SelecionarKeepAlive", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(janela, new object?[] { "valor-que-nao-existe" });

                ((ComboBoxItem)Achar<ComboBox>(janela, "KeepAliveComboBox").SelectedItem).Tag.Should().Be("-1");
                janela.Close();
            });
        }

        [Fact]
        public void RestaurarNaConexao_DoOpenRouter_MANTEM_OModelo()
        {
            // O OpenRouter não tem modelo de fábrica. Restaurar trocava o perfil pelo padrão, de
            // modelo vazio, e a conversa ficava sem ter com quem falar.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (janela, _) = Nova(PaginaDeConfiguracoes.Conexao, s =>
                {
                    s.AiProvider = ProvedoresDeIa.OpenRouter;
                    // Sem barra de propósito: com barra a tela consulta os provedores do modelo na
                    // rede, e ensaio não depende de rede.
                    s.ModelName = "modelo-escolhido";
                    s.ContextWindow = 65536;
                });

                Achar<TextBox>(janela, "JanelaTextBox").Text.Should().Be("65536");

                Restaurar(janela, "Conexao");

                Achar<ComboBox>(janela, "OpenRouterModelComboBox").Text.Should().Be("modelo-escolhido");
                Achar<TextBox>(janela, "JanelaTextBox").Text
                    .Should().Be(PerfilDeProvedor.JanelaPadrao.ToString(), "o resto do perfil volta ao padrão");
                janela.Close();
            });
        }
    }
}
