using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O segredo colado na caixa de mensagem, antes de enviar.
    /// <para>
    /// Pedido do usuário (20/10/2026): "vamos tentar detectar antes de enviar, e exibir um alerta
    /// na tela". Caso que motivou: o webhook do Bitrix colado no chat ia ao provedor do modelo e
    /// ficava no histórico, e o filtro de segredos só olha a saída das ferramentas.
    /// </para>
    /// </summary>
    public class SegredoNaMensagemTests
    {
        private const string Webhook = "https://cpaps.bitrix24.com.br/rest/1/k3x9q2v7m1p8z4t6/";

        private static Cofre Novo() =>
            new(Path.Combine(Path.GetTempPath(), "AIB_SegredoMsg_" + Guid.NewGuid().ToString("N")));

        // ── detector ─────────────────────────────────────────────────────────

        [Fact]
        public void WebhookDoBitrix_EhAchado_ComNomeEHostSugeridos()
        {
            var a = SegredoNaMensagem.Achar($"usa esse aqui: {Webhook} por favor").Single();

            a.Trecho.Should().Be(Webhook);
            a.Nome.Should().Be("bitrix");
            a.Host.Should().Be("cpaps.bitrix24.com.br");
            a.Tipo.Should().Contain("webhook");
        }

        [Theory]
        [InlineData("a chave é sk-or-v1-abcdefghijklmnopqrstuvwxyz0123", "sk-or-v1-abcdefghijklmnopqrstuvwxyz0123")]
        [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
        [InlineData("DB_PASSWORD=Tr0ub4dor3 no .env", "Tr0ub4dor3")]
        [InlineData("https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX", "https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX")]
        public void FormatosConhecidos_SaoAchados(string texto, string trecho)
        {
            SegredoNaMensagem.Achar(texto).Select(a => a.Trecho).Should().Contain(trecho);
        }

        [Theory]
        [InlineData("pode me escrever um texto perguntando se seria possivel uma reunião amanhã?")]
        [InlineData("abre https://cpaps.bitrix24.com.br/workgroups/ e vê a tarefa 42")]
        [InlineData("o código faz password=self.password)")]
        public void TextoComum_NaoEhAchado(string texto)
        {
            SegredoNaMensagem.Achar(texto).Should().BeEmpty();
        }

        [Fact]
        public void ValorQueJaEstaNoCofre_ViraONome_SemAlerta()
        {
            var c = Novo();
            c.Guardar("bitrix", Webhook, "Webhook REST do Bitrix", new[] { "cpaps.bitrix24.com.br" });

            var (texto, trocados, achados) = SegredoNaMensagem.Conferir($"usa {Webhook}", c);

            texto.Should().Be("usa [cofre: bitrix]");
            trocados.Should().Equal("bitrix");
            achados.Should().BeEmpty();
        }

        [Fact]
        public void TrocarPeloNome_TrocaSoOTrecho()
        {
            string msg = $"usa {Webhook} agora";
            var a = SegredoNaMensagem.Achar(msg).Single();
            SegredoNaMensagem.TrocarPeloNome(msg, a, "bitrix").Should().Be("usa [cofre: bitrix] agora");
        }

        [Fact]
        public void Trechos_ApontaOValor_ComoORedigirTrocaria()
        {
            string texto = "DB_PASSWORD=Tr0ub4dor3 e Authorization: Bearer abcdefghijklmnopqrstu";
            var t = Segredos.Trechos(texto);

            t.Select(x => texto.Substring(x.Inicio, x.Tamanho)).Should().Equal("Tr0ub4dor3", "abcdefghijklmnopqrstu");
            Segredos.Redigir(texto, out int n);
            n.Should().Be(t.Count);
        }

        // ── janela de chat ───────────────────────────────────────────────────

        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static void Enviar(ChatWindow j) =>
            typeof(ChatWindow).GetMethod("SendButton_Click", Privados)!.Invoke(j, new object?[] { j, new RoutedEventArgs() });

        private static void Clicar(ChatWindow j, string nome) =>
            typeof(ChatWindow).GetMethod(nome + "_Click", Privados)!.Invoke(j, new object?[] { j, new RoutedEventArgs() });

        private static T Achar<T>(FrameworkElement j, string nome) where T : class => (T)j.FindName(nome);

        [Fact]
        public void Chat_WebhookColado_SeguraOEnvio_EGuardarEnviaComONome()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();
                var cofre = Novo();
                typeof(ChatWindow).GetProperty("CofreDaMensagem", Privados)!.SetValue(janela, cofre);

                var input = Achar<TextBox>(janela, "InputBox");
                input.Text = $"usa esse aqui: {Webhook}";
                Enviar(janela);

                Achar<Border>(janela, "AlertaDeSegredo").Visibility.Should().Be(Visibility.Visible);
                input.Text.Should().Contain(Webhook, "nada foi enviado");
                Achar<TextBox>(janela, "AlertaNome").Text.Should().Be("bitrix");
                Achar<TextBox>(janela, "AlertaHosts").Text.Should().Be("cpaps.bitrix24.com.br");

                Clicar(janela, "AlertaGuardar");

                cofre.Achar("bitrix")!.Hosts.Should().Equal("cpaps.bitrix24.com.br");
                cofre.Redigir(Webhook).Should().Be("[cofre: bitrix]");
                Achar<Border>(janela, "AlertaDeSegredo").Visibility.Should().Be(Visibility.Collapsed);
                input.Text.Should().BeEmpty("a mensagem saiu, com o nome no lugar do valor");

                janela.Close();
            });
        }

        [Fact]
        public void Chat_EnviarMesmoAssim_Envia()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();
                typeof(ChatWindow).GetProperty("CofreDaMensagem", Privados)!.SetValue(janela, Novo());

                var input = Achar<TextBox>(janela, "InputBox");
                input.Text = "testa a chave revogada sk-or-v1-abcdefghijklmnopqrstuvwxyz0123";
                Enviar(janela);
                input.Text.Should().NotBeEmpty();

                Clicar(janela, "AlertaEnviar");

                input.Text.Should().BeEmpty();
                Achar<Border>(janela, "AlertaDeSegredo").Visibility.Should().Be(Visibility.Collapsed);

                janela.Close();
            });
        }

        // ── barra do orbe ────────────────────────────────────────────────────

        [Fact]
        public void Orbe_PrimeiroEnterAvisa_SegundoEnvia()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var barra = new ShadowAssistantWindow();
                typeof(ShadowAssistantWindow).GetProperty("CofreDaMensagem", Privados)!.SetValue(barra, Novo());
                string? enviada = null;
                barra.MensagemEnviada += t => enviada = t;

                Achar<TextBox>(barra, "Campo").Text = $"usa {Webhook}";
                barra.Enviar();

                enviada.Should().BeNull();
                Achar<TextBlock>(barra, "AvisoDeSegredo").Visibility.Should().Be(Visibility.Visible);

                barra.Enviar();
                enviada.Should().Be($"usa {Webhook}");
                Achar<TextBlock>(barra, "AvisoDeSegredo").Visibility.Should().Be(Visibility.Collapsed);

                barra.Close();
            });
        }

        [Fact]
        public void Orbe_ValorDoCofre_EnviaComONome_SemAviso()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var barra = new ShadowAssistantWindow();
                var cofre = Novo();
                cofre.Guardar("bitrix", Webhook, "Webhook REST do Bitrix", new[] { "cpaps.bitrix24.com.br" });
                typeof(ShadowAssistantWindow).GetProperty("CofreDaMensagem", Privados)!.SetValue(barra, cofre);
                string? enviada = null;
                barra.MensagemEnviada += t => enviada = t;

                Achar<TextBox>(barra, "Campo").Text = $"usa {Webhook}";
                barra.Enviar();

                enviada.Should().Be("usa [cofre: bitrix]");
                barra.Close();
            });
        }
    }
}
